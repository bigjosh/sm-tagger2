using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
            [fixture.DataDirectory, "-keep", "-l", Path.Combine(fixture.DataDirectory, "log.txt"), fixture.SpoolDirectory, "-42"]);
        Assert.Equal(0, tagger.ExitCode);
        Assert.Equal("", tagger.Error);
        Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.hdr").Length);
        Assert.Equal(["-42c1.eml", "-42c1.hdr", "-42c2.eml", "-42c2.hdr"],
            Directory.GetFiles(fixture.SpoolDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
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
            [fixture.DataDirectory, "-l", Path.Combine(fixture.DataDirectory, "log.txt"), fixture.SpoolDirectory, "invalid"]);
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("exactly one From", failed.Error);
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "invalid.hdr.err")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "invalid.eml.err")));
        fixture.WriteMessage("valid");
        var succeeded = await RunAsync("sm-tagger", fixture,
            [fixture.DataDirectory, "-l", Path.Combine(fixture.DataDirectory, "log.txt"), fixture.SpoolDirectory, "valid"]);
        Assert.Equal(0, succeeded.ExitCode);
        Assert.Contains("ERROR logging to", succeeded.Error);
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "validc1.hdr")));
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
        await WaitUntilAsync(() => OwnsLock(Path.Combine(fixture.DataDirectory, "sm-tagger.lock"))
            && OwnsLock(Path.Combine(fixture.WorkDirectory, "sm-tagger.lock")), tagger);
        fixture.WriteMessage("queued");
        var secondSorter = await RunAsync("sm-sorter", fixture, [fixture.DataDirectory, fixture.SpoolDirectory, "missing"]);
        var secondTagger = await RunAsync("sm-tagger", fixture, [fixture.DataDirectory, fixture.SpoolDirectory, "missing"]);
        Assert.Equal(1, secondSorter.ExitCode);
        Assert.Equal(1, secondTagger.ExitCode);
        Assert.Contains("lock", secondSorter.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lock", secondTagger.Error, StringComparison.OrdinalIgnoreCase);
        await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.SpoolDirectory, "queuedc1.hdr")), tagger);
    }

    // A real watcher processes existing backlog and later arrivals after retaining a local contract error.
    [ReleaseFact]
    public async Task PublishedWatcherHandlesBacklogNewArrivalAndMessageLocalFailure()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("a-invalid", eml: ProcessorFixture.Message(""));
        fixture.WriteMessage("b-valid");
        using var tagger = Start("sm-tagger", fixture, [fixture.DataDirectory, "-l", Path.Combine(fixture.DataDirectory, "log.txt"), "-v", fixture.SpoolDirectory]);
        await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.SpoolDirectory, "b-validc1.hdr")), tagger);
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "a-invalid.hdr.err")));
        fixture.WriteMessage("c-arrival");
        await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.SpoolDirectory, "c-arrivalc1.hdr")), tagger);
        Assert.Contains("event=QUEUE_SCAN", ReadSharedText(Path.Combine(fixture.DataDirectory, "log.txt")));
        Assert.False(tagger.Process.HasExited);
        tagger.Stop();
        string output = await tagger.Output.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("DEBUG ", output);
        Assert.Contains("event=QUEUE_SCAN", output);
        Assert.Contains("context=\"c-arrival\"", output);
    }

    // Flushes one result per message while a real sorter watcher keeps its append log and verbose console active.
    [ReleaseFact]
    public async Task PublishedSorterWatcherFlushesResultsForBacklogAndNewArrival()
    {
        using var fixture = new ProcessorFixture();
        string logfile = Path.Combine(fixture.Root, "sorter watch.log");
        var backlog = fixture.WriteMessage("watch-startup", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "watch-startup");
        using var sorter = Start("sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, "-v", fixture.SpoolDirectory]);
        await WaitUntilAsync(() => File.Exists(logfile) &&
            ReadSharedText(logfile).Contains("basename=\"watch-startup\" result=PASS", StringComparison.Ordinal) &&
            ReadSharedText(logfile).EndsWith("\r\n", StringComparison.Ordinal), sorter);

        string firstRecord = ReadSharedText(logfile);
        Assert.EndsWith("\r\n", firstRecord);
        Assert.Single(firstRecord.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(backlog.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "watch-startup.hdr")));
        Assert.Equal(backlog.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "watch-startup.eml")));
        Assert.False(sorter.Process.HasExited);

        var arrival = fixture.WriteMessage("watch-arrival");
        MoveToSorter(fixture, "watch-arrival");
        await WaitUntilAsync(() => ReadSharedText(logfile)
            .Contains("basename=\"watch-arrival\" result=DIVERT", StringComparison.Ordinal) &&
            ReadSharedText(logfile).EndsWith("\r\n", StringComparison.Ordinal), sorter);

        string liveLog = ReadSharedText(logfile);
        string[] records = liveLog.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith(firstRecord, liveLog);
        Assert.EndsWith("\r\n", liveLog);
        Assert.Equal(2, records.Length);
        Assert.Contains("result=PASS", records[0]);
        Assert.Contains("result=DIVERT", records[1]);
        Assert.DoesNotContain("event=", liveLog);
        Assert.Equal(arrival.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "watch-arrival.hdr")));
        Assert.Equal(arrival.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "watch-arrival.eml")));
        Assert.False(sorter.Process.HasExited);

        sorter.Stop();
        string output = await sorter.Output.WaitAsync(TimeSpan.FromSeconds(10));
        string error = await sorter.Error.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(error);
        Assert.Contains("DEBUG", output);
        Assert.Contains("event=STARTUP", output);
        Assert.Contains("event=QUEUE_SCAN", output);
        Assert.Contains("event=MOVE_OK", output);
        Assert.Contains("result=PASS", output);
    }

    // Discover only final EML files, ignore orphan HDRs, and route with the completed replacement HDR.
    [ReleaseFact]
    public async Task PublishedSorterWatcherWaitsForCompletedPairAndUsesFinalHdr()
    {
        using var fixture = new ProcessorFixture();
        string input = Path.Combine(fixture.SpoolDirectory, "proc");
        string logfile = Path.Combine(fixture.Root, "sorter-readiness.log");
        const string basename = "a-readiness";
        byte[] provisional = Encoding.ASCII.GetBytes("Writing \r\nearly@example.com\r\nearly@example.net\r\n\r\n");
        string sourceHdr = Path.Combine(input, basename + ".hdr");
        string sourceEml = Path.Combine(input, basename + ".eml");
        string orphanHdr = Path.Combine(input, "b-orphan.hdr");
        File.WriteAllBytes(sourceHdr, provisional);
        File.WriteAllBytes(orphanHdr, provisional);
        fixture.WriteMessage("z-first-scan", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "z-first-scan");
        using var sorter = Start("sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, "-v", fixture.SpoolDirectory]);
        await WaitUntilAsync(() => File.Exists(logfile) && ReadSharedText(logfile)
            .Contains("basename=\"z-first-scan\" result=PASS", StringComparison.Ordinal), sorter);

        Assert.Equal(provisional, File.ReadAllBytes(sourceHdr));
        Assert.False(File.Exists(sourceEml));
        Assert.False(File.Exists(Path.Combine(input, basename + ".hdr.sort")));
        Assert.False(File.Exists(Path.Combine(input, basename + ".sort.err")));
        Assert.DoesNotContain("basename=\"" + basename + "\"", ReadSharedText(logfile));

        byte[] finalEml = Encoding.ASCII.GetBytes(ProcessorFixture.Message(body: "Final synthetic message.\r\n"));
        string stagedEml = sourceEml + ".tmp";
        File.WriteAllBytes(stagedEml, finalEml);
        fixture.WriteMessage("z-second-scan", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "z-second-scan");
        await WaitUntilAsync(() => ReadSharedText(logfile)
            .Contains("basename=\"z-second-scan\" result=PASS", StringComparison.Ordinal), sorter);

        Assert.Equal(provisional, File.ReadAllBytes(sourceHdr));
        Assert.False(File.Exists(sourceEml));
        Assert.Equal(finalEml, File.ReadAllBytes(stagedEml));
        Assert.False(File.Exists(Path.Combine(input, basename + ".hdr.sort")));
        Assert.False(File.Exists(Path.Combine(input, basename + ".sort.err")));
        Assert.DoesNotContain("basename=\"" + basename + "\"", ReadSharedText(logfile));

        byte[] finalHdr = Encoding.ASCII.GetBytes(ProcessorFixture.Header("final@example.net",
            extra: "opaque: final replacement\r\n"));
        string stagedHdr = Path.Combine(input, basename + ".hdr.ready");
        File.WriteAllBytes(stagedHdr, finalHdr);
        File.Move(stagedHdr, sourceHdr, overwrite: true);
        File.Move(stagedEml, sourceEml);
        await WaitUntilAsync(() => ReadSharedText(logfile)
            .Contains("basename=\"" + basename + "\" result=DIVERT", StringComparison.Ordinal) &&
            ReadSharedText(logfile).EndsWith("\r\n", StringComparison.Ordinal), sorter);

        Assert.Equal(finalHdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, basename + ".hdr")));
        Assert.Equal(finalEml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, basename + ".eml")));
        Assert.False(File.Exists(Path.Combine(fixture.SpoolDirectory, basename + ".hdr")));
        Assert.False(File.Exists(sourceHdr));
        Assert.False(File.Exists(sourceEml));
        Assert.False(File.Exists(Path.Combine(input, basename + ".hdr.sort")));
        Assert.False(File.Exists(Path.Combine(input, basename + ".sort.err")));
        string[] records = ReadSharedText(logfile).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, records.Length);
        string result = Assert.Single(records, line => line.Contains("basename=\"" + basename + "\"", StringComparison.Ordinal));
        Assert.Contains("auth=\"auth@example.com\"", result);
        Assert.Contains("result=DIVERT", result);
        Assert.DoesNotContain("early@example.com", result);
        Assert.Equal(provisional, File.ReadAllBytes(orphanHdr));
        Assert.False(File.Exists(Path.Combine(input, "b-orphan.hdr.sort")));
        Assert.False(File.Exists(Path.Combine(input, "b-orphan.sort.err")));
        Assert.DoesNotContain("basename=\"b-orphan\"", ReadSharedText(logfile));

        sorter.Stop();
        Assert.Empty(await sorter.Error.WaitAsync(TimeSpan.FromSeconds(10)));
        string output = await sorter.Output.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("basename=\"b-orphan\"", output);
        Assert.Contains("result=DIVERT", output);
    }

    // A failed upstream HDR is retained once while the live sorter continues with later final EMLs.
    [ReleaseFact]
    public async Task PublishedSorterWatcherRetainsFailedHdrAndContinues()
    {
        using var fixture = new ProcessorFixture();
        string input = Path.Combine(fixture.SpoolDirectory, "proc");
        string logfile = Path.Combine(fixture.Root, "sorter-upstream-failure.log");
        string holding = fixture.FailedDirectory;
        var failed = fixture.WriteMessage("a-upstream-failed",
            ProcessorFixture.Header(auth: "not-enrolled@example.com").Replace("Written \r\n", "Failed \r\n"));
        MoveToSorter(fixture, "a-upstream-failed");
        var passed = fixture.WriteMessage("b-after-failed", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "b-after-failed");
        using var sorter = Start("sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, "-v", fixture.SpoolDirectory]);
        await WaitUntilAsync(() => File.Exists(logfile) && ReadSharedText(logfile)
            .Contains("basename=\"b-after-failed\" result=PASS", StringComparison.Ordinal), sorter);

        Assert.Equal(failed.Hdr, File.ReadAllBytes(Path.Combine(holding, "a-upstream-failed.hdr.sort")));
        Assert.Equal(failed.Eml, File.ReadAllBytes(Path.Combine(holding, "a-upstream-failed.eml")));
        Assert.True(File.Exists(Path.Combine(holding, "a-upstream-failed.sort.err")));
        Assert.False(File.Exists(Path.Combine(input, "a-upstream-failed.hdr")));
        Assert.False(File.Exists(Path.Combine(input, "a-upstream-failed.hdr.sort")));
        Assert.False(File.Exists(Path.Combine(input, "a-upstream-failed.eml")));
        Assert.False(File.Exists(Path.Combine(fixture.SpoolDirectory, "a-upstream-failed.hdr")));
        Assert.Equal(passed.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "b-after-failed.hdr")));
        Assert.Equal(passed.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "b-after-failed.eml")));

        fixture.WriteMessage("c-next-arrival", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "c-next-arrival");
        await WaitUntilAsync(() => ReadSharedText(logfile)
            .Contains("basename=\"c-next-arrival\" result=PASS", StringComparison.Ordinal) &&
            ReadSharedText(logfile).EndsWith("\r\n", StringComparison.Ordinal), sorter);
        string[] records = ReadSharedText(logfile).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, records.Length);
        string errorRecord = Assert.Single(records, line => line.Contains("result=ERROR", StringComparison.Ordinal));
        Assert.Contains("reason=\"UPSTREAM_FAILED\"", errorRecord);
        Assert.Contains("hdr=" + SmTagger.Shared.ConsoleErrors.Quote(Path.Combine(holding, "a-upstream-failed.hdr.sort")), errorRecord);
        Assert.Contains("eml=" + SmTagger.Shared.ConsoleErrors.Quote(Path.Combine(holding, "a-upstream-failed.eml")), errorRecord);
        Assert.False(sorter.Process.HasExited);

        sorter.Stop();
        Assert.NotEmpty(await sorter.Error.WaitAsync(TimeSpan.FromSeconds(10)));

        string restartedLog = Path.Combine(fixture.Root, "sorter-restarted.log");
        using FileStream lockedHeldEml = new(Path.Combine(holding, "a-upstream-failed.eml"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using FileStream lockedHeldHdr = new(Path.Combine(holding, "a-upstream-failed.hdr.sort"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var restarted = Start("sm-sorter", fixture,
            [fixture.DataDirectory, "-l", restartedLog, "-v", fixture.SpoolDirectory]);
        await WaitUntilAsync(() => OwnsLock(Path.Combine(input, "sm-sorter.lock")), restarted);
        fixture.WriteMessage("d-after-restart", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "d-after-restart");
        await WaitUntilAsync(() => File.Exists(restartedLog) && ReadSharedText(restartedLog)
            .Contains("basename=\"d-after-restart\" result=PASS", StringComparison.Ordinal)
            && ReadSharedText(restartedLog).EndsWith("\r\n", StringComparison.Ordinal), restarted);
        Assert.Single(ReadSharedText(restartedLog).Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        restarted.Stop();
        Assert.Empty(await restarted.Error.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // Real one-shot startup returns failure without claiming Writing, unknown, or incomplete-status input.
    [ReleaseFact]
    public async Task PublishedSorterOneShotRequiresWrittenStatus()
    {
        foreach (var (text, reason, observed) in new[]
        {
            ("Writing \t\r\nunfinished routing", "HDR_WRITING", "Writing"),
            ("Quarantined\r\nunfinished routing", "HDR_STATUS_UNEXPECTED", "Quarantined"),
            ("Written", "HDR_STATUS_UNEXPECTED", "<missing CRLF>")
        })
        {
            using var fixture = new ProcessorFixture();
            var original = fixture.WriteMessage("unready", text);
            MoveToSorter(fixture, "unready");
            string input = Path.Combine(fixture.SpoolDirectory, "proc");
            string logfile = Path.Combine(fixture.Root, "written-gate.log");

            var result = await RunAsync("sm-sorter", fixture,
                [fixture.DataDirectory, "-l", logfile, "-v", fixture.SpoolDirectory, "unready"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("NOT READY", result.Error);
            Assert.Contains(reason, result.Error);
            Assert.Contains(SmTagger.Shared.ConsoleErrors.Quote(observed), result.Error);
            Assert.Contains("event=DEFER", result.Output);
            Assert.Empty(File.ReadAllText(logfile));
            Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(input, "unready.hdr")));
            Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(input, "unready.eml")));
            Assert.False(File.Exists(Path.Combine(input, "unready.hdr.sort")));
            Assert.False(File.Exists(Path.Combine(input, "unready.sort.err")));
            Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
            Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
            Assert.False(Directory.Exists(fixture.FailedDirectory));
        }
    }

    // A nonverbose published watcher reports unknown status, skips unready pairs, and rechecks changed final status.
    [ReleaseFact]
    public async Task PublishedSorterWatcherRechecksStatusWithoutClaimingUnreadyPairs()
    {
        using var fixture = new ProcessorFixture();
        string input = Path.Combine(fixture.SpoolDirectory, "proc");
        string logfile = Path.Combine(fixture.Root, "status-watch.log");
        var writing = fixture.WriteMessage("a-writing", ProcessorFixture.Header(auth: "not-enrolled@example.com")
            .Replace("Written \r\n", "Writing \r\n", StringComparison.Ordinal));
        var unknown = fixture.WriteMessage("b-unknown", "Quarantined\r\nunfinished routing");
        MoveToSorter(fixture, "a-writing");
        MoveToSorter(fixture, "b-unknown");
        fixture.WriteMessage("z-first", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "z-first");
        using var sorter = Start("sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, fixture.SpoolDirectory]);
        await WaitUntilAsync(() => File.Exists(logfile) && ReadSharedText(logfile)
            .Contains("basename=\"z-first\" result=PASS", StringComparison.Ordinal), sorter);
        Assert.Equal(writing.Hdr, File.ReadAllBytes(Path.Combine(input, "a-writing.hdr")));
        Assert.Equal(writing.Eml, File.ReadAllBytes(Path.Combine(input, "a-writing.eml")));
        Assert.Equal(unknown.Hdr, File.ReadAllBytes(Path.Combine(input, "b-unknown.hdr")));
        Assert.Equal(unknown.Eml, File.ReadAllBytes(Path.Combine(input, "b-unknown.eml")));
        Assert.Empty(Directory.GetFiles(input, "*.hdr.sort"));
        Assert.Empty(Directory.GetFiles(input, "*.sort.err"));

        byte[] written = Encoding.ASCII.GetBytes(ProcessorFixture.Header("changed@example.net"));
        File.WriteAllBytes(Path.Combine(input, "a-writing.hdr"), written);
        fixture.WriteMessage("z-second", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "z-second");
        await WaitUntilAsync(() => ReadSharedText(logfile).Contains("basename=\"a-writing\" result=DIVERT", StringComparison.Ordinal)
            && ReadSharedText(logfile).Contains("basename=\"z-second\" result=PASS", StringComparison.Ordinal), sorter);
        Assert.Equal(written, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "a-writing.hdr")));
        Assert.Equal(writing.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "a-writing.eml")));
        Assert.Equal(unknown.Hdr, File.ReadAllBytes(Path.Combine(input, "b-unknown.hdr")));

        byte[] failed = "Failed \r\nmalformed final routing"u8.ToArray();
        File.WriteAllBytes(Path.Combine(input, "b-unknown.hdr"), failed);
        fixture.WriteMessage("z-third", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "z-third");
        await WaitUntilAsync(() => ReadSharedText(logfile).Contains("basename=\"b-unknown\" result=ERROR", StringComparison.Ordinal)
            && ReadSharedText(logfile).Contains("basename=\"z-third\" result=PASS", StringComparison.Ordinal)
            && ReadSharedText(logfile).EndsWith("\r\n", StringComparison.Ordinal), sorter);
        Assert.Equal(failed, File.ReadAllBytes(Path.Combine(fixture.FailedDirectory, "b-unknown.hdr.sort")));
        Assert.Equal(unknown.Eml, File.ReadAllBytes(Path.Combine(fixture.FailedDirectory, "b-unknown.eml")));
        Assert.False(File.Exists(Path.Combine(input, "b-unknown.hdr")));
        Assert.False(File.Exists(Path.Combine(input, "b-unknown.eml")));
        string[] records = ReadSharedText(logfile).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, records.Length);
        Assert.Contains("reason=\"UPSTREAM_FAILED\"", Assert.Single(records,
            line => line.Contains("basename=\"b-unknown\"", StringComparison.Ordinal)));

        sorter.Stop();
        string error = await sorter.Error.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(error.Split("HDR_STATUS_UNEXPECTED", StringSplitOptions.None).Length - 1 >= 2);
        Assert.Contains("\"Quarantined\"", error);
        Assert.DoesNotContain("HDR_WRITING", error);
        string output = await sorter.Output.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(output);
    }

    // One-shot readiness failures leave the input live and produce no terminal email-log record.
    [ReleaseFact]
    public async Task PublishedSorterOneShotWaitsForMissingEmlWithoutOwningHdr()
    {
        using var fixture = new ProcessorFixture();
        var original = fixture.WriteMessage("one-shot-ready", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        string input = Path.Combine(fixture.SpoolDirectory, "proc");
        string sourceHdr = Path.Combine(input, "one-shot-ready.hdr");
        string logfile = Path.Combine(fixture.Root, "one-shot-readiness.log");
        File.Move(Path.Combine(fixture.ProcessDirectory, "one-shot-ready.hdr"), sourceHdr);

        var waiting = await RunAsync("sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, "-v", fixture.SpoolDirectory, "one-shot-ready"]);

        Assert.Equal(1, waiting.ExitCode);
        Assert.Contains("NOT READY", waiting.Error);
        Assert.Contains("event=DEFER", waiting.Output);
        Assert.Empty(File.ReadAllText(logfile));
        Assert.Equal(original.Hdr, File.ReadAllBytes(sourceHdr));
        Assert.False(File.Exists(Path.Combine(input, "one-shot-ready.hdr.sort")));
        Assert.False(File.Exists(Path.Combine(input, "one-shot-ready.sort.err")));

        File.Move(Path.Combine(fixture.ProcessDirectory, "one-shot-ready.eml"), Path.Combine(input, "one-shot-ready.eml"));
        var completed = await RunAsync("sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, fixture.SpoolDirectory, "one-shot-ready"]);

        Assert.Equal(0, completed.ExitCode);
        Assert.Empty(completed.Error);
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "one-shot-ready.hdr")));
        Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "one-shot-ready.eml")));
        Assert.Contains("result=PASS", Assert.Single(File.ReadAllLines(logfile)));
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
        await WaitUntilAsync(() => OwnsLock(Path.Combine(fixture.DataDirectory, "sm-tagger.lock"))
            && OwnsLock(Path.Combine(fixture.WorkDirectory, "sm-tagger.lock")), restarted);
        fixture.WriteMessage("fresh");
        await WaitUntilAsync(() => File.Exists(Path.Combine(fixture.SpoolDirectory, "freshc1.hdr")), restarted);
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

    // Publish the final EML after its complete HDR to model the sorter's producer readiness signal.
    private static void MoveToSorter(ProcessorFixture fixture, string basename)
    {
        foreach (var extension in new[] { ".hdr", ".eml" })
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
