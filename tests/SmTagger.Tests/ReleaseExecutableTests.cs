using System.Diagnostics;
using System.Security.Cryptography;
using SmTagger.Engine;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ReleaseExecutableTests
{
    // Exercises both actual published executables through sorter diversion and tagger fan-out.
    [ReleaseFact]
    public async Task PublishedExecutablesProcessAnEnrolledMessageEndToEnd()
    {
        using var fixture = new ProcessorFixture();
        var originals = fixture.WriteMessage("-42", ProcessorFixture.Header("bob@example.net,alice@example.net"));
        MoveToSorter(fixture, "-42");
        var sorter = await RunAsync("sm-sorter", fixture, [fixture.DataDirectory, fixture.SpoolDirectory, "-42"]);
        Assert.Equal(0, sorter.ExitCode);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-42.hdr")));
        var tagger = await RunAsync("sm-tagger", fixture,
            [fixture.DataDirectory, "-keep", "-log", fixture.SpoolDirectory, "-42"]);
        Assert.Equal(0, tagger.ExitCode);
        Assert.Equal("", tagger.Error);
        Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.hdr").Length);
        using var loaded = fixture.Open();
        Assert.Equal(3, loaded.Tags.Mappings.Count);
        Assert.Contains("hdrMarker=\"Written \"", File.ReadAllText(Path.Combine(fixture.DataDirectory, "log.txt")));
    }

    // Actual release processes enforce the agreed From retention and one-shot exit status.
    [ReleaseFact]
    public async Task PublishedTaggerRejectsFromCountsAndContinuesAfterLoggingFailure()
    {
        using var fixture = new ProcessorFixture();
        Directory.CreateDirectory(Path.Combine(fixture.DataDirectory, "log.txt"));
        fixture.WriteMessage("invalid", eml: ProcessorFixture.Message(""));
        var failed = await RunAsync("sm-tagger", fixture,
            [fixture.DataDirectory, "-log", fixture.SpoolDirectory, "invalid"]);
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("exactly one From", failed.Error);
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "invalid.hdr.err")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "invalid.eml.err")));
        fixture.WriteMessage("valid");
        var succeeded = await RunAsync("sm-tagger", fixture,
            [fixture.DataDirectory, "-log", fixture.SpoolDirectory, "valid"]);
        Assert.Equal(0, succeeded.ExitCode);
        Assert.Contains("ERROR logging to", succeeded.Error);
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "valid-1.hdr")));
        Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, "valid.err")));
    }

    // A failed tagger configuration does not block the independent sorter's no-auth pass-through.
    [ReleaseFact]
    public async Task TaggerFailureLeavesSorterPassThroughAvailable()
    {
        using var fixture = new ProcessorFixture();
        File.Delete(Path.Combine(fixture.ProfileDirectory, "private-address.txt"));
        fixture.WriteMessage("enrolled");
        MoveToSorter(fixture, "enrolled");
        Assert.Equal(0, (await RunAsync("sm-sorter", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "enrolled"])).ExitCode);
        Assert.Equal(1, (await RunAsync("sm-tagger", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "enrolled"])).ExitCode);
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "enrolled.hdr")));
        var inbound = fixture.WriteMessage("inbound", ProcessorFixture.Header().Replace("auth: auth@example.com\r\n", ""));
        MoveToSorter(fixture, "inbound");
        Assert.Equal(0, (await RunAsync("sm-sorter", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "inbound"])).ExitCode);
        Assert.Equal(inbound.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "inbound.eml")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "enrolled.hdr")));
    }

    // Both actual process roles exclude a second owner while allowing the other role to run independently.
    [ReleaseFact]
    public async Task PublishedWatchersEnforceIndependentSingletons()
    {
        using var fixture = new ProcessorFixture();
        using var sorter = Start("sm-sorter", fixture, [fixture.DataDirectory, fixture.SpoolDirectory]);
        using var tagger = Start("sm-tagger", fixture, [fixture.DataDirectory, fixture.SpoolDirectory]);
        await WaitUntilAsync(() => OwnsLock(Path.Combine(fixture.SpoolDirectory, "proc", "sm-sorter.lock")), sorter);
        await WaitUntilAsync(() => OwnsLock(Path.Combine(fixture.DataDirectory, "sm-tagger.lock")), tagger);
        fixture.WriteMessage("queued");
        var secondSorter = await RunAsync("sm-sorter", fixture, [fixture.DataDirectory, fixture.SpoolDirectory, "missing"]);
        var secondTagger = await RunAsync("sm-tagger", fixture, [fixture.DataDirectory, fixture.SpoolDirectory, "missing"]);
        Assert.Equal(1, secondSorter.ExitCode);
        Assert.Equal(1, secondTagger.ExitCode);
        Assert.Contains("lock", secondSorter.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lock", secondTagger.Error, StringComparison.OrdinalIgnoreCase);
        await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.SpoolDirectory, "queued-1.hdr")), tagger);
    }

    // A real watcher processes existing backlog and later arrivals after retaining a local contract error.
    [ReleaseFact]
    public async Task PublishedWatcherHandlesBacklogNewArrivalAndMessageLocalFailure()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("a-invalid", eml: ProcessorFixture.Message(""));
        fixture.WriteMessage("b-valid");
        using var tagger = Start("sm-tagger", fixture, [fixture.DataDirectory, "-log", fixture.SpoolDirectory]);
        await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.SpoolDirectory, "b-valid-1.hdr")), tagger);
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "a-invalid.hdr.err")));
        fixture.WriteMessage("c-arrival");
        await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.SpoolDirectory, "c-arrival-1.hdr")), tagger);
        Assert.Contains("event=QUEUE_SCAN", ReadSharedText(Path.Combine(fixture.DataDirectory, "log.txt")));
        Assert.False(tagger.Process.HasExited);
    }

    // Killing the actual release process during fan-out leaves inert work that restart must not replay.
    [ReleaseFact]
    public async Task ForcedTerminationDuringFanOutReloadsMappingsWithoutResumingMail()
    {
        using var fixture = new ProcessorFixture();
        var recipients = string.Join(',', Enumerable.Range(0, 1500).Select(index => $"recipient{index:D4}@example.net"));
        fixture.WriteMessage("crash", ProcessorFixture.Header(recipients));
        using (var interrupted = Start("sm-tagger", fixture, [fixture.DataDirectory, fixture.SpoolDirectory, "crash"]))
        {
            await WaitUntilAsync(() => Directory.EnumerateFiles(fixture.ProcessDirectory, "*.pend").Any(), interrupted);
            interrupted.Stop();
        }

        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "crash.hdr.break")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        var retained = Directory.GetFiles(fixture.ProcessDirectory).ToDictionary(path => Path.GetFileName(path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        using (var loaded = fixture.Open())
        {
            Assert.NotEmpty(loaded.Tags.Mappings);
        }

        using var restarted = Start("sm-tagger", fixture, [fixture.DataDirectory, fixture.SpoolDirectory]);
        await WaitUntilAsync(() => OwnsLock(Path.Combine(fixture.DataDirectory, "sm-tagger.lock")), restarted);
        fixture.WriteMessage("fresh");
        await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.SpoolDirectory, "fresh-1.hdr")), restarted);
        foreach (var file in retained)
        {
            var path = Path.Combine(fixture.ProcessDirectory, file.Key!);
            Assert.True(File.Exists(path));
            Assert.Equal(file.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }

        Assert.Single(Directory.GetFiles(fixture.SpoolDirectory, "*.hdr"));
    }

    // Confirms published folders do not carry test assemblies, private samples, or tagger dependencies in the sorter.
    [ReleaseFact]
    public void ReleaseFoldersContainOnlyTheirOwnRuntimeDependencies()
    {
        var sorter = ReleaseFactAttribute.ReleaseDirectory("sm-sorter");
        var tagger = ReleaseFactAttribute.ReleaseDirectory("sm-tagger");
        Assert.False(File.Exists(Path.Combine(sorter, "SmTagger.Mail.dll")));
        Assert.False(File.Exists(Path.Combine(sorter, "SmTagger.Engine.dll")));
        Assert.True(File.Exists(Path.Combine(tagger, "SmTagger.Engine.dll")));
        foreach (var directory in new[] { sorter, tagger })
        {
            Assert.DoesNotContain(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories),
                file => file.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).Contains("Tests", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).Contains("CrashWorker", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).Contains("lab-services", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Feeds the exact synthetic pair through the sorter's queue without changing its bytes.
    private static void MoveToSorter(ProcessorFixture fixture, string basename)
    {
        foreach (var extension in new[] { ".eml", ".hdr" })
        {
            File.Move(Path.Combine(fixture.ProcessDirectory, basename + extension),
                Path.Combine(fixture.SpoolDirectory, "proc", basename + extension));
        }
    }

    // Runs one published executable with bounded lifetime and continuously drained output pipes.
    private static async Task<ProcessResult> RunAsync(string application, ProcessorFixture fixture, string[] arguments)
    {
        using var running = Start(application, fixture, arguments);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await running.Process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(running.Process.ExitCode, await running.Output, await running.Error);
    }

    // Starts the self-contained release without relying on DOTNET_ROOT or an installed runtime.
    private static RunningExecutable Start(string application, ProcessorFixture fixture, string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(ReleaseFactAttribute.ReleaseDirectory(application), application + ".exe"))
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove("DOTNET_ROOT");
        info.Environment.Remove("DOTNET_ROOT_X64");
        return new RunningExecutable(Process.Start(info) ?? throw new InvalidOperationException("Could not start release executable."));
    }

    // Waits for observed filesystem behavior while detecting premature process exit.
    private static async Task WaitUntilAsync(Func<bool> condition, RunningExecutable running)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (running.Process.HasExited)
            {
                Assert.Fail("Release executable exited before the expected state: " + await running.Error);
            }

            if (timeout.Elapsed > TimeSpan.FromSeconds(30))
            {
                Assert.Fail("Release executable did not reach the expected filesystem state within 30 seconds.");
            }

            await Task.Delay(10);
        }
    }

    // Observes actual Windows sharing contention without replacing the lock file.
    private static bool OwnsLock(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    // Reads a live trace using the sharing mode permitted to diagnostic observers.
    private static string ReadSharedText(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class RunningExecutable : IDisposable
    {
        public Process Process { get; }
        public Task<string> Output { get; }
        public Task<string> Error { get; }

        // Starts draining both streams immediately so verbose diagnostics cannot block the child.
        public RunningExecutable(Process process)
        {
            Process = process;
            Output = process.StandardOutput.ReadToEndAsync();
            Error = process.StandardError.ReadToEndAsync();
        }

        // Forces only this owned test process to stop, modeling the documented process-crash boundary.
        public void Stop()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                if (!Process.WaitForExit(10_000))
                {
                    throw new TimeoutException("Owned test process did not terminate.");
                }
            }
        }

        // Ensures no watcher or open singleton outlives its isolated test fixture.
        public void Dispose()
        {
            Stop();
            Process.Dispose();
        }
    }
}

internal sealed class ReleaseFactAttribute : FactAttribute
{
    // Skips release-process checks explicitly until the real production artifacts have been published.
    public ReleaseFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(ReleaseDirectory("sm-sorter"), "sm-sorter.exe")) ||
            !File.Exists(Path.Combine(ReleaseDirectory("sm-tagger"), "sm-tagger.exe")))
        {
            Skip = "Run scripts/Publish-Release.ps1 on Windows before release executable checks.";
        }
    }

    // Locates the repository-owned release folder from the test assembly's build directory.
    public static string ReleaseDirectory(string application)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmTagger.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? AppContext.BaseDirectory, "artifacts", "release", application);
    }
}
