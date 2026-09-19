using SmSorter;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class SorterConciseTests
{
    // Summarize each complete pair from the existing HDR snapshot without reading EML or sender records.
    [Theory]
    [InlineData("no-auth", "spool")]
    [InlineData("unenrolled", "spool")]
    [InlineData("enrolled", "process")]
    [InlineData("unenrollable", "spool")]
    public void CompletedRouteReportsOnePairAfterBothMoves(string arrangement, string destination)
    {
        using SorterTestDirectory tree = new();
        string auth = arrangement == "unenrollable" ? "account/part@example.com" : "account@example.com";
        byte[] hdr = tree.AddMessage("421", arrangement == "no-auth" ? "" : "auth: " + auth + "\r\n");
        byte[] eml = [0, 255, 13, 10, 42];
        File.WriteAllBytes(tree.Input("421.eml"), eml);
        if (arrangement == "enrolled")
            Directory.CreateDirectory(tree.Data("senders/auth-addresses/" + auth));
        else if (arrangement == "unenrolled")
            Directory.CreateDirectory(tree.Data("senders/auth-addresses"));
        string root = destination == "process" ? tree.Work("process") : tree.Spool("");
        using ObservingWriter output = new(() => File.Exists(Path.Combine(root, "421.hdr"))
            && File.Exists(Path.Combine(root, "421.eml")) && !File.Exists(tree.Input("421.hdr.sort"))
            && !File.Exists(tree.Input("421.eml")));
        SorterProcessor processor = Create(tree, output);

        using (FileStream unreadable = new(tree.Input("421.eml"), FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
        {
            Assert.Equal(MessageOutcome.Succeeded, processor.Process("421"));
        }

        string line = Assert.Single(Lines(output));
        Assert.Contains(": 421 " + (destination == "process" ? "TAKE" : "PASS") + " <sender@example.com> to <recipient@example.net>", line);
        Assert.EndsWith(arrangement == "no-auth" ? ", no auth" : ", auth <" + auth + ">", line);
        Assert.NotEmpty(output.ObservedStates);
        Assert.All(output.ObservedStates, Assert.True);
        Assert.Equal(hdr, File.ReadAllBytes(Path.Combine(root, "421.hdr")));
        Assert.Equal(eml, File.ReadAllBytes(Path.Combine(root, "421.eml")));
        Assert.False(Directory.Exists(tree.Data("senders/sender-ids")));
        Assert.Empty(tree.Errors.ToString());
    }

    // Final EML candidates that remain producer-owned stay silent until a complete routing transition.
    [Theory]
    [InlineData("Writing")]
    [InlineData("Quarantined")]
    public void RepeatedDeferralsEmitNothingUntilWritten(string status)
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        tree.AddMessage("waiting");
        File.WriteAllText(tree.Input("waiting.hdr"), status + "\r\nsender@example.com\r\nrecipient@example.net\r\n\r\n");
        SorterProcessor processor = Create(tree, output);

        Assert.Equal(MessageOutcome.Deferred, processor.Process("waiting", watchMode: true));
        Assert.Equal(MessageOutcome.Deferred, processor.Process("waiting", watchMode: true));
        Assert.Empty(output.ToString());
        Assert.True(File.Exists(tree.Input("waiting.hdr")));
        Assert.False(File.Exists(tree.Input("waiting.hdr.sort")));
        tree.AddMessage("waiting");
        Assert.Equal(MessageOutcome.Succeeded, processor.Process("waiting", watchMode: true));
        Assert.Contains(": waiting PASS <sender@example.com> to <recipient@example.net>, no auth", Assert.Single(Lines(output)));
        Assert.Equal(MessageOutcome.Stale, processor.Process("waiting", watchMode: true));
        Assert.Single(Lines(output));
    }

    // A fully retained upstream Failed pair gets one summary even when its remaining HDR is opaque.
    [Fact]
    public void CompletedFailedRetentionReportsAfterBothMovesAndPreservesError()
    {
        using SorterTestDirectory tree = new();
        byte[] hdr = "Failed \r\nopaque malformed remainder"u8.ToArray();
        byte[] eml = [0, 255, 42];
        File.WriteAllBytes(tree.Input("rejected.hdr"), hdr);
        File.WriteAllBytes(tree.Input("rejected.eml"), eml);
        using ObservingWriter output = new(() => File.Exists(tree.Work("failed/rejected.hdr.sort"))
            && File.Exists(tree.Work("failed/rejected.eml")) && !File.Exists(tree.Input("rejected.hdr.sort"))
            && !File.Exists(tree.Input("rejected.eml")));
        SorterProcessor processor = Create(tree, output);

        Assert.Equal(MessageOutcome.Failed, processor.Process("rejected", watchMode: true));

        string line = Assert.Single(Lines(output));
        Assert.Contains(": rejected FAIL ", line);
        Assert.DoesNotContain("moved to", line);
        Assert.NotEmpty(output.ObservedStates);
        Assert.All(output.ObservedStates, Assert.True);
        Assert.Equal(hdr, File.ReadAllBytes(tree.Work("failed/rejected.hdr.sort")));
        Assert.Equal(eml, File.ReadAllBytes(tree.Work("failed/rejected.eml")));
        Assert.Contains("SmarterMail marked the message Failed", tree.Errors.ToString());
        Assert.True(File.Exists(tree.Work("failed/rejected.sort.err")));
    }

    // A partial retention collision cannot be described as a moved pair.
    [Theory]
    [InlineData("eml")]
    [InlineData("hdr.sort")]
    public void FailedRetentionCollisionEmitsNoSummary(string extension)
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        tree.AddMessage("rejected");
        byte[] hdr = "Failed\r\nsender@example.com\r\nrecipient@example.net\r\n\r\n"u8.ToArray();
        File.WriteAllBytes(tree.Input("rejected.hdr"), hdr);
        Directory.CreateDirectory(tree.Work("failed"));
        string blocker = tree.Work("failed/rejected." + extension);
        File.WriteAllText(blocker, "existing evidence");
        SorterProcessor processor = Create(tree, output);

        Assert.Equal(MessageOutcome.Failed, processor.Process("rejected", watchMode: true));

        Assert.Empty(output.ToString());
        Assert.Equal("existing evidence", File.ReadAllText(blocker));
        Assert.Equal(hdr, File.ReadAllBytes(tree.Input("rejected.hdr.sort")));
        Assert.True(File.Exists(extension == "eml" ? tree.Input("rejected.eml") : tree.Work("failed/rejected.eml")));
        Assert.NotEmpty(tree.Errors.ToString());
    }

    // Neither routing branch reports a complete pair when final HDR publication collides.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialRoutingEmitsNoSummary(bool enrolled)
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        byte[] hdr = tree.AddMessage("partial", enrolled ? "auth: account@example.com\r\n" : "");
        string destination = enrolled ? tree.Work("process") : tree.Spool("");
        Directory.CreateDirectory(destination);
        if (enrolled)
            Directory.CreateDirectory(tree.Data("senders/auth-addresses/account@example.com"));
        File.WriteAllText(Path.Combine(destination, "partial.hdr"), "existing trigger");
        SorterProcessor processor = Create(tree, output);

        Assert.Equal(MessageOutcome.Failed, processor.Process("partial"));

        Assert.Empty(output.ToString());
        Assert.Equal(hdr, File.ReadAllBytes(tree.Input("partial.hdr.sort")));
        Assert.True(File.Exists(Path.Combine(destination, "partial.eml")));
        Assert.False(File.Exists(tree.Input("partial.eml")));
        Assert.Equal("existing trigger", File.ReadAllText(Path.Combine(destination, "partial.hdr")));
        Assert.NotEmpty(tree.Errors.ToString());
    }

    // Readiness, stale discovery, unsafe headers, and failed ownership do not constitute completed pairs.
    [Fact]
    public void NoSummaryForUnmovedOrUnclaimedInputs()
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        SorterProcessor processor = Create(tree, output);
        tree.AddMessage("hdr-only");
        File.Delete(tree.Input("hdr-only.eml"));
        tree.AddMessage("unsafe", "auth:\r\n");
        tree.AddMessage("collision");
        File.WriteAllText(tree.Input("collision.hdr.sort"), "existing residual");

        Assert.Equal(MessageOutcome.Deferred, processor.Process("hdr-only"));
        Assert.Equal(MessageOutcome.Stale, processor.Process("gone", watchMode: true));
        Assert.Equal(MessageOutcome.Failed, processor.Process("unsafe"));
        Assert.Throws<FatalProcessingException>(() => processor.Process("collision"));
        processor.ReportResiduals();

        Assert.Empty(output.ToString());
        Assert.True(File.Exists(tree.Input("hdr-only.hdr")));
        Assert.True(File.Exists(tree.Input("unsafe.hdr.sort")));
        Assert.True(File.Exists(tree.Input("collision.hdr")));
    }

    // Concise display must not add envelope validation to the no-auth routing contract.
    [Fact]
    public void RawInvalidEnvelopeStillPassesByteExactly()
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        tree.AddMessage("opaque");
        byte[] hdr = "Written\r\nnot an address\r\nunparsed recipient value\r\n\r\n"u8.ToArray();
        File.WriteAllBytes(tree.Input("opaque.hdr"), hdr);
        SorterProcessor processor = Create(tree, output);

        Assert.Equal(MessageOutcome.Succeeded, processor.Process("opaque"));

        Assert.Equal(hdr, File.ReadAllBytes(tree.Spool("opaque.hdr")));
        Assert.Contains("not an address", Assert.Single(Lines(output)));
        Assert.Contains("unparsed recipient value", Assert.Single(Lines(output)));
        Assert.Empty(tree.Errors.ToString());
    }

    // A failing concise writer cannot hold the completed pair or disable the independent terminal file log.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrokenConciseOutputPreservesProcessingAndFileLogging(bool failFlush)
    {
        using SorterTestDirectory tree = new();
        using FailingWriter output = new(failFlush);
        string log = tree.Data("sorter.log");
        using (SorterDiagnostics diagnostics = SorterDiagnostics.Open(log, false, stderr: tree.Errors))
        {
            SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics,
                new ConciseOutput(true, output, tree.Errors));
            tree.AddMessage("current");
            tree.AddMessage("next");
            Assert.Equal(MessageOutcome.Succeeded, processor.Process("current"));
            Assert.Equal(MessageOutcome.Succeeded, processor.Process("next"));
        }

        Assert.True(File.Exists(tree.Spool("current.hdr")));
        Assert.True(File.Exists(tree.Spool("next.hdr")));
        Assert.Equal(2, File.ReadAllLines(log).Length);
        Assert.All(File.ReadAllLines(log), line => Assert.Contains("result=PASS", line));
        Assert.NotEmpty(tree.Errors.ToString());
    }

    // Concise and verbose channels are additive while the file retains one ordinary terminal record.
    [Fact]
    public void ConciseAndVerboseBothEmitWithUnchangedFileRecord()
    {
        using SorterTestDirectory tree = new();
        using StringWriter output = new();
        string log = tree.Data("sorter.log");
        using (SorterDiagnostics diagnostics = SorterDiagnostics.Open(log, true, output, tree.Errors))
        {
            tree.AddMessage("both");
            SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics,
                new ConciseOutput(true, output, tree.Errors));
            Assert.Equal(MessageOutcome.Succeeded, processor.Process("both"));
        }

        string[] lines = Lines(output);
        Assert.Contains(lines, line => line.StartsWith("DEBUG ", StringComparison.Ordinal));
        Assert.Contains(": both PASS <sender@example.com> to <recipient@example.net>, no auth",
            Assert.Single(lines, line => !line.StartsWith("DEBUG ", StringComparison.Ordinal)));
        string record = Assert.Single(File.ReadAllLines(log));
        Assert.Contains("result=PASS", record);
        Assert.DoesNotContain("moved to", record);
        Assert.DoesNotContain(" from <", record);
        Assert.Empty(tree.Errors.ToString());
    }

    // Bind only the shared optional console formatter to an otherwise ordinary sorter fixture.
    private static SorterProcessor Create(SorterTestDirectory tree, TextWriter output) =>
        new(tree.Data(""), tree.Spool(""), tree.Errors, concise: new ConciseOutput(true, output, tree.Errors));

    // Split physical console lines without permitting hidden extra records in one assertion.
    private static string[] Lines(StringWriter output) =>
        output.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    private sealed class ObservingWriter : StringWriter
    {
        private readonly Func<bool> inspect;
        public List<bool> ObservedStates { get; } = [];

        // Capture the expected filesystem postcondition for every attempted summary write.
        public ObservingWriter(Func<bool> inspect) => this.inspect = inspect;

        // Observe publication before allowing the summary text into the console buffer.
        public override void WriteLine(string? value)
        {
            ObservedStates.Add(inspect());
            base.WriteLine(value);
        }
    }

    private sealed class FailingWriter : StringWriter
    {
        private readonly bool failFlush;

        // Select a console write or flush failure without adding production fault controls.
        public FailingWriter(bool failFlush) => this.failFlush = failFlush;

        // Simulate a broken output pipe at its write boundary.
        public override void WriteLine(string? value)
        {
            if (!failFlush) throw new IOException("Concise console write failed.");
            base.WriteLine(value);
        }

        // Simulate a console flush failure after the summary bytes were accepted.
        public override void Flush()
        {
            if (failFlush) throw new IOException("Concise console flush failed.");
            base.Flush();
        }
    }
}
