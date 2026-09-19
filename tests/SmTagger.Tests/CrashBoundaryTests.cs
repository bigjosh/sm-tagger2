using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class CrashBoundaryTests
{
    private const string Recipients = "alice@example.net,bob@example.net";
    private const string GroupRecipientId = "alice@example.net;bob@example.net;";

    // Kill only after an acknowledged boundary, then prove production startup preserves evidence and reuses live identities.
    [WindowsCrashTheory]
    [InlineData("PARENT_HDR_CLAIMED")]
    [InlineData("STAGING_READY")]
    [InlineData("MAPPING_PUBLISHED")]
    [InlineData("CHILD_READY")]
    [InlineData("CHILD_EML_PUBLISHED")]
    [InlineData("BEFORE_PARENT_CLEANUP")]
    [InlineData("FROM_HDR_RETAINED")]
    public async Task ForcedTerminationRetainsTheExactBoundaryAndProductionRestartDoesNotResumeIt(string phase)
    {
        using var fixture = new ProcessorFixture();
        byte[] body = [0, 255, 127, 13, 10, 17];
        var original = fixture.WriteMessage("crash", ProcessorFixture.Header(Recipients),
            phase == "FROM_HDR_RETAINED" ? ProcessorFixture.Message("") : ProcessorFixture.Message(), body);
        string signalPath = Path.Combine(fixture.Root, "boundary.ready");
        using (var interrupted = Start(typeof(SmTagger.CrashWorker.Program).Assembly.Location, fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "crash", phase, signalPath]))
        {
            await WaitUntilAsync(() => File.Exists(signalPath), interrupted);
            Assert.Equal(phase, File.ReadAllText(signalPath));
            Assert.False(interrupted.Process.HasExited);
            Assert.Throws<IOException>(() =>
            {
                using var denied = SingletonLock.Acquire(Path.Combine(fixture.DataDirectory, "sm-tagger.lock"));
            });
            Assert.Throws<IOException>(() =>
            {
                using var denied = SingletonLock.Acquire(Path.Combine(fixture.WorkDirectory, "sm-tagger.lock"));
            });
            interrupted.Stop();
        }

        using (SingletonLock releasedData = SingletonLock.Acquire(Path.Combine(fixture.DataDirectory, "sm-tagger.lock")))
        using (SingletonLock releasedQueue = SingletonLock.Acquire(Path.Combine(fixture.WorkDirectory, "sm-tagger.lock")))
        {
            Assert.True(File.Exists(Path.Combine(fixture.WorkDirectory, "sm-tagger.lock")));
        }

        BoundaryState expected = ExpectedState(phase);
        AssertBoundaryFiles(fixture, expected, original.Hdr, original.Eml);
        Dictionary<string, string> originalMappings;
        using (var loaded = fixture.Open())
        {
            originalMappings = loaded.Tags.Mappings.Values.ToDictionary(mapping => mapping.RecipientId,
                mapping => mapping.TagAddress, StringComparer.Ordinal);
            Assert.Equal(expected.LiveMappings, originalMappings.Count);
            string[] allocationOrder = [GroupRecipientId, "alice@example.net;", "bob@example.net;"];
            Assert.Equal(allocationOrder.Take(expected.LiveMappings).Order(StringComparer.Ordinal),
                originalMappings.Keys.Order(StringComparer.Ordinal));
        }

        string stagingRoot = Path.Combine(fixture.DataDirectory, "staging");
        AssertStagingIdentity(fixture, stagingRoot, expected.StagingRecords);
        var retainedProcess = Snapshot(fixture.ProcessDirectory);
        var retainedStaging = Snapshot(stagingRoot);
        var retainedSpool = Snapshot(fixture.SpoolDirectory);
        string tracePath = Path.Combine(fixture.DataDirectory, "log.txt");
        using (var restarted = Start(typeof(SmTagger.Program).Assembly.Location, fixture,
            [fixture.DataDirectory, "-l", Path.Combine(fixture.DataDirectory, "log.txt"), fixture.SpoolDirectory]))
        {
            await WaitUntilAsync(() => ReadSharedTrace(tracePath).Contains("event=QUEUE_SCAN result=OK", StringComparison.Ordinal), restarted);
            string[] mappingEvents = ReadSharedTrace(tracePath).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("event=MAPPING_LOAD result=OK", StringComparison.Ordinal)).ToArray();
            Assert.Equal(expected.LiveMappings, mappingEvents.Length);
            foreach (string tagAddress in originalMappings.Values)
                Assert.Contains(mappingEvents, line => line.Contains("tagAddress=\"" + tagAddress + "\"", StringComparison.Ordinal));
            AssertSnapshot(retainedProcess, fixture.ProcessDirectory);
            AssertSnapshot(retainedStaging, stagingRoot);
            AssertSnapshot(retainedSpool, fixture.SpoolDirectory);

            PublishFreshPair(fixture, body);
            await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.SpoolDirectory, "freshc2.hdr")) &&
                !File.Exists(Path.Combine(fixture.ProcessDirectory, "fresh.hdr.break")), restarted);
            Assert.False(restarted.Process.HasExited);
            restarted.Stop();
            string errors = await restarted.Error;
            Assert.Contains("WARNING retained message evidence", errors);
            if (expected.StagingRecords > 0)
                Assert.Contains("WARNING inert staging entry", errors);
            Assert.DoesNotContain("ERROR", errors);
        }

        AssertSnapshot(retainedProcess, fixture.ProcessDirectory);
        AssertSnapshot(retainedStaging, stagingRoot);
        AssertSpoolAfterFreshMail(fixture, retainedSpool);
        using var final = fixture.Open();
        Assert.Equal(3, final.Tags.Mappings.Count);
        foreach ((string recipientId, string tagAddress) in originalMappings)
        {
            Assert.Equal(tagAddress, final.Tags.Mappings[(ProcessorFixture.SenderId, recipientId)].TagAddress);
            string directory = Path.Combine(fixture.DataDirectory, "tag-addresses", tagAddress);
            Assert.Equal(ProcessorFixture.SenderId, File.ReadAllText(Path.Combine(directory, "sender-id.txt")));
            Assert.Equal(recipientId, File.ReadAllText(Path.Combine(directory, "recipient-id.txt")));
        }

        string group = final.Tags.Mappings[(ProcessorFixture.SenderId, GroupRecipientId)].TagAddress;
        string[] recipientIds = ["alice@example.net;", "bob@example.net;"];
        for (int index = 0; index < recipientIds.Length; index++)
        {
            string individual = final.Tags.Mappings[(ProcessorFixture.SenderId, recipientIds[index])].TagAddress;
            byte[] output = File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, $"freshc{index + 1}.eml"));
            string headers = Encoding.ASCII.GetString(output[..^body.Length]);
            Assert.Contains($"From: \"Fixture Sender\" <{individual}>\r\n", headers);
            Assert.Contains($"Reply-To: \"Fixture Sender\" <{group}>\r\n", headers);
            Assert.Equal(body, output[^body.Length..]);
        }
    }

    // Describe the externally visible files immediately after each intentionally interrupted transition.
    private static BoundaryState ExpectedState(string phase) => phase switch
    {
        "PARENT_HDR_CLAIMED" => new(".hdr.start", ".eml", [], [], 0, 0),
        "STAGING_READY" => new(".hdr.break", ".eml.break", [], [], 0, 1),
        "MAPPING_PUBLISHED" => new(".hdr.break", ".eml.break", [], [], 1, 0),
        "CHILD_READY" => new(".hdr.break", ".eml.break", ["crashc1.eml.pend", "crashc1.hdr.pend"], [], 2, 0),
        "CHILD_EML_PUBLISHED" => new(".hdr.break", ".eml.break",
            ["crashc1.hdr.pend", "crashc2.eml.pend", "crashc2.hdr.pend"], ["crashc1.eml"], 3, 0),
        "BEFORE_PARENT_CLEANUP" => new(".hdr.break", ".eml.break", [],
            ["crashc1.eml", "crashc1.hdr", "crashc2.eml", "crashc2.hdr"], 3, 0),
        "FROM_HDR_RETAINED" => new(".hdr.err", ".eml.start", [], [], 0, 0),
        _ => throw new ArgumentException("Unknown crash-test phase.", nameof(phase))
    };

    // Check complete file inventories and byte-exact originals before any restart can alter the evidence.
    private static void AssertBoundaryFiles(ProcessorFixture fixture, BoundaryState expected, byte[] hdr, byte[] eml)
    {
        string[] expectedProcess = ["crash" + expected.HdrSuffix, "crash" + expected.EmlSuffix, .. expected.ChildFiles];
        Assert.Equal(expectedProcess.Order(StringComparer.Ordinal),
            Directory.GetFiles(fixture.ProcessDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(expected.SpoolFiles.Order(StringComparer.Ordinal),
            Directory.GetFiles(fixture.SpoolDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "crash" + expected.HdrSuffix)));
        Assert.Equal(eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "crash" + expected.EmlSuffix)));
    }

    // Prove the unpublished stage is complete but inert, including a closed empty best-effort tag log.
    private static void AssertStagingIdentity(ProcessorFixture fixture, string root, int expectedCount)
    {
        string[] directories = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        Assert.Equal(expectedCount, directories.Length);
        foreach (string directory in directories)
        {
            Assert.EndsWith(".tagtmp", directory);
            Assert.Equal(ProcessorFixture.SenderId, File.ReadAllText(Path.Combine(directory, "sender-id.txt")));
            Assert.Equal(GroupRecipientId, File.ReadAllText(Path.Combine(directory, "recipient-id.txt")));
            Assert.Empty(File.ReadAllBytes(Path.Combine(directory, "tag-log.txt")));
            Assert.Equal(3, Directory.GetFiles(directory).Length);
        }
    }

    // Publish closed fixture bytes with the same EML-first/HDR-last visibility contract required of SmarterMail.
    private static void PublishFreshPair(ProcessorFixture fixture, byte[] body)
    {
        string eml = Path.Combine(fixture.ProcessDirectory, "fresh.eml.inject");
        string hdr = Path.Combine(fixture.ProcessDirectory, "fresh.hdr.inject");
        File.WriteAllBytes(eml, [.. Encoding.ASCII.GetBytes(ProcessorFixture.Message()), .. body]);
        File.WriteAllBytes(hdr, Encoding.ASCII.GetBytes(ProcessorFixture.Header(Recipients)));
        File.Move(eml, Path.Combine(fixture.ProcessDirectory, "fresh.eml"), overwrite: false);
        File.Move(hdr, Path.Combine(fixture.ProcessDirectory, "fresh.hdr"), overwrite: false);
    }

    // Record retained mail and directories without opening the independently verified active queue lock.
    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
            return snapshot;
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(Path.Combine("proc", "sm-tagger", "sm-tagger.lock"), StringComparison.OrdinalIgnoreCase))
                continue;
            string relative = Path.GetRelativePath(root, path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                snapshot.Add("D|" + relative, "");
            else
                snapshot.Add("F|" + relative, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return snapshot;
    }

    // Require retained evidence to have the same names, directory structure, and byte hashes after restart.
    private static void AssertSnapshot(SortedDictionary<string, string> expected, string root)
    {
        Assert.Equal(expected.ToArray(), Snapshot(root).ToArray());
    }

    // Allow only the two fresh child pairs while proving no interrupted output was replayed or changed.
    private static void AssertSpoolAfterFreshMail(ProcessorFixture fixture, SortedDictionary<string, string> retained)
    {
        var actual = Snapshot(fixture.SpoolDirectory);
        foreach ((string path, string hash) in retained)
        {
            Assert.True(actual.Remove(path, out string? actualHash), "Retained spool evidence disappeared: " + path);
            Assert.Equal(hash, actualHash);
        }
        Assert.Equal(new[] { "F|freshc1.eml", "F|freshc1.hdr", "F|freshc2.eml", "F|freshc2.hdr" }, actual.Keys);
    }

    // Launch either the separate test worker or the production tagger assembly using the installed managed host.
    private static RunningProcess Start(string assembly, ProcessorFixture fixture, string[] arguments)
    {
        var info = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(assembly);
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        return new RunningProcess(Process.Start(info) ?? throw new InvalidOperationException("Could not start owned crash-test process."));
    }

    // Prefer the active test host and approved installed runtime without installing or changing machine settings.
    private static string DotnetHost()
    {
        string? activeHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(activeHost) && File.Exists(activeHost))
            return activeHost;
        foreach (string variable in new[] { "DOTNET_ROOT_X64", "DOTNET_ROOT" })
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "dotnet.exe")))
                return Path.Combine(root, "dotnet.exe");
        }
        string approvedHost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "dotnet", "dotnet.exe");
        return File.Exists(approvedHost) ? approvedHost : "dotnet";
    }

    // Wait for an explicit observed boundary and fail promptly if the owned process exits before reaching it.
    private static async Task WaitUntilAsync(Func<bool> condition, RunningProcess running)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (running.Process.HasExited)
                Assert.Fail("Owned process exited before its expected boundary: " + await running.Error);
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                Assert.Fail("Owned process did not reach its expected boundary within 30 seconds.");
            await Task.Delay(10);
        }
    }

    // Observe completed trace events through the reader sharing permitted by the production append stream.
    private static string ReadSharedTrace(string path)
    {
        if (!File.Exists(path))
            return "";
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private sealed record BoundaryState(string HdrSuffix, string EmlSuffix, string[] ChildFiles,
        string[] SpoolFiles, int LiveMappings, int StagingRecords);

    private sealed class RunningProcess : IDisposable
    {
        public Process Process { get; }
        public Task<string> Error { get; }
        private Task<string> Output { get; }

        // Drain both redirected streams immediately so a diagnostic cannot prevent reaching the test boundary.
        public RunningProcess(Process process)
        {
            Process = process;
            Error = process.StandardError.ReadToEndAsync();
            Output = process.StandardOutput.ReadToEndAsync();
        }

        // Kill only this test-owned process and wait for its handles to close before reading retained state.
        public void Stop()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                if (!Process.WaitForExit(10_000))
                    throw new TimeoutException("Owned crash-test process did not terminate.");
            }
        }

        // Reap the child even when an assertion fails so no watcher or singleton outlives the fixture.
        public void Dispose()
        {
            Stop();
            Process.Dispose();
        }
    }
}

internal sealed class WindowsCrashTheoryAttribute : TheoryAttribute
{
    // Keep unsupported-platform discovery explicit rather than pretending Windows crash guarantees were tested there.
    public WindowsCrashTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Deterministic process-crash boundaries require the supported Windows filesystem and runtime.";
    }
}
