using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ConciseExecutableTests
{
    // Exercise all independent output flag combinations through both real standalone executables.
    [StandaloneTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task StandaloneOutputFlagsRemainIndependent(bool concise, bool verbose, bool log)
    {
        using ProcessorFixture fixture = new();
        fixture.WriteMessage("7300");
        MoveToSorter(fixture, "7300");
        string binaries = ReleaseFactAttribute.ReleaseDirectory("standalone");
        foreach (string application in new[] { "sm-sorter", "sm-tagger" })
        {
            string logPath = Path.Combine(fixture.Root, application + ".log");
            List<string> arguments = [fixture.DataDirectory];
            if (concise) arguments.Add("-c");
            if (verbose) arguments.Add("-v");
            if (log) arguments.AddRange(["-l", logPath]);
            arguments.AddRange([fixture.SpoolDirectory, "7300"]);

            ProcessResult result = await RunAsync(binaries, application, fixture, arguments);

            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Empty(result.Error);
            string[] lines = Lines(result.Output);
            string[] summaries = concise ? AssertStartup(result.Output, application, fixture, "7300",
                application == "sm-tagger" ? "loaded 1 sender records and 0 tag mappings from disk" : null)
                : WithoutDebug(result.Output);
            Assert.Equal(verbose, lines.Any(line => line.StartsWith("DEBUG ", StringComparison.Ordinal)));
            Assert.Equal(concise ? 1 : 0, summaries.Length);
            if (concise)
            {
                string summary = Assert.Single(summaries);
                Assert.Contains((application == "sm-sorter" ? "TAKE" : "from") + " <private@example.com> to <alice@example.net>, auth <auth@example.com>", summary);
                Assert.EndsWith(application == "sm-sorter" ? "auth <auth@example.com>" : "moved to spool", summary);
                if (application == "sm-tagger") Assert.Contains("created tag <", summary);
            }
            Assert.Equal(log, File.Exists(logPath));
            if (log)
            {
                string contents = File.ReadAllText(logPath);
                Assert.NotEmpty(contents);
                Assert.DoesNotContain("moved to ", contents);
                Assert.DoesNotContain(" starting, ", contents);
                Assert.DoesNotContain("tag mappings from disk", contents);
                if (application == "sm-sorter")
                    Assert.Contains("result=DIVERT", Assert.Single(File.ReadAllLines(logPath)));
                else
                    Assert.Contains("event=", contents);
            }
        }
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "7300c1.hdr")));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "7300c1.eml")));
        Assert.False(File.Exists(Path.Combine(fixture.DataDirectory, "log.txt")));
    }

    // Verify actual application-folder fan-out prints each published child and distinguishes new and reused tags.
    [ReleaseFact]
    public Task ApplicationFolderReportsCreatedAndReusedChildren() => VerifyFanoutAsync(standalone: false);

    // Verify the same child reporting and stable identity behavior through actual single-file bundles.
    [StandaloneFact]
    public Task StandaloneReportsCreatedAndReusedChildren() => VerifyFanoutAsync(standalone: true);

    // Exercise repeated readiness deferrals through a real watcher without waiting for the idle rescan timer.
    [ReleaseFact]
    public async Task SorterWatcherReportsOnlyCompletedPairsAcrossRepeatedDeferrals()
    {
        using ProcessorFixture fixture = new();
        string input = Path.Combine(fixture.SpoolDirectory, "proc");
        string log = Path.Combine(fixture.Root, "watcher.log");
        string readyHdr = ProcessorFixture.Header(auth: "not-enrolled@example.com");
        fixture.WriteMessage("a-waiting", readyHdr.Replace("Written ", "Writing ", StringComparison.Ordinal));
        MoveToSorter(fixture, "a-waiting");
        fixture.WriteMessage("z-one", readyHdr);
        MoveToSorter(fixture, "z-one");
        using RunningExecutable running = Start(ReleaseFactAttribute.ReleaseDirectory("sm-sorter"), "sm-sorter", fixture,
            [fixture.DataDirectory, "-c", "-l", log, fixture.SpoolDirectory]);
        await WaitUntilAsync(() => HasPassRecord(log, "z-one"), running);
        Assert.True(File.Exists(Path.Combine(input, "a-waiting.hdr")));
        Assert.False(File.Exists(Path.Combine(input, "a-waiting.hdr.sort")));

        fixture.WriteMessage("z-two", readyHdr);
        MoveToSorter(fixture, "z-two");
        await WaitUntilAsync(() => HasPassRecord(log, "z-two"), running);
        Assert.True(File.Exists(Path.Combine(input, "a-waiting.hdr")));
        Assert.False(File.Exists(Path.Combine(input, "a-waiting.hdr.sort")));
        Assert.DoesNotContain("basename=\"a-waiting\"", ReadSharedText(log));

        File.WriteAllText(Path.Combine(input, "a-waiting.hdr"), readyHdr);
        fixture.WriteMessage("z-three", readyHdr);
        MoveToSorter(fixture, "z-three");
        await WaitUntilAsync(() => HasPassRecord(log, "a-waiting") && HasPassRecord(log, "z-three")
            && running.OutputLines.Count(line => line.Contains(" PASS ", StringComparison.Ordinal)) >= 4, running);
        running.Stop();

        string[] summaries = AssertStartup(await running.Output.WaitAsync(TimeSpan.FromSeconds(10)),
            "sm-sorter", fixture, null);
        Assert.Equal(4, summaries.Length);
        Assert.Single(summaries, line => line.Contains(": a-waiting PASS ", StringComparison.Ordinal));
        Assert.All(summaries, line => Assert.EndsWith("auth <not-enrolled@example.com>", line));
        Assert.Empty(await running.Error.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(4, File.ReadAllLines(log).Length);
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "a-waiting.hdr")));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "a-waiting.eml")));
        Assert.False(File.Exists(Path.Combine(input, "a-waiting.sort.err")));
    }

    // Failed configuration or mapping loading must not announce a successful load or begin queue processing.
    [StandaloneTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTaggerStartupPrintsWelcomeWithoutLoadSummary(bool invalidMapping)
    {
        using ProcessorFixture fixture = new();
        Directory.Delete(fixture.ProcessDirectory);
        if (invalidMapping)
            Directory.CreateDirectory(Path.Combine(fixture.DataDirectory, "tag-addresses", "incomplete@example.com"));
        else
            fixture.WriteProfile("allow-mdn.txt", "invalid");

        ProcessResult result = await RunAsync(ReleaseFactAttribute.ReleaseDirectory("standalone"), "sm-tagger", fixture,
            [fixture.DataDirectory, "-c", fixture.SpoolDirectory, "startup-failure"]);

        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(result.Error);
        Assert.Empty(AssertStartup(result.Output, "sm-tagger", fixture, "startup-failure"));
        Assert.DoesNotContain("loaded ", result.Output);
        Assert.False(Directory.Exists(fixture.ProcessDirectory));
    }

    // Report zero counts from a valid empty configuration while ignoring incomplete mapping staging.
    [StandaloneFact]
    public async Task EmptyTaggerStartupDoesNotCountInertStaging()
    {
        using ProcessorFixture fixture = new();
        foreach (string file in Directory.GetFiles(fixture.ProfileDirectory)) File.Delete(file);
        Directory.Delete(fixture.ProfileDirectory);
        string authDirectory = Path.Combine(fixture.DataDirectory, "senders", "auth-addresses", ProcessorFixture.Auth);
        File.Delete(Path.Combine(authDirectory, "sender-id.txt"));
        Directory.Delete(authDirectory);
        string staging = Path.Combine(fixture.DataDirectory, "staging", Guid.NewGuid() + ".tagtmp");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "incomplete.txt"), "inert synthetic staging");
        Directory.Delete(fixture.ProcessDirectory);
        using RunningExecutable running = Start(ReleaseFactAttribute.ReleaseDirectory("standalone"), "sm-tagger", fixture,
            [fixture.DataDirectory, "-c", fixture.SpoolDirectory]);
        await WaitUntilAsync(() => running.OutputLines.Any(line => line.EndsWith(
            "loaded 0 sender records and 0 tag mappings from disk", StringComparison.Ordinal))
            && Directory.Exists(fixture.ProcessDirectory), running);
        running.Stop();

        Assert.Empty(AssertStartup(await running.Output.WaitAsync(TimeSpan.FromSeconds(10)), "sm-tagger", fixture, null,
            "loaded 0 sender records and 0 tag mappings from disk"));
        string warning = Assert.Single(Lines(await running.Error.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.Contains("WARNING inert staging entry retained:", warning);
        Assert.Contains(Path.GetFileName(staging), warning);
        Assert.Equal("inert synthetic staging", File.ReadAllText(Path.Combine(staging, "incomplete.txt")));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "tag-addresses")));
    }

    // No welcome is emitted until every required exclusive lock belongs to this invocation.
    [StandaloneTheory]
    [InlineData("sm-sorter", "sorter")]
    [InlineData("sm-tagger", "data")]
    [InlineData("sm-tagger", "queue")]
    public async Task LockContentionLeavesConciseOutputQuiet(string application, string lockKind)
    {
        using ProcessorFixture fixture = new();
        var original = fixture.WriteMessage("locked");
        if (application == "sm-sorter") MoveToSorter(fixture, "locked");
        string lockPath = lockKind switch
        {
            "sorter" => Path.Combine(fixture.SpoolDirectory, "proc", "sm-sorter.lock"),
            "data" => Path.Combine(fixture.DataDirectory, "sm-tagger.lock"),
            _ => Path.Combine(fixture.WorkDirectory, "sm-tagger.lock")
        };
        using FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ProcessResult result = await RunAsync(ReleaseFactAttribute.ReleaseDirectory("standalone"), application, fixture,
            [fixture.DataDirectory, "-c", fixture.SpoolDirectory, "locked"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.NotEmpty(result.Error);
        string input = application == "sm-sorter" ? Path.Combine(fixture.SpoolDirectory, "proc") : fixture.ProcessDirectory;
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(input, "locked.hdr")));
        Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(input, "locked.eml")));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "tag-addresses")));
    }

    // Start fresh processes for two parents so reused reporting is established from durable mappings after restart.
    private static async Task VerifyFanoutAsync(bool standalone)
    {
        using ProcessorFixture fixture = new();
        string[] recipients = ["alice@example.net", "bob@example.net"];
        Dictionary<string, string> tags = [];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string parent = attempt == 0 ? "7410" : "7411";
            fixture.WriteMessage(parent, ProcessorFixture.Header("bob@example.net,alice@example.net"));
            MoveToSorter(fixture, parent);
            string sorterDirectory = ReleaseFactAttribute.ReleaseDirectory(standalone ? "standalone" : "sm-sorter");
            ProcessResult sorter = await RunAsync(sorterDirectory, "sm-sorter", fixture,
                [fixture.DataDirectory, "-c", fixture.SpoolDirectory, parent]);
            Assert.True(sorter.ExitCode == 0, sorter.Error);
            Assert.Empty(sorter.Error);
            string sorterSummary = Assert.Single(AssertStartup(sorter.Output, "sm-sorter", fixture, parent));
            Assert.Contains(": " + parent + " TAKE ", sorterSummary);
            Assert.EndsWith("auth <auth@example.com>", sorterSummary);

            string taggerDirectory = ReleaseFactAttribute.ReleaseDirectory(standalone ? "standalone" : "sm-tagger");
            ProcessResult tagger = await RunAsync(taggerDirectory, "sm-tagger", fixture,
                [fixture.DataDirectory, "-c", fixture.SpoolDirectory, parent]);
            Assert.True(tagger.ExitCode == 0, tagger.Error);
            Assert.Empty(tagger.Error);
            string[] summaries = AssertStartup(tagger.Output, "sm-tagger", fixture, parent,
                "loaded 1 sender records and " + (attempt == 0 ? "0" : "3") + " tag mappings from disk");
            Assert.Equal(2, summaries.Length);
            string[] mappingDirectories = Directory.GetDirectories(Path.Combine(fixture.DataDirectory, "tag-addresses"));
            Assert.Equal(3, mappingDirectories.Length);
            for (int child = 0; child < recipients.Length; child++)
            {
                string recipient = recipients[child];
                string mapping = Assert.Single(mappingDirectories, directory =>
                    File.ReadAllText(Path.Combine(directory, "recipient-id.txt")) == recipient + ";");
                string tag = Path.GetFileName(mapping);
                if (attempt == 0) tags.Add(recipient, tag);
                else Assert.Equal(tags[recipient], tag);
                string basename = parent + "c" + (child + 1);
                string summary = summaries[child];
                Assert.Contains(": " + basename + " from <private@example.com> to <" + recipient + ">, auth <auth@example.com>", summary);
                Assert.Contains((attempt == 0 ? ", created tag <" : ", used tag <") + tag + ">", summary);
                Assert.EndsWith("moved to spool", summary);
                Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, basename + ".hdr")));
                Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, basename + ".eml")));
                Assert.Contains(tag, File.ReadAllText(Path.Combine(fixture.SpoolDirectory, basename + ".hdr")));
            }
            Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, parent + ".hdr")));
            Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, parent + ".eml")));
        }
    }

    // Publish a complete synthetic source HDR before final EML visibility admits sorter discovery.
    private static void MoveToSorter(ProcessorFixture fixture, string basename)
    {
        foreach (string extension in new[] { ".hdr", ".eml" })
            File.Move(Path.Combine(fixture.ProcessDirectory, basename + extension),
                Path.Combine(fixture.SpoolDirectory, "proc", basename + extension));
    }

    // Bound each one-shot invocation while continuously draining both redirected output streams.
    private static async Task<ProcessResult> RunAsync(string directory, string application, ProcessorFixture fixture,
        IEnumerable<string> arguments)
    {
        using RunningExecutable running = Start(directory, application, fixture, arguments);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await running.Process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(running.Process.ExitCode, await running.Output, await running.Error);
    }

    // Launch a real published Windows executable without inheriting a selected SDK/runtime host.
    private static RunningExecutable Start(string directory, string application, ProcessorFixture fixture,
        IEnumerable<string> arguments)
    {
        ProcessStartInfo info = new(Path.Combine(directory, application + ".exe"))
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        foreach (string variable in info.Environment.Keys.Where(name => name.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(variable);
        info.Environment.Remove("DOTNET_HOST_PATH");
        return new RunningExecutable(Process.Start(info) ?? throw new InvalidOperationException("Could not start published executable."));
    }

    // Observe a completed flushed sorter record without taking an incompatible file handle.
    private static bool HasPassRecord(string log, string basename) => File.Exists(log)
        && ReadSharedText(log).Contains("basename=\"" + basename + "\" result=PASS", StringComparison.Ordinal)
        && ReadSharedText(log).EndsWith("\r\n", StringComparison.Ordinal);

    // Read a live logfile using sharing compatible with its append writer.
    private static string ReadSharedText(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    // Wait for real observable progress and fail promptly if the owned watcher exits unexpectedly.
    private static async Task WaitUntilAsync(Func<bool> condition, RunningExecutable running)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (running.Process.HasExited)
                Assert.Fail("Published watcher exited before the expected state: " + await running.Error);
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                Assert.Fail("Published watcher did not reach the expected state within 30 seconds.");
            await Task.Delay(10);
        }
    }

    // Count actual physical output records rather than searching a concatenated stream.
    private static string[] Lines(string text) => text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    // Keep independently requested verbose output separate from concise startup and message lines.
    private static string[] WithoutDebug(string text) => Lines(text)
        .Where(line => !line.StartsWith("DEBUG ", StringComparison.Ordinal)).ToArray();

    // Validate startup state first, then return only completed-pair summaries for existing processing assertions.
    private static string[] AssertStartup(string output, string application, ProcessorFixture fixture,
        string? basename, string? loaded = null)
    {
        string[] lines = WithoutDebug(output);
        int startupCount = loaded is null ? 2 : 3;
        Assert.True(lines.Length >= startupCount, output);
        string version = typeof(Invocation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Assert.EndsWith(": " + application + " " + version + " starting, " + (basename is null ? "watch mode"
            : "one-shot mode, message " + basename), lines[0]);
        Assert.EndsWith(": data \"" + fixture.DataDirectory + "\", spool \"" + fixture.SpoolDirectory + "\"", lines[1]);
        if (loaded is not null) Assert.EndsWith(": " + loaded, lines[2]);
        return lines.Skip(startupCount).ToArray();
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class RunningExecutable : IDisposable
    {
        public Process Process { get; }
        public Task<string> Output { get; }
        public Task<string> Error { get; }
        public ConcurrentQueue<string> OutputLines { get; } = new();

        // Drain both pipes immediately so diagnostic output cannot block the test process.
        public RunningExecutable(Process process)
        {
            Process = process;
            Output = ReadOutputAsync(process.StandardOutput);
            Error = process.StandardError.ReadToEndAsync();
        }

        // Expose flushed stdout lines to watcher assertions while retaining the complete bounded test output.
        private async Task<string> ReadOutputAsync(StreamReader reader)
        {
            StringBuilder text = new();
            while (await reader.ReadLineAsync() is { } line)
            {
                OutputLines.Enqueue(line);
                text.AppendLine(line);
            }
            return text.ToString();
        }

        // Stop only this fixture-owned watcher after the asserted pair has completed processing.
        public void Stop()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                if (!Process.WaitForExit(10_000)) throw new TimeoutException("Owned test process did not terminate.");
            }
        }

        // Ensure the child process releases every fixture file and lock before temporary-tree cleanup.
        public void Dispose()
        {
            Stop();
            Process.Dispose();
        }
    }
}
