using System.Text;
using SmSorter;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class SorterDiagnosticsTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 9, 16, 12, 34, 56, TimeSpan.Zero);

    // Record the actual completed handoff once without reading or changing the message bytes.
    [Theory]
    [InlineData("no-auth", "PASS", "-")]
    [InlineData("unenrolled", "PASS", "account@example.com")]
    [InlineData("enrolled", "DIVERT", "account@example.com")]
    [InlineData("unenrollable", "PASS", "account/part@example.com")]
    public void SuccessfulHandoffRecordsOneTerminalResult(string arrangement, string result, string auth)
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        using MemoryStream bytes = new();
        using SorterDiagnostics diagnostics = Open(tree, bytes, output, verbose: true);
        string metadata = arrangement switch
        {
            "no-auth" => "",
            "unenrollable" => "auth: Account/Part@Example.com\r\n",
            _ => "auth: Account@Example.com\r\n"
        };
        if (arrangement == "enrolled")
            Directory.CreateDirectory(tree.Data("senders/auth-addresses/account@example.com"));
        else if (arrangement == "unenrolled")
            Directory.CreateDirectory(tree.Data("senders/auth-addresses"));
        byte[] hdr = tree.AddMessage("message", metadata);
        byte[] eml = [0, 255, 13, 10, 42];
        File.WriteAllBytes(tree.Input("message.eml"), eml);
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);

        using (FileStream unreadableEml = new(tree.Input("message.eml"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.Delete))
        {
            Assert.Equal(MessageOutcome.Succeeded, processor.Process("message"));
        }

        string destination = arrangement == "enrolled" ? tree.Work("process") : tree.Spool("");
        Assert.Equal(hdr, File.ReadAllBytes(Path.Combine(destination, "message.hdr")));
        Assert.Equal(eml, File.ReadAllBytes(Path.Combine(destination, "message.eml")));
        string line = Assert.Single(Lines(bytes));
        Assert.StartsWith("2026-09-16T12:34:56.000Z ", line);
        Assert.Contains("basename=\"message\"", line);
        Assert.Contains("result=" + result, line);
        Assert.Contains("auth=" + ConsoleErrors.Quote(auth), line);
        Assert.Contains("hdr=" + ConsoleErrors.Quote(Path.Combine(destination, "message.hdr")), line);
        Assert.Contains("eml=" + ConsoleErrors.Quote(Path.Combine(destination, "message.eml")), line);
        Assert.DoesNotContain("DEBUG", line);
        Assert.Contains("DEBUG", output.ToString());
        Assert.Empty(tree.Errors.ToString());
    }

    // A disabled logger has no file, formatting, or stdout dependency even during normal routing.
    [Fact]
    public void DisabledDiagnosticsDoNotOpenOrFormatAnything()
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        using SorterDiagnostics diagnostics = SorterDiagnostics.OpenForTesting(null, false, output, tree.Errors,
            () => throw new InvalidOperationException("Clock must not be used."),
            _ => throw new IOException("File must not be opened."));
        tree.AddMessage("plain");
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);

        Assert.Equal(MessageOutcome.Succeeded, processor.Process("plain"));
        Assert.Empty(output.ToString());
        Assert.Empty(tree.Errors.ToString());
        Assert.False(File.Exists(tree.Data("sorter.log")));
        Assert.False(File.Exists(tree.Data("log.txt")));
    }

    // Unsafe classification, uncertain enrollment, and failed queue creation retain the source EML.
    [Theory]
    [InlineData("unsafe")]
    [InlineData("missing-root")]
    [InlineData("process-is-file")]
    public void HeldMessagesRecordTheOwnedHdrAndUnmovedEml(string arrangement)
    {
        using SorterTestDirectory tree = new();
        using MemoryStream bytes = new();
        using SorterDiagnostics diagnostics = Open(tree, bytes);
        byte[] hdr = tree.AddMessage("held", arrangement == "unsafe" ? "auth:\r\n" : "auth: account@example.com\r\n");
        byte[] eml = File.ReadAllBytes(tree.Input("held.eml"));
        if (arrangement == "process-is-file")
        {
            Directory.CreateDirectory(tree.Data("senders/auth-addresses/account@example.com"));
            Directory.CreateDirectory(tree.Work(""));
            File.WriteAllText(tree.Work("process"), "existing evidence");
        }
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);

        Assert.Equal(MessageOutcome.Failed, processor.Process("held", watchMode: true));
        Assert.Equal(hdr, File.ReadAllBytes(tree.Input("held.hdr.sort")));
        Assert.Equal(eml, File.ReadAllBytes(tree.Input("held.eml")));
        Assert.True(File.Exists(tree.Input("held.sort.err")));
        string line = Assert.Single(Lines(bytes));
        Assert.Contains("result=ERROR", line);
        Assert.Contains("hdr=" + ConsoleErrors.Quote(tree.Input("held.hdr.sort")), line);
        Assert.Contains("eml=" + ConsoleErrors.Quote(tree.Input("held.eml")), line);
        if (arrangement == "process-is-file")
        {
            Assert.Contains("operation=\"create process queue directory\"", line);
            Assert.Equal("existing evidence", File.ReadAllText(tree.Work("process")));
        }
    }

    // A failed final HDR move reports the moved EML's real destination for either routing decision.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HdrCollisionRecordsPartialHandoffWithoutAFalseSuccess(bool divert)
    {
        using SorterTestDirectory tree = new();
        using MemoryStream bytes = new();
        using SorterDiagnostics diagnostics = Open(tree, bytes);
        byte[] hdr = tree.AddMessage("partial", divert ? "auth: account@example.com\r\n" : "");
        byte[] eml = File.ReadAllBytes(tree.Input("partial.eml"));
        string destination = divert ? tree.Work("process") : tree.Spool("");
        if (divert)
        {
            Directory.CreateDirectory(tree.Data("senders/auth-addresses/account@example.com"));
            Directory.CreateDirectory(destination);
        }
        string destinationHdr = Path.Combine(destination, "partial.hdr");
        string destinationEml = Path.Combine(destination, "partial.eml");
        File.WriteAllText(destinationHdr, "existing destination");
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);

        Assert.Equal(MessageOutcome.Failed, processor.Process("partial"));
        Assert.Equal(hdr, File.ReadAllBytes(tree.Input("partial.hdr.sort")));
        Assert.Equal(eml, File.ReadAllBytes(destinationEml));
        Assert.False(File.Exists(tree.Input("partial.eml")));
        Assert.Equal("existing destination", File.ReadAllText(destinationHdr));
        string line = Assert.Single(Lines(bytes));
        Assert.Contains("result=ERROR", line);
        Assert.Contains("operation=\"publish HDR\"", line);
        Assert.Contains("hdr=" + ConsoleErrors.Quote(tree.Input("partial.hdr.sort")), line);
        Assert.Contains("eml=" + ConsoleErrors.Quote(destinationEml), line);
        Assert.Contains("destination=" + ConsoleErrors.Quote(destinationHdr), line);
        Assert.DoesNotContain("result=PASS", line);
        Assert.DoesNotContain("result=DIVERT", line);
    }

    // A first ownership failure remains fatal and records the still-live source without a parent diagnostic.
    [Fact]
    public void OwnershipCollisionRecordsOneErrorAndStillThrows()
    {
        using SorterTestDirectory tree = new();
        using MemoryStream bytes = new();
        using SorterDiagnostics diagnostics = Open(tree, bytes);
        byte[] hdr = tree.AddMessage("collision");
        File.WriteAllText(tree.Input("collision.hdr.sort"), "existing residual");
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);

        Assert.Throws<FatalProcessingException>(() => processor.Process("collision", watchMode: true));
        Assert.Equal(hdr, File.ReadAllBytes(tree.Input("collision.hdr")));
        Assert.True(File.Exists(tree.Input("collision.eml")));
        Assert.Equal("existing residual", File.ReadAllText(tree.Input("collision.hdr.sort")));
        Assert.False(File.Exists(tree.Input("collision.sort.err")));
        string line = Assert.Single(Lines(bytes));
        Assert.Contains("result=ERROR", line);
        Assert.Contains("hdr=" + ConsoleErrors.Quote(tree.Input("collision.hdr")), line);
        Assert.Contains("eml=" + ConsoleErrors.Quote(tree.Input("collision.eml")), line);
        Assert.Contains("destination=" + ConsoleErrors.Quote(tree.Input("collision.hdr.sort")), line);
    }

    // Stale discovery and old residuals are not message results; a missing one-shot request is an error.
    [Fact]
    public void MissingOneShotIsLoggedButStaleDiscoveryAndResidualsAreNot()
    {
        using SorterTestDirectory tree = new();
        using MemoryStream bytes = new();
        using SorterDiagnostics diagnostics = Open(tree, bytes);
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);
        Assert.Equal(MessageOutcome.Stale, processor.Process("gone", watchMode: true));
        Assert.Empty(Lines(bytes));
        Assert.Empty(tree.Errors.ToString());
        File.WriteAllText(tree.Input("old.hdr.sort"), "retained");
        processor.ReportResiduals();
        Assert.Empty(Lines(bytes));

        File.WriteAllText(tree.Input("gone.eml"), "selected final EML");
        Assert.Equal(MessageOutcome.Failed, processor.Process("gone"));
        string line = Assert.Single(Lines(bytes));
        Assert.Contains("result=ERROR", line);
        Assert.Contains("basename=\"gone\"", line);
        Assert.False(File.Exists(tree.Input("gone.sort.err")));
    }

    // Failure of the separate parent diagnostic must not duplicate the message's terminal log entry.
    [Fact]
    public void DiagnosticCollisionKeepsExactlyOneTerminalError()
    {
        using SorterTestDirectory tree = new();
        using MemoryStream bytes = new();
        using SorterDiagnostics diagnostics = Open(tree, bytes);
        tree.AddMessage("held", "auth:\r\n");
        File.WriteAllText(tree.Input("held.sort.err"), "previous evidence");
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);

        Assert.Equal(MessageOutcome.Failed, processor.Process("held"));
        Assert.Contains("result=ERROR", Assert.Single(Lines(bytes)));
        Assert.Equal("previous evidence", File.ReadAllText(tree.Input("held.sort.err")));
        Assert.Contains("Cannot write sorter diagnostic", tree.Errors.ToString());
    }

    // Preserve any old malformed tail while appending UTF-8 terminal lines without a BOM or debug events.
    [Fact]
    public void RealFileAppendPreservesExistingBytesAndEscapesRecordValues()
    {
        using SorterTestDirectory tree = new();
        string path = tree.Data("sorter.log");
        byte[] original = [255, 0, 97];
        File.WriteAllBytes(path, original);
        using (SorterDiagnostics diagnostics = SorterDiagnostics.Open(path, false, stderr: tree.Errors))
        {
            diagnostics.Debug("ignored", "IGNORED", ("value", "not a file event"));
            diagnostics.Message("message\r\nforged", "ERROR", "account@example.com", "reason\r\nnext", "operation",
                "header", "mail", "destination", new IOException("failure\r\nextra"));
        }

        byte[] actual = File.ReadAllBytes(path);
        Assert.Equal(original, actual[..original.Length]);
        string suffix = Encoding.UTF8.GetString(actual[original.Length..]);
        string line = Assert.Single(suffix.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("basename=\"message\\r\\nforged\"", line);
        Assert.Contains("reason=\"reason\\r\\nnext\"", line);
        Assert.Contains("failure\\r\\nextra", line);
        Assert.DoesNotContain("not a file event", suffix);
        Assert.DoesNotContain('\uFEFF', suffix);
        Assert.Empty(tree.Errors.ToString());
    }

    // File failures cannot alter the same or later message outcome or disable the independent debug stream.
    [Theory]
    [InlineData("open")]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("close")]
    public void FileFailuresAreBestEffortAndDoNotDisableVerboseOutput(string stage)
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        FailingLogStream bytes = new(stage);
        int openCount = 0;
        SorterDiagnostics diagnostics = SorterDiagnostics.OpenForTesting(tree.Data("sorter.log"), true, output,
            tree.Errors, () => FixedTime, _ =>
            {
                openCount++;
                if (stage == "open")
                    throw new IOException("injected open failure");
                return bytes;
            });
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);
        tree.AddMessage("first");
        tree.AddMessage("later");

        Assert.Equal(MessageOutcome.Succeeded, processor.Process("first"));
        int writesAfterFirst = bytes.WriteAttempts;
        Assert.Equal(MessageOutcome.Succeeded, processor.Process("later"));
        if (stage is "write" or "flush")
            Assert.Equal(writesAfterFirst, bytes.WriteAttempts);
        diagnostics.Dispose();
        diagnostics.Dispose();

        Assert.Equal(1, openCount);
        Assert.True(File.Exists(tree.Spool("first.hdr")));
        Assert.True(File.Exists(tree.Spool("later.hdr")));
        Assert.Contains("first", output.ToString());
        Assert.Contains("later", output.ToString());
        Assert.Equal(1, tree.Errors.ToString().Split("ERROR logging to", StringSplitOptions.None).Length - 1);
        if (stage == "flush")
            Assert.Contains("result=PASS", Assert.Single(Lines(bytes)));
        if (stage == "close")
            Assert.Equal(2, Lines(bytes).Length);
    }

    // A file-formatting failure cannot suppress console formatting or retry the disabled file sink.
    [Fact]
    public void FileFormattingFailureLeavesConsoleMessageResultsAvailable()
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        using MemoryStream bytes = new();
        int clockCalls = 0;
        using SorterDiagnostics diagnostics = SorterDiagnostics.OpenForTesting(tree.Data("sorter.log"), true,
            output, tree.Errors, () =>
            {
                if (++clockCalls == 1)
                    throw new InvalidOperationException("injected first formatting failure");
                return FixedTime;
            }, _ => bytes);

        diagnostics.Message("first", "PASS", null, "NO_AUTH", "publish HDR",
            tree.Spool("first.hdr"), tree.Spool("first.eml"));
        diagnostics.Message("later", "PASS", null, "NO_AUTH", "publish HDR",
            tree.Spool("later.hdr"), tree.Spool("later.eml"));

        Assert.Equal(3, clockCalls);
        Assert.Empty(Lines(bytes));
        string[] consoleLines = output.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, consoleLines.Length);
        Assert.Contains("basename=\"first\"", consoleLines[0]);
        Assert.Contains("basename=\"later\"", consoleLines[1]);
        Assert.All(consoleLines, line => Assert.Contains("result=PASS", line));
        Assert.Equal(1, tree.Errors.ToString().Split("ERROR logging to", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("ERROR writing sorter debug output", tree.Errors.ToString());
    }

    // Formatting failures disable the file sink without escaping into a completed handoff or retrying it.
    [Fact]
    public void ClockFailureIsBestEffortAndDisablesTheFileSink()
    {
        using SorterTestDirectory tree = new();
        using MemoryStream bytes = new();
        int clockCalls = 0;
        using SorterDiagnostics diagnostics = SorterDiagnostics.OpenForTesting(tree.Data("sorter.log"), false,
            TextWriter.Null, tree.Errors, () =>
            {
                clockCalls++;
                throw new InvalidOperationException("injected clock failure");
            }, _ => bytes);
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);
        tree.AddMessage("first");
        tree.AddMessage("later");

        Assert.Equal(MessageOutcome.Succeeded, processor.Process("first"));
        Assert.Equal(MessageOutcome.Succeeded, processor.Process("later"));
        Assert.Equal(1, clockCalls);
        Assert.Empty(Lines(bytes));
        Assert.Contains("ERROR logging to", tree.Errors.ToString());
        Assert.True(File.Exists(tree.Spool("first.hdr")));
        Assert.True(File.Exists(tree.Spool("later.hdr")));
    }

    // A failing debug destination is disabled while both messages still get their independent file records.
    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    public void DebugOutputFailureDoesNotDisableFileResults(string stage)
    {
        using SorterTestDirectory tree = new();
        using FailingTextWriter output = new(stage);
        using MemoryStream bytes = new();
        using SorterDiagnostics diagnostics = Open(tree, bytes, output, verbose: true);
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);
        tree.AddMessage("first");
        tree.AddMessage("later");

        Assert.Equal(MessageOutcome.Succeeded, processor.Process("first"));
        int writesAfterFirst = output.WriteAttempts;
        Assert.Equal(MessageOutcome.Succeeded, processor.Process("later"));
        Assert.Equal(writesAfterFirst, output.WriteAttempts);
        Assert.Equal(2, Lines(bytes).Length);
        Assert.All(Lines(bytes), line => Assert.Contains("result=PASS", line));
        Assert.Equal(1, tree.Errors.ToString().Split("ERROR writing sorter debug output", StringSplitOptions.None).Length - 1);
    }

    // Even simultaneous unavailable outputs leave actual mail success and owned error retention unchanged.
    [Fact]
    public void AllOutputFailuresPreserveTheUnderlyingMessageOutcome()
    {
        using SorterTestDirectory tree = new();
        using FailingTextWriter output = new("write");
        using FailingTextWriter errors = new("write");
        using SorterDiagnostics diagnostics = SorterDiagnostics.OpenForTesting(tree.Data("sorter.log"), true,
            output, errors, () => FixedTime, _ => throw new IOException("log unavailable"));
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), errors, diagnostics);
        tree.AddMessage("good");
        tree.AddMessage("held", "auth:\r\n");
        File.WriteAllText(tree.Input("held.sort.err"), "previous evidence");

        Assert.Equal(MessageOutcome.Succeeded, processor.Process("good"));
        Assert.True(File.Exists(tree.Spool("good.hdr")));
        Assert.Equal(MessageOutcome.Failed, processor.Process("held"));
        Assert.True(File.Exists(tree.Input("held.hdr.sort")));
        Assert.True(File.Exists(tree.Input("held.eml")));
        Assert.Equal("previous evidence", File.ReadAllText(tree.Input("held.sort.err")));
    }

    // Bind one deterministic file sink while leaving the production formatter and processor intact.
    private static SorterDiagnostics Open(SorterTestDirectory tree, MemoryStream bytes,
        TextWriter? output = null, bool verbose = false) =>
        SorterDiagnostics.OpenForTesting(tree.Data("sorter.log"), verbose, output ?? TextWriter.Null,
            tree.Errors, () => FixedTime, _ => bytes);

    // Inspect attempted log bytes even after the production logger has closed its test stream.
    private static string[] Lines(MemoryStream bytes) =>
        Encoding.UTF8.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private sealed class FailingTextWriter : StringWriter
    {
        private readonly string stage;
        public int WriteAttempts { get; private set; }

        // Select either the write or flush boundary without changing production configuration.
        public FailingTextWriter(string stage) => this.stage = stage;

        // Count writes to prove the failed debug sink is not retried for later messages.
        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            if (stage == "write")
                throw new IOException("injected output write failure");
            base.WriteLine(value);
        }

        // Leave already-written debug bytes intact when flush fails.
        public override void Flush()
        {
            if (stage == "flush")
                throw new IOException("injected output flush failure");
            base.Flush();
        }
    }

    private sealed class FailingLogStream : MemoryStream
    {
        private readonly string stage;
        public int WriteAttempts { get; private set; }

        // Select one managed file-output boundary for an otherwise ordinary memory stream.
        public FailingLogStream(string stage) => this.stage = stage;

        // Exercise the span-based writer while retaining bytes for later flush or close failures.
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteAttempts++;
            if (stage == "write")
                throw new IOException("injected log write failure");
            base.Write(buffer);
        }

        // Cover byte-array dispatch without allowing a selected write failure to pass through.
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempts++;
            if (stage == "write")
                throw new IOException("injected log write failure");
            base.Write(buffer, offset, count);
        }

        // Preserve the written record while simulating a failed managed flush.
        public override void Flush()
        {
            if (stage == "flush")
                throw new IOException("injected log flush failure");
            base.Flush();
        }

        // Release stream resources before reporting a close failure to the best-effort logger.
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && stage == "close")
                throw new IOException("injected log close failure");
        }
    }
}
