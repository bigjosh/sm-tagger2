using System.Text;
using SmSorter;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class SorterWrittenGateTests
{
    // Writing remains producer-owned in both modes, without inspecting incomplete routing metadata.
    [Theory]
    [InlineData("Writing", true)]
    [InlineData("Writing \t", true)]
    [InlineData("Writing", false)]
    [InlineData("Writing \t", false)]
    public void WritingDefersUntouchedBeforeRouting(string status, bool watchMode)
    {
        using GateFixture fixture = new();
        byte[] hdr = Encoding.ASCII.GetBytes(status + "\r\nunfinished\ninvalid metadata");
        fixture.Write("waiting", hdr);
        Directory.CreateDirectory(fixture.Tree.Data("senders"));
        File.WriteAllText(fixture.Tree.Data("senders/auth-addresses"), "Routing must not be inspected yet.");

        Assert.Equal(MessageOutcome.Deferred, fixture.Processor.Process("waiting", watchMode));

        fixture.AssertUntouched("waiting", hdr);
        Assert.Contains("event=DEFER", fixture.Output.ToString());
        Assert.Contains("reason=\"HDR_WRITING\"", fixture.Output.ToString());
        Assert.DoesNotContain("event=AUTH_LOOKUP", fixture.Output.ToString());
        if (watchMode)
            Assert.Empty(fixture.Tree.Errors.ToString());
        else
        {
            Assert.Contains("NOT READY", fixture.Tree.Errors.ToString());
            Assert.Contains("HDR_WRITING", fixture.Tree.Errors.ToString());
        }
    }

    // Every unexpected status attempt is reported with escaped original bytes while leaving all producer files untouched.
    [Theory]
    [InlineData("Quarantined\r\n", "Quarantined")]
    [InlineData("Ready\r\n", "Ready")]
    [InlineData("Unknown\r\n", "Unknown")]
    [InlineData("written\r\n", "written")]
    [InlineData("WRITTEN\r\n", "WRITTEN")]
    [InlineData("failed\r\n", "failed")]
    [InlineData("writing\r\n", "writing")]
    [InlineData(" Written\r\n", " Written")]
    [InlineData("Written\v\r\n", "Written\v")]
    [InlineData("Written\u00a0\r\n", "Written\u00a0")]
    [InlineData("\r\n", "")]
    [InlineData("", "<missing CRLF>")]
    [InlineData("Written", "<missing CRLF>")]
    [InlineData("Written\n", "<missing CRLF>")]
    [InlineData("\0Quarantine\t\"\\\nforged\r\n", "\0Quarantine\t\"\\\nforged")]
    public void UnexpectedStatusesRemainPlainAndReportEveryWatchAttempt(string headerPrefix, string observedStatus)
    {
        using GateFixture fixture = new();
        byte[] hdr = Encoding.Latin1.GetBytes(headerPrefix + (headerPrefix.Contains("\r\n", StringComparison.Ordinal)
            ? "sender@example.com\r\nrecipient@example.net\r\nauth: missing@example.com\r\n\r\n" : ""));
        fixture.Write("unexpected", hdr);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(MessageOutcome.Deferred, fixture.Processor.Process("unexpected", watchMode: true));
            fixture.AssertUntouched("unexpected", hdr);
        }

        string[] errors = fixture.Tree.Errors.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, errors.Length);
        Assert.All(errors, line =>
        {
            Assert.Contains("HDR_STATUS_UNEXPECTED", line);
            Assert.Contains(ConsoleErrors.Quote(observedStatus), line);
        });
        Assert.Contains("reason=\"HDR_STATUS_UNEXPECTED\"", fixture.Output.ToString());
        Assert.DoesNotContain("event=AUTH_LOOKUP", fixture.Output.ToString());
        Assert.DoesNotContain("event=MOVE_INTENT", fixture.Output.ToString());
    }

    // One-shot unexpected statuses explain the deferral rather than recording an owned message failure.
    [Theory]
    [InlineData("Quarantined\r\n", "Quarantined")]
    [InlineData("", "<missing CRLF>")]
    public void UnexpectedOneShotIsNotReadyWithObservedStatus(string text, string observedStatus)
    {
        using GateFixture fixture = new();
        byte[] hdr = Encoding.ASCII.GetBytes(text);
        fixture.Write("unexpected", hdr);

        Assert.Equal(MessageOutcome.Deferred, fixture.Processor.Process("unexpected"));

        fixture.AssertUntouched("unexpected", hdr);
        string error = Assert.Single(fixture.Tree.Errors.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("NOT READY", error);
        Assert.Contains("HDR_STATUS_UNEXPECTED", error);
        Assert.Contains(ConsoleErrors.Quote(observedStatus), error);
    }

    // A later Written HDR must supply fresh auth/routing bytes rather than reusing the provisional producer state.
    [Fact]
    public void WritingToWrittenUsesNewAuthenticationAndEnvelope()
    {
        using GateFixture fixture = new();
        Directory.CreateDirectory(fixture.Tree.Data("senders/auth-addresses/early@example.com"));
        byte[] writing = "Writing \r\nearly@example.com\r\nearly@example.net\r\nauth: early@example.com\r\n\r\n"u8.ToArray();
        fixture.Write("transition", writing);
        Assert.Equal(MessageOutcome.Deferred, fixture.Processor.Process("transition", watchMode: true));
        fixture.AssertUntouched("transition", writing);
        byte[] written = "Written\t\r\nfinal@example.com\r\nfinal@example.net\r\nauth: FINAL@EXAMPLE.COM\r\n\r\n"u8.ToArray();
        File.WriteAllBytes(fixture.Tree.Input("transition.hdr"), written);

        Assert.Equal(MessageOutcome.Succeeded, fixture.Processor.Process("transition", watchMode: true));

        Assert.Equal(written, File.ReadAllBytes(fixture.Tree.Spool("transition.hdr")));
        Assert.Equal(GateFixture.Eml, File.ReadAllBytes(fixture.Tree.Spool("transition.eml")));
        string record = Assert.Single(fixture.Records());
        Assert.Contains("result=PASS", record);
        Assert.Contains("auth=\"final@example.com\"", record);
        Assert.DoesNotContain("early@example.com", record);
        Assert.False(Directory.Exists(fixture.Tree.Work("process")));
        Assert.Empty(fixture.Tree.Errors.ToString());
    }

    // Discovery continues beyond an unknown-status candidate and processes a later ready message in the same scan.
    [Fact]
    public void UnexpectedStatusDoesNotBlockLaterWatcherCandidate()
    {
        using GateFixture fixture = new();
        byte[] hdr = "Quarantined\r\nopaque unfinished routing"u8.ToArray();
        fixture.Write("a-waiting", hdr);
        byte[] ready = fixture.Tree.AddMessage("b-ready");
        List<string> observed = [];
        QueueWatcher? active = null;
        using QueueWatcher watcher = new(fixture.Tree.Input(""), basename =>
        {
            observed.Add(basename);
            MessageOutcome outcome = fixture.Processor.Process(basename, watchMode: true);
            if (basename == "b-ready")
                active!.RequestStop();
            return outcome;
        }, inputExtension: ".eml");
        active = watcher;

        watcher.Run();

        Assert.Equal(["a-waiting", "b-ready"], observed);
        Assert.Equal(hdr, File.ReadAllBytes(fixture.Tree.Input("a-waiting.hdr")));
        Assert.Equal(GateFixture.Eml, File.ReadAllBytes(fixture.Tree.Input("a-waiting.eml")));
        Assert.False(File.Exists(fixture.Tree.Input("a-waiting.hdr.sort")));
        Assert.False(File.Exists(fixture.Tree.Input("a-waiting.sort.err")));
        Assert.Equal(ready, File.ReadAllBytes(fixture.Tree.Spool("b-ready.hdr")));
        Assert.Contains("basename=\"b-ready\" result=PASS", Assert.Single(fixture.Records()));
        Assert.Contains("HDR_STATUS_UNEXPECTED", fixture.Tree.Errors.ToString());
    }

    // Unknown status may later become an upstream failure retained in the private Proc subtree.
    [Fact]
    public void UnexpectedToFailedRetainsOnlyAfterFinalStatus()
    {
        using GateFixture fixture = new();
        byte[] pending = "Quarantined\r\nunfinished"u8.ToArray();
        fixture.Write("transition", pending);
        Assert.Equal(MessageOutcome.Deferred, fixture.Processor.Process("transition", watchMode: true));
        fixture.AssertUntouched("transition", pending);
        byte[] failed = "Failed \t\r\nunparseable\nremainder"u8.ToArray();
        File.WriteAllBytes(fixture.Tree.Input("transition.hdr"), failed);

        Assert.Equal(MessageOutcome.Failed, fixture.Processor.Process("transition", watchMode: true));

        Assert.Equal(failed, File.ReadAllBytes(fixture.Tree.Work("failed/transition.hdr.sort")));
        Assert.Equal(GateFixture.Eml, File.ReadAllBytes(fixture.Tree.Work("failed/transition.eml")));
        Assert.Empty(Directory.GetFiles(fixture.Tree.Input("")));
        Assert.Contains("reason=\"UPSTREAM_FAILED\"", Assert.Single(fixture.Records()));
    }

    // Broken console diagnostics cannot promote a deferred candidate to ownership or a terminal mail result.
    [Theory]
    [InlineData("Writing\r\n", false)]
    [InlineData("Quarantined\r\n", true)]
    [InlineData("", false)]
    public void DiagnosticFailureCannotClaimUnreadyInput(string text, bool watchMode)
    {
        using GateFixture fixture = new();
        byte[] hdr = Encoding.ASCII.GetBytes(text);
        fixture.Write("unready", hdr);
        using ThrowingWriter errors = new();
        using SorterDiagnostics diagnostics = SorterDiagnostics.Open(null, true, errors, errors);
        SorterProcessor processor = new(fixture.Tree.Data(""), fixture.Tree.Spool(""), errors, diagnostics);

        Assert.Equal(MessageOutcome.Deferred, processor.Process("unready", watchMode));

        fixture.AssertUntouched("unready", hdr);
        Assert.True(errors.Attempts > 0);
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public int Attempts { get; private set; }

        // Fail the actual console output boundary without throwing assertions inside best-effort production code.
        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("Synthetic diagnostic output failure.");
        }
    }

    private sealed class GateFixture : IDisposable
    {
        public static readonly byte[] Eml = [0, 255, 42, 13, 10];
        private readonly MemoryStream records = new();
        private readonly SorterDiagnostics diagnostics;
        public SorterTestDirectory Tree { get; } = new();
        public StringWriter Output { get; } = new();
        public SorterProcessor Processor { get; }

        // Bind the actual sorter to independent stdout, stderr, and terminal-log observers.
        public GateFixture()
        {
            diagnostics = SorterDiagnostics.OpenForTesting(Tree.Data("sorter.log"), true, Output, Tree.Errors,
                () => new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero), _ => records);
            Processor = new SorterProcessor(Tree.Data(""), Tree.Spool(""), Tree.Errors, diagnostics);
        }

        // Install one synthesized pair while retaining exact original bytes for the untouched-input checks.
        public void Write(string basename, byte[] hdr)
        {
            File.WriteAllBytes(Tree.Input(basename + ".hdr"), hdr);
            File.WriteAllBytes(Tree.Input(basename + ".eml"), Eml);
        }

        // Decode only terminal email-log records, which readiness deferrals must never emit.
        public string[] Records() => Encoding.UTF8.GetString(records.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Check the complete externally visible pre-ownership boundary after any readiness rejection.
        public void AssertUntouched(string basename, byte[] hdr)
        {
            Assert.Equal(hdr, File.ReadAllBytes(Tree.Input(basename + ".hdr")));
            Assert.Equal(Eml, File.ReadAllBytes(Tree.Input(basename + ".eml")));
            Assert.False(File.Exists(Tree.Input(basename + ".hdr.sort")));
            Assert.False(File.Exists(Tree.Input(basename + ".sort.err")));
            Assert.False(File.Exists(Tree.Spool(basename + ".hdr")));
            Assert.False(File.Exists(Tree.Spool(basename + ".eml")));
            Assert.False(Directory.Exists(Tree.Work("process")));
            Assert.False(Directory.Exists(Tree.Work("failed")));
            Assert.Empty(Records());
        }

        // Close diagnostic streams before deleting only the fixture's validated temporary tree.
        public void Dispose()
        {
            diagnostics.Dispose();
            records.Dispose();
            Output.Dispose();
            Tree.Dispose();
        }
    }
}
