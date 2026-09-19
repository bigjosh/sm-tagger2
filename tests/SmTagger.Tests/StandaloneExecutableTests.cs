using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class StandaloneExecutableTests
{
    // Requires useful argument-count guidance before either real EXE can lock or modify its queues.
    [StandaloneTheory]
    [InlineData("sm-sorter", "empty")]
    [InlineData("sm-sorter", "datadir-only")]
    [InlineData("sm-sorter", "too-many")]
    [InlineData("sm-sorter", "sorter-log-without-path")]
    [InlineData("sm-sorter", "sorter-log-without-spool")]
    [InlineData("sm-sorter", "sorter-verbose-without-spool")]
    [InlineData("sm-tagger", "empty")]
    [InlineData("sm-tagger", "datadir-only")]
    [InlineData("sm-tagger", "too-many")]
    [InlineData("sm-tagger", "log-without-path")]
    [InlineData("sm-tagger", "log-without-spool")]
    [InlineData("sm-tagger", "verbose-without-spool")]
    [InlineData("sm-tagger", "obsolete-log")]
    [InlineData("sm-tagger", "duplicate-verbose")]
    [InlineData("sm-tagger", "duplicate-log")]
    [InlineData("sm-tagger", "keep-without-spool")]
    [InlineData("sm-tagger", "both-flags-without-spool")]
    public async Task InvalidArgumentCountsShowHintsWithoutTouchingQueues(string application, string scenario)
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, application);
        fixture.WriteMessage("process-ready");
        fixture.WriteMessage("sorter-ready");
        MoveToSorter(fixture, "sorter-ready");
        string[] before = SnapshotQueues(fixture);
        string[] arguments = scenario switch
        {
            "empty" => [],
            "datadir-only" => [fixture.DataDirectory],
            "too-many" => [fixture.DataDirectory, fixture.SpoolDirectory, "sorter-ready", "extra"],
            "sorter-log-without-path" => [fixture.DataDirectory, "-l"],
            "sorter-log-without-spool" => [fixture.DataDirectory, "-l", Path.Combine(fixture.DataDirectory, "sorter.log")],
            "sorter-verbose-without-spool" => [fixture.DataDirectory, "-v"],
            "log-without-path" => [fixture.DataDirectory, "-l"],
            "log-without-spool" => [fixture.DataDirectory, "-l", Path.Combine(fixture.Root, "tagger.log")],
            "verbose-without-spool" => [fixture.DataDirectory, "-v"],
            "obsolete-log" => [fixture.DataDirectory, "-log", fixture.SpoolDirectory, "process-ready"],
            "duplicate-verbose" => [fixture.DataDirectory, "-v", "-v", fixture.SpoolDirectory],
            "duplicate-log" => [fixture.DataDirectory, "-l", "first.log", "-l", "second.log", fixture.SpoolDirectory],
            "keep-without-spool" => [fixture.DataDirectory, "-keep"],
            "both-flags-without-spool" => [fixture.DataDirectory, "-l", Path.Combine(fixture.Root, "tagger.log"), "-v", "-keep"],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var result = await RunAsync(binaryDirectory, application, fixture, arguments);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.StartsWith("ERROR", result.Error);
        Assert.Contains($"Usage: {application}.exe ", result.Error);
        Assert.Equal(1, result.Error.Split("Usage:", StringSplitOptions.None).Length - 1);
        Assert.Contains("<datadir>", result.Error);
        Assert.Contains("<spooldir>", result.Error);
        Assert.Contains("<basename>", result.Error);
        Assert.Contains("Data root", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spool root", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("watch", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("process that pair and exit", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quote paths containing spaces", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.ArgumentException", result.Error);
        Assert.DoesNotContain("\n   at ", result.Error);
        if (application == "sm-tagger")
        {
            Assert.Contains("process", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[-l <logfile>]", result.Error);
            Assert.Contains("[-v]", result.Error);
            Assert.Contains("-keep", result.Error);
            Assert.Contains("execution trace", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("original and output copies", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("after <datadir>, before <spooldir>", result.Error);
        }
        else
        {
            Assert.Contains("proc", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[-l <logfile>]", result.Error);
            Assert.Contains("[-v]", result.Error);
            Assert.DoesNotContain("-log", result.Error);
            Assert.DoesNotContain("-keep", result.Error);
        }

        Assert.Equal(before, SnapshotQueues(fixture));
        Assert.False(File.Exists(Path.Combine(fixture.DataDirectory, "sm-tagger.lock")));
        Assert.False(File.Exists(Path.Combine(fixture.WorkDirectory, "sm-tagger.lock")));
        Assert.False(File.Exists(Path.Combine(fixture.SpoolDirectory, "proc", "sm-sorter.lock")));
    }

    // Proves one relocated sorter EXE preserves nonenrolled mail without any adjacent runtime files.
    [StandaloneFact]
    public async Task StandaloneSorterPassesNonenrolledMailByteForByte()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter");
        byte[] body = [0, 255, 13, 10, .. Encoding.ASCII.GetBytes("Opaque synthetic body")];
        var originals = fixture.WriteMessage("unrelated",
            ProcessorFixture.Header(auth: "not-enrolled@example.com"), body: body);
        MoveToSorter(fixture, "unrelated");

        var result = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "unrelated"]);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.Error);
        Assert.Empty(result.Output);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "unrelated.hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "unrelated.eml")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "tag-addresses")));
        Assert.False(File.Exists(Path.Combine(fixture.DataDirectory, "log.txt")));
        Assert.Equal(["sm-sorter.exe"], Directory.GetFiles(binaryDirectory).Select(Path.GetFileName));
    }

    // Appends one PASS record to existing history without enabling verbose stdout or altering mail bytes.
    [StandaloneFact]
    public async Task StandaloneSorterAppendsPassLogWithoutVerboseOutput()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter");
        string logfile = Path.Combine(fixture.Root, "sorter trace.txt");
        const string history = "Existing history is not parsed or replaced.";
        File.WriteAllText(logfile, history + "\r\n", new UTF8Encoding(false));
        var originals = fixture.WriteMessage("logged-pass", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "logged-pass");

        var result = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, fixture.SpoolDirectory, "logged-pass"]);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.Error);
        Assert.Empty(result.Output);
        string[] lines = File.ReadAllLines(logfile);
        Assert.Equal(2, lines.Length);
        Assert.Equal(history, lines[0]);
        AssertSorterLogRecord(lines[1], "PASS", "logged-pass");
        Assert.Contains("auth=\"not-enrolled@example.com\"", lines[1]);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "logged-pass.hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "logged-pass.eml")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
    }

    // Emits routing and move diagnostics with -v independently of whether an explicit logfile is requested.
    [StandaloneTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneSorterVerboseMovesWorkWithOrWithoutLogfile(bool writeLog)
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter");
        string logfile = Path.Combine(fixture.Root, "sorter trace.txt");
        var originals = fixture.WriteMessage("verbose-divert");
        MoveToSorter(fixture, "verbose-divert");
        string[] arguments = writeLog
            ? [fixture.DataDirectory, "-v", "-l", logfile, fixture.SpoolDirectory, "verbose-divert"]
            : [fixture.DataDirectory, "-v", fixture.SpoolDirectory, "verbose-divert"];

        var result = await RunAsync(binaryDirectory, "sm-sorter", fixture, arguments);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.Error);
        Assert.Contains("DEBUG", result.Output);
        Assert.Contains("event=ROUTE", result.Output);
        Assert.Contains("event=MOVE_INTENT", result.Output);
        Assert.Contains("event=MOVE_OK", result.Output);
        Assert.Contains("result=DIVERT", result.Output);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "verbose-divert.hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "verbose-divert.eml")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        if (writeLog)
        {
            string record = Assert.Single(File.ReadAllLines(logfile));
            AssertSorterLogRecord(record, "DIVERT", "verbose-divert");
            Assert.Contains("auth=\"auth@example.com\"", record);
        }
        else
        {
            Assert.False(File.Exists(logfile));
            Assert.False(File.Exists(Path.Combine(fixture.DataDirectory, "log.txt")));
        }
    }

    // The real standalone sorter returns one-shot failure after preserving an upstream-rejected pair in private Proc storage.
    [StandaloneFact]
    public async Task StandaloneSorterMovesUpstreamFailureOutsideSpool()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter");
        string logfile = Path.Combine(fixture.Root, "sorter-upstream-failed.log");
        var originals = fixture.WriteMessage("rejected",
            "Failed \t\r\nmalformed remainder need not be parsed\n", body: [0, 255, 13, 10]);
        MoveToSorter(fixture, "rejected");
        string holding = fixture.FailedDirectory;
        Assert.False(Directory.Exists(holding));

        var result = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, fixture.SpoolDirectory, "rejected"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(holding, "rejected.hdr.sort")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(holding, "rejected.eml")));
        Assert.True(File.Exists(Path.Combine(holding, "rejected.sort.err")));
        Assert.Contains(SmTagger.Shared.ConsoleErrors.Quote(Path.Combine(holding, "rejected.hdr.sort")), result.Error);
        Assert.Contains(SmTagger.Shared.ConsoleErrors.Quote(Path.Combine(holding, "rejected.eml")), result.Error);
        string record = Assert.Single(File.ReadAllLines(logfile));
        AssertSorterLogRecord(record, "ERROR", "rejected");
        Assert.Contains("reason=\"UPSTREAM_FAILED\"", record);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory, "*.hdr"));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory, "*.eml"));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.SpoolDirectory, "proc"), "rejected.*"));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
    }

    // Records held mail as ERROR while stderr remains active and both original message files stay inert.
    [StandaloneFact]
    public async Task StandaloneSorterLogsHeldErrorWithoutVerboseOutput()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter");
        string logfile = Path.Combine(fixture.Root, "sorter trace.txt");
        var originals = fixture.WriteMessage("held", ProcessorFixture.Header(auth: ""));
        MoveToSorter(fixture, "held");

        var result = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, fixture.SpoolDirectory, "held"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("held", result.Error, StringComparison.OrdinalIgnoreCase);
        AssertSorterLogRecord(Assert.Single(File.ReadAllLines(logfile)), "ERROR", "held");
        string queue = Path.Combine(fixture.SpoolDirectory, "proc");
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(queue, "held.hdr.sort")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(queue, "held.eml")));
        Assert.True(File.Exists(Path.Combine(queue, "held.sort.err")));
        Assert.False(File.Exists(Path.Combine(queue, "held.hdr")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // A real logfile-open failure reports to stderr but cannot prevent the same message from passing.
    [StandaloneFact]
    public async Task StandaloneSorterLogOpenFailureDoesNotHoldMail()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter");
        string logfile = Path.Combine(fixture.Root, "unwritable sorter log.txt");
        Directory.CreateDirectory(logfile);
        var originals = fixture.WriteMessage("pass-after-log-error", ProcessorFixture.Header(auth: "not-enrolled@example.com"));
        MoveToSorter(fixture, "pass-after-log-error");

        var result = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, "-l", logfile, fixture.SpoolDirectory, "pass-after-log-error"]);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.Output);
        Assert.Contains("ERROR logging to", result.Error);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "pass-after-log-error.hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "pass-after-log-error.eml")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.SpoolDirectory, "proc"), "*.sort.err"));
        Assert.True(Directory.Exists(logfile));
    }

    // Exercises relocated EXEs through byte-preserving diversion, real rewriting, and two-recipient fan-out.
    [StandaloneFact]
    public async Task StandaloneExecutablesDivertAndRewriteEnrolledMail()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter", "sm-tagger");
        byte[] body = [0, 255, 13, 10, 10, .. Encoding.ASCII.GetBytes("private@example.com stays in the body")];
        var originals = fixture.WriteMessage("-standalone",
            ProcessorFixture.Header("bob@example.net,alice@example.net"), body: body);
        MoveToSorter(fixture, "-standalone");

        var sorter = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "-standalone"]);
        Assert.True(sorter.ExitCode == 0, sorter.Error);
        Assert.Empty(sorter.Error);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-standalone.hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-standalone.eml")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));

        var tagger = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, "-keep", fixture.SpoolDirectory, "-standalone"]);
        Assert.True(tagger.ExitCode == 0, tagger.Error);
        Assert.Empty(tagger.Error);
        Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.hdr").Length);
        Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.eml").Length);
        using var loaded = fixture.Open();
        Assert.Equal(3, loaded.Tags.Mappings.Count);
        string group = loaded.Tags.Mappings[(ProcessorFixture.SenderId, "alice@example.net;bob@example.net;")].TagAddress;
        string[] recipients = ["alice@example.net", "bob@example.net"];
        for (int index = 0; index < recipients.Length; index++)
        {
            string individual = loaded.Tags.Mappings[(ProcessorFixture.SenderId, recipients[index] + ";")].TagAddress;
            string basename = "-standalonec" + (index + 1);
            Assert.Equal(Encoding.ASCII.GetBytes(ProcessorFixture.Header(recipients[index], individual)),
                File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, basename + ".hdr")));
            string expectedHeaders = ProcessorFixture.Message()
                .Replace("Return-Path: <private@example.com>\r\n", "", StringComparison.Ordinal)
                .Replace("From: \"Fixture Sender\" <private@example.com>\r\n",
                    $"From: \"Fixture Sender\" <{individual}>\r\nReply-To: \"Fixture Sender\" <{group}>\r\n",
                    StringComparison.Ordinal);
            byte[] expectedEml = [.. Encoding.ASCII.GetBytes(expectedHeaders), .. body];
            Assert.Equal(expectedEml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, basename + ".eml")));
        }

        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-standalone.hdr.in")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-standalone.eml.in")));
        Assert.Equal(["sm-sorter.exe", "sm-tagger.exe"],
            Directory.GetFiles(binaryDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // Both standalone programs support descriptive sender keys while separate tagger processes reuse one alias-shared tag.
    [StandaloneTheory]
    [InlineData("Sales Team Émail")]
    [InlineData(ProcessorFixture.SenderId)]
    public async Task StandaloneExecutablesReuseSenderIdAcrossAliasesAndRestarts(string senderId)
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter", "sm-tagger");
        if (senderId != ProcessorFixture.SenderId)
            Directory.Move(fixture.ProfileDirectory, Path.Combine(fixture.DataDirectory, "senders", "sender-ids", senderId));
        string aliasDirectory = Path.Combine(fixture.DataDirectory, "senders", "auth-addresses", "alias@example.com");
        Directory.CreateDirectory(aliasDirectory);
        File.WriteAllText(Path.Combine(aliasDirectory, "sender-id.txt"), senderId, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.DataDirectory, "senders", "auth-addresses", ProcessorFixture.Auth, "sender-id.txt"),
            senderId, new UTF8Encoding(false));
        byte[] body = [0, 255, 13, 10, .. Encoding.ASCII.GetBytes("Unchanged synthetic body.")];
        string? originalTag = null;
        string[] auths = [ProcessorFixture.Auth, "alias@example.com"];
        for (int index = 0; index < auths.Length; index++)
        {
            string basename = "sender-key-" + index;
            var original = fixture.WriteMessage(basename, ProcessorFixture.Header(auth: auths[index]), body: body);
            MoveToSorter(fixture, basename);
            var sorter = await RunAsync(binaryDirectory, "sm-sorter", fixture,
                [fixture.DataDirectory, fixture.SpoolDirectory, basename]);
            Assert.True(sorter.ExitCode == 0, sorter.Error);
            Assert.Empty(sorter.Error);
            Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, basename + ".hdr")));
            Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, basename + ".eml")));

            var tagger = await RunAsync(binaryDirectory, "sm-tagger", fixture,
                [fixture.DataDirectory, fixture.SpoolDirectory, basename]);
            Assert.True(tagger.ExitCode == 0, tagger.Error);
            Assert.Empty(tagger.Error);
            Assert.Empty(tagger.Output);
            string mappingDirectory = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.DataDirectory, "tag-addresses")));
            string tag = Path.GetFileName(mappingDirectory);
            originalTag ??= tag;
            Assert.Equal(originalTag, tag);
            Assert.Equal(Encoding.UTF8.GetBytes(senderId), File.ReadAllBytes(Path.Combine(mappingDirectory, "sender-id.txt")));
            Assert.Equal("alice@example.net;"u8.ToArray(), File.ReadAllBytes(Path.Combine(mappingDirectory, "recipient-id.txt")));
            Assert.Equal(Encoding.ASCII.GetBytes(ProcessorFixture.Header(sender: tag, auth: auths[index])),
                File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, basename + "c1.hdr")));
            string expectedHeaders = ProcessorFixture.Message()
                .Replace("Return-Path: <private@example.com>\r\n", "", StringComparison.Ordinal)
                .Replace("From: \"Fixture Sender\" <private@example.com>\r\n",
                    $"From: \"Fixture Sender\" <{tag}>\r\n", StringComparison.Ordinal);
            Assert.Equal([.. Encoding.ASCII.GetBytes(expectedHeaders), .. body],
                File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, basename + "c1.eml")));
            Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        }
    }

    // Equal auth/private identities still tag sender fields, preserve auth metadata, and ignore auth-only matches.
    [StandaloneFact]
    public async Task StandaloneExecutablesAllowSameSenderAuthAndPrivateAddress()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter", "sm-tagger");
        fixture.WriteProfile("private-address.txt", "AUTH@EXAMPLE.COM");
        byte[] body = [0, 255, .. Encoding.ASCII.GetBytes("Synthetic unchanged body.\r\n")];
        string headers = ProcessorFixture.Message().Replace(ProcessorFixture.Private, ProcessorFixture.Auth, StringComparison.Ordinal);
        fixture.WriteMessage("same-identity", ProcessorFixture.Header(sender: ProcessorFixture.Auth), headers, body);
        MoveToSorter(fixture, "same-identity");

        var sorter = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "same-identity"]);
        Assert.True(sorter.ExitCode == 0, sorter.Error);
        var tagger = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "same-identity"]);
        Assert.True(tagger.ExitCode == 0, tagger.Error);
        Assert.Empty(tagger.Error);
        string mapping = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.DataDirectory, "tag-addresses")));
        string tag = Path.GetFileName(mapping);
        Assert.Equal(Encoding.ASCII.GetBytes(ProcessorFixture.Header(sender: tag)),
            File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "same-identityc1.hdr")));
        string taggedHeaders = headers.Replace("Return-Path: <auth@example.com>\r\n", "", StringComparison.Ordinal)
            .Replace("From: \"Fixture Sender\" <auth@example.com>\r\n",
                $"From: \"Fixture Sender\" <{tag}>\r\n", StringComparison.Ordinal);
        Assert.Equal([.. Encoding.ASCII.GetBytes(taggedHeaders), .. body],
            File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "same-identityc1.eml")));

        var unchanged = fixture.WriteMessage("auth-only", body: body);
        MoveToSorter(fixture, "auth-only");
        var passSorter = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "auth-only"]);
        Assert.True(passSorter.ExitCode == 0, passSorter.Error);
        var passTagger = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "auth-only"]);
        Assert.True(passTagger.ExitCode == 0, passTagger.Error);
        Assert.Empty(passTagger.Error);
        Assert.Equal(unchanged.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "auth-only.hdr")));
        Assert.Equal(unchanged.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "auth-only.eml")));
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.DataDirectory, "tag-addresses")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
    }

    // Startup failures render real stack lines on the console while the selected trace retains one line per event.
    [StandaloneFact]
    public async Task StandaloneTaggerPrintsReadableExceptionsWithoutDoubleEscapingFileTrace()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-tagger");
        var original = fixture.WriteMessage("queued");
        fixture.WriteProfile("allow-mdn.txt", "invalid-policy");
        string logPath = Path.Combine(fixture.Root, "fatal-trace.txt");

        var result = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, "-l", logPath, "-v", fixture.SpoolDirectory, "queued"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("\n   at ", result.Error);
        Assert.Contains("\n   at ", result.Output);
        Assert.DoesNotContain("\\r\\n", result.Error);
        Assert.DoesNotContain("\\r\\n", result.Output);
        Assert.Contains(fixture.ProfileDirectory, result.Error);
        Assert.Contains(fixture.ProfileDirectory, result.Output);
        string fatal = Assert.Single(File.ReadAllLines(logPath), line => line.Contains("event=FATAL", StringComparison.Ordinal));
        Assert.Contains("\\r\\n   at ", fatal);
        Assert.DoesNotContain("\\\\r\\\\n", fatal);
        Assert.DoesNotContain("exception=\"\\\"", fatal);
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "queued.hdr")));
        Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "queued.eml")));
        Assert.Equal(2, Directory.GetFiles(fixture.ProcessDirectory).Length);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // Unsafe persisted references stop the real process before it claims or changes queued mail.
    [StandaloneTheory]
    [InlineData("auth")]
    [InlineData("mapping")]
    public async Task StandaloneTaggerRejectsUnsafeSenderIdBeforeMessageWork(string source)
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-tagger");
        var original = fixture.WriteMessage("queued");
        string identity = source == "mapping"
            ? Path.Combine(fixture.WriteMapping("tag-11111@reply.example.com", "alice@example.net;"), "sender-id.txt")
            : Path.Combine(fixture.DataDirectory, "senders", "auth-addresses", ProcessorFixture.Auth, "sender-id.txt");
        byte[] unsafeIdentity = "..\\outside"u8.ToArray();
        File.WriteAllBytes(identity, unsafeIdentity);

        var result = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "queued"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("sender", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unsafeIdentity, File.ReadAllBytes(identity));
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "queued.hdr")));
        Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "queued.eml")));
        Assert.Equal(2, Directory.GetFiles(fixture.ProcessDirectory).Length);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "staging")));
    }

    // Published diagnostics independently select file/console output without changing retained originals or routing.
    [StandaloneTheory]
    [InlineData("none")]
    [InlineData("file")]
    [InlineData("verbose")]
    [InlineData("both")]
    public async Task StandaloneTaggerHonorsIndependentDiagnosticOptions(string mode)
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-tagger");
        var originals = fixture.WriteMessage("-log");
        string relativeLog = "selected tagger trace.txt";
        string chosenLog = Path.Combine(fixture.Root, relativeLog);
        bool file = mode is "file" or "both";
        bool verbose = mode is "verbose" or "both";
        if (file)
            File.WriteAllText(chosenLog, "previous history\r\n");
        string oldDefault = Path.Combine(fixture.DataDirectory, "log.txt");
        byte[] priorDefault = "old default remains untouched\r\n"u8.ToArray();
        File.WriteAllBytes(oldDefault, priorDefault);
        var arguments = new List<string> { fixture.DataDirectory, "-keep" };
        if (file)
            arguments.AddRange(["-l", relativeLog]);
        if (verbose)
            arguments.Add("-v");
        arguments.AddRange([fixture.SpoolDirectory, "-log"]);

        var result = await RunAsync(binaryDirectory, "sm-tagger", fixture, arguments.ToArray());

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.Error);
        Assert.Equal(priorDefault, File.ReadAllBytes(oldDefault));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "-logc1.hdr")));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "-logc1.eml")));
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-log.hdr.in")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-log.eml.in")));
        Assert.Equal(file, File.Exists(chosenLog));
        if (verbose)
        {
            Assert.Contains("DEBUG ", result.Output);
            Assert.Contains("event=STARTUP", result.Output);
            Assert.Contains("event=STATE_INTENT", result.Output);
            Assert.Contains("event=STATE_RESULT", result.Output);
            Assert.Contains("event=SHUTDOWN", result.Output);
        }
        else
        {
            Assert.Empty(result.Output);
        }
        if (file)
        {
            string trace = File.ReadAllText(chosenLog);
            Assert.StartsWith("previous history\r\n", trace);
            Assert.Contains("event=STARTUP", trace);
            Assert.Contains("event=SHUTDOWN", trace);
            string[] records = trace.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            Assert.All(records, record => Assert.DoesNotContain("DEBUG ", record));
            if (verbose)
            {
                string[] consoleRecords = result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(records.Select(record => Regex.Match(record, @"^.*? result=[A-Z_]+").Value),
                    consoleRecords.Select(record => Regex.Match(record, @"^DEBUG (.*? result=[A-Z_]+)").Groups[1].Value));
                Assert.Contains(fixture.DataDirectory, result.Output);
                Assert.Contains(fixture.SpoolDirectory, result.Output);
            }
        }
    }

    // Both real startup locks gate optional file and console output as well as message ownership.
    [StandaloneTheory]
    [InlineData("data")]
    [InlineData("queue")]
    public async Task StandaloneTaggerRejectsContentionBeforeOpeningEitherDiagnosticSink(string lockKind)
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-tagger");
        var original = fixture.WriteMessage("queued");
        string lockPath = Path.Combine(lockKind == "data" ? fixture.DataDirectory : fixture.WorkDirectory, "sm-tagger.lock");
        string logPath = Path.Combine(fixture.Root, "chosen trace.txt");
        byte[] prior = "existing trace bytes\r\n"u8.ToArray();
        File.WriteAllBytes(logPath, prior);
        using SingletonLock owner = SingletonLock.Acquire(lockPath);

        var result = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, "-v", "-l", logPath, fixture.SpoolDirectory, "queued"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("lock", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(prior, File.ReadAllBytes(logPath));
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "queued.hdr")));
        Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "queued.eml")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "tag-addresses")));
    }

    // Checks retained originals on a real contract failure and continued publication when logging cannot open.
    [StandaloneFact]
    public async Task StandaloneTaggerRetainsInvalidFromAndSurvivesLoggingFailure()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-tagger");
        Directory.CreateDirectory(Path.Combine(fixture.DataDirectory, "log.txt"));
        var originals = fixture.WriteMessage("invalid", eml: ProcessorFixture.Message(""));

        var invalid = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, "-l", Path.Combine(fixture.DataDirectory, "log.txt"), "-v", fixture.SpoolDirectory, "invalid"]);

        Assert.Equal(1, invalid.ExitCode);
        Assert.Contains("exactly one From", invalid.Error);
        Assert.Contains("ERROR logging to", invalid.Error);
        Assert.Contains("event=STATE_RESULT", invalid.Output);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "invalid.hdr.err")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "invalid.eml.err")));
        Assert.Contains("exactly one From", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "invalid.err")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "tag-addresses")));

        fixture.WriteMessage("valid");
        var valid = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, "-l", Path.Combine(fixture.DataDirectory, "log.txt"), "-v", fixture.SpoolDirectory, "valid"]);

        Assert.True(valid.ExitCode == 0, valid.Error);
        Assert.Contains("ERROR logging to", valid.Error);
        Assert.Contains("event=SHUTDOWN", valid.Output);
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "validc1.hdr")));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "validc1.eml")));
        Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, "valid.err")));
    }

    // Checks the terminal result and quoted message identity without depending on timestamps or explanatory wording.
    private static void AssertSorterLogRecord(string record, string result, string basename)
    {
        Assert.Contains("result=" + result, record);
        Assert.Contains("basename=\"" + basename + "\"", record);
        Assert.Contains("reason=\"", record);
    }

    // Copies only requested EXEs into a fresh isolated directory, leaving all publish-folder sidecars behind.
    private static string CopyExecutables(ProcessorFixture fixture, params string[] applications)
    {
        string directory = Path.Combine(fixture.Root, "standalone-bin");
        Directory.CreateDirectory(directory);
        foreach (string application in applications)
        {
            File.Copy(Path.Combine(ReleaseFactAttribute.ReleaseDirectory("standalone"), application + ".exe"),
                Path.Combine(directory, application + ".exe"));
        }

        return directory;
    }

    // Records every protected directory and file hash so invalid invocations cannot silently mutate evidence.
    private static string[] SnapshotQueues(ProcessorFixture fixture)
    {
        return new[] { fixture.DataDirectory, fixture.SpoolDirectory }
            .SelectMany(root => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            .Select(path => File.Exists(path)
                ? "file " + Path.GetRelativePath(fixture.Root, path) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                : "directory " + Path.GetRelativePath(fixture.Root, path))
            .Order(StringComparer.Ordinal).ToArray();
    }

    // Publishes the synthetic input to the sorter queue with EML present before its plain HDR.
    private static void MoveToSorter(ProcessorFixture fixture, string basename)
    {
        foreach (string extension in new[] { ".eml", ".hdr" })
        {
            File.Move(Path.Combine(fixture.ProcessDirectory, basename + extension),
                Path.Combine(fixture.SpoolDirectory, "proc", basename + extension));
        }
    }

    // Launches a relocated bundle with bounded lifetime, drained pipes, and its own fresh extraction root.
    private static async Task<ProcessResult> RunAsync(string binaryDirectory, string application,
        ProcessorFixture fixture, string[] arguments)
    {
        string extractionDirectory = Path.Combine(fixture.Root, "extract-" + Guid.NewGuid().ToString("N"));
        string hostTracePath = extractionDirectory + ".host-trace.txt";
        Directory.CreateDirectory(extractionDirectory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(extractionDirectory));
        var info = new ProcessStartInfo(Path.Combine(binaryDirectory, application + ".exe"))
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (string variable in info.Environment.Keys.Where(name => name.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            info.Environment.Remove(variable);
        }

        info.Environment.Remove("DOTNET_HOST_PATH");
        info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extractionDirectory;
        info.Environment["DOTNET_HOST_TRACE"] = "1";
        info.Environment["DOTNET_HOST_TRACEFILE"] = hostTracePath;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start standalone executable.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10));
            var result = new ProcessResult(process.ExitCode, await output, await error);
            string hostTrace = File.Exists(hostTracePath) ? File.ReadAllText(hostTracePath) : "";
            string details = $"Standalone {application} exit code: {result.ExitCode}. " +
                $"Standard error: {result.Error}. Standard output: {result.Output}. " +
                "Host trace: " + string.Join(Environment.NewLine, hostTrace.Split('\n').Where(line =>
                    line.Contains("bundle", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("self-contained", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("CoreCLR", StringComparison.Ordinal)).TakeLast(20));
            Assert.True(hostTrace.Contains("Detected Single-File app bundle", StringComparison.Ordinal), details);
            Assert.True(hostTrace.Contains("Executing as a self-contained app", StringComparison.Ordinal), details);
            // Some runtime packs embed CoreCLR in the native host instead of extracting a separate DLL.
            bool extractedRuntime = Directory.EnumerateFiles(extractionDirectory, "coreclr.dll", SearchOption.AllDirectories).Any();
            bool embeddedRuntime = hostTrace.Contains("CoreCLR path = '', CoreCLR dir = ''", StringComparison.Ordinal);
            Assert.True(extractedRuntime || embeddedRuntime, details);
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }

            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

internal sealed class StandaloneFactAttribute : FactAttribute
{
    // Skips explicitly until both Windows standalone release bundles have been published.
    public StandaloneFactAttribute()
    {
        Skip = UnavailableReason();
    }

    // Shares the published-bundle prerequisite between facts and parameterized executable checks.
    internal static string? UnavailableReason()
    {
        string directory = ReleaseFactAttribute.ReleaseDirectory("standalone");
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(directory, "sm-sorter.exe")) ||
            !File.Exists(Path.Combine(directory, "sm-tagger.exe")))
        {
            return "Run scripts/Publish-Release.ps1 on Windows before standalone executable checks.";
        }

        return null;
    }
}

internal sealed class StandaloneTheoryAttribute : TheoryAttribute
{
    // Applies the same explicit artifact skip policy to every argument-count case.
    public StandaloneTheoryAttribute()
    {
        Skip = StandaloneFactAttribute.UnavailableReason();
    }
}
