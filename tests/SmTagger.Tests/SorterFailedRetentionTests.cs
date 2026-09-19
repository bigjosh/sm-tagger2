using System.Text;
using SmSorter;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class SorterFailedRetentionTests
{
    // Preserve Failed bytes in the private Proc subtree without parsing the remaining HDR metadata or reading EML.
    [Theory]
    [InlineData("Failed", false)]
    [InlineData("Failed \t", true)]
    public void UpstreamFailureMovesOpaquePairWithoutReadingEml(string status, bool existingDirectory)
    {
        using FailureFixture fixture = new();
        var original = fixture.AddFailed("rejected", status);
        if (existingDirectory)
            Directory.CreateDirectory(fixture.Held(""));
        Assert.False(Directory.Exists(fixture.Tree.Data("senders/auth-addresses")));
        using (FileStream unreadable = new(fixture.Tree.Input("rejected.eml"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.Delete))
        {
            Assert.Equal(MessageOutcome.Failed, fixture.Processor.Process("rejected", watchMode: true));
        }

        Assert.Equal(original.Hdr, File.ReadAllBytes(fixture.Held("rejected.hdr.sort")));
        Assert.Equal(original.Eml, File.ReadAllBytes(fixture.Held("rejected.eml")));
        Assert.Empty(Directory.GetFiles(fixture.Tree.Input("")));
        Assert.Empty(Directory.GetFiles(fixture.Tree.Spool("")));
        Assert.False(Directory.Exists(fixture.Tree.Work("process")));
        Assert.False(Directory.Exists(fixture.Tree.Data("senders/auth-addresses")));
        fixture.AssertError("rejected", fixture.Held("rejected.hdr.sort"), fixture.Held("rejected.eml"),
            fixture.Held("rejected.sort.err"));
    }

    // A blocked holding directory or destination collision leaves the exact completed move boundary and later mail works.
    [Theory]
    [InlineData("directory-file")]
    [InlineData("eml-collision")]
    [InlineData("hdr-collision")]
    public void RetentionIoFailuresPreserveActualPartialStateAndContinue(string failure)
    {
        using FailureFixture fixture = new();
        var original = fixture.AddFailed("rejected");
        const string existing = "Do not overwrite earlier retained evidence.";
        string blocker;
        if (failure == "directory-file")
        {
            Directory.CreateDirectory(fixture.Tree.Work(""));
            blocker = fixture.Held("");
            File.WriteAllText(blocker, existing);
        }
        else
        {
            Directory.CreateDirectory(fixture.Held(""));
            blocker = fixture.Held("rejected." + (failure == "eml-collision" ? "eml" : "hdr.sort"));
            File.WriteAllText(blocker, existing);
        }

        Assert.Equal(MessageOutcome.Failed, fixture.Processor.Process("rejected", watchMode: true));

        string currentEml = failure == "hdr-collision" ? fixture.Held("rejected.eml") : fixture.Tree.Input("rejected.eml");
        Assert.Equal(original.Hdr, File.ReadAllBytes(fixture.Tree.Input("rejected.hdr.sort")));
        Assert.Equal(original.Eml, File.ReadAllBytes(currentEml));
        Assert.Equal(existing, File.ReadAllText(blocker));
        Assert.False(File.Exists(fixture.Tree.Input("rejected.hdr")));
        if (failure == "hdr-collision")
            Assert.False(File.Exists(fixture.Tree.Input("rejected.eml")));
        Assert.Empty(Directory.GetFiles(fixture.Tree.Spool("")));
        fixture.AssertError("rejected", fixture.Tree.Input("rejected.hdr.sort"), currentEml,
            fixture.Tree.Input("rejected.sort.err"));
        Assert.Contains("IOException", Assert.Single(fixture.Records()));
        string operation = failure switch
        {
            "directory-file" => "create failed retention directory",
            "eml-collision" => "retain failed EML",
            _ => "retain failed HDR"
        };
        Assert.Contains("operation=" + ConsoleErrors.Quote(operation), Assert.Single(fixture.Records()));

        string stderr = fixture.Tree.Errors.ToString();
        const string errorField = "\r\nerror=";
        int errorStart = stderr.IndexOf(errorField, StringComparison.Ordinal);
        Assert.True(errorStart >= 0);
        string exceptionText = stderr[(errorStart + errorField.Length)..].TrimEnd('\r', '\n');
        Assert.Contains(Environment.NewLine + "   at ", exceptionText);
        string[] diagnosticLines = File.ReadAllLines(fixture.Tree.Input("rejected.sort.err"));
        Assert.Equal(8, diagnosticLines.Length);
        Assert.Equal("error=" + ConsoleErrors.Quote(exceptionText), diagnosticLines[^1]);

        fixture.Tree.AddMessage("later");
        Assert.Equal(MessageOutcome.Succeeded, fixture.Processor.Process("later", watchMode: true));
        Assert.Equal(2, fixture.Records().Length);
        Assert.Contains("result=PASS", fixture.Records()[1]);
    }

    // A real Windows delete-sharing denial stops at the EML move without losing the claimed HDR or original bytes.
    [Fact]
    public void LockedEmlStopsRetentionBeforeHdrMove()
    {
        using FailureFixture fixture = new();
        var original = fixture.AddFailed("busy");
        using (FileStream locked = new(fixture.Tree.Input("busy.eml"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Equal(MessageOutcome.Failed, fixture.Processor.Process("busy", watchMode: true));
        }

        Assert.Equal(original.Hdr, File.ReadAllBytes(fixture.Tree.Input("busy.hdr.sort")));
        Assert.Equal(original.Eml, File.ReadAllBytes(fixture.Tree.Input("busy.eml")));
        Assert.Empty(Directory.GetFiles(fixture.Held("")));
        fixture.AssertError("busy", fixture.Tree.Input("busy.hdr.sort"), fixture.Tree.Input("busy.eml"),
            fixture.Tree.Input("busy.sort.err"));
        Assert.Contains("IOException", Assert.Single(fixture.Records()));
        Assert.Contains("operation=\"retain failed EML\"", Assert.Single(fixture.Records()));
    }

    // Existing diagnostic bytes or a diagnostic directory cannot prevent relocation or be overwritten.
    [Theory]
    [InlineData("held-file")]
    [InlineData("held-directory")]
    [InlineData("old-proc-file")]
    public void DiagnosticCollisionDoesNotBlockRetention(string arrangement)
    {
        using FailureFixture fixture = new();
        var original = fixture.AddFailed("rejected");
        Directory.CreateDirectory(fixture.Held(""));
        string oldDiagnostic = arrangement == "old-proc-file" ? fixture.Tree.Input("rejected.sort.err") : fixture.Held("rejected.sort.err");
        if (arrangement == "held-directory")
            Directory.CreateDirectory(oldDiagnostic);
        else
            File.WriteAllText(oldDiagnostic, "Earlier diagnostic remains exact.");

        Assert.Equal(MessageOutcome.Failed, fixture.Processor.Process("rejected"));

        Assert.Equal(original.Hdr, File.ReadAllBytes(fixture.Held("rejected.hdr.sort")));
        Assert.Equal(original.Eml, File.ReadAllBytes(fixture.Held("rejected.eml")));
        Assert.False(File.Exists(fixture.Tree.Input("rejected.hdr.sort")));
        Assert.False(File.Exists(fixture.Tree.Input("rejected.eml")));
        Assert.Contains("reason=\"UPSTREAM_FAILED\"", Assert.Single(fixture.Records()));
        if (arrangement == "held-directory")
            Assert.Empty(Directory.GetFileSystemEntries(oldDiagnostic));
        else
            Assert.Equal("Earlier diagnostic remains exact.", File.ReadAllText(oldDiagnostic));
        if (arrangement == "old-proc-file")
            Assert.True(File.Exists(fixture.Held("rejected.sort.err")));
        else
            Assert.Contains("Cannot write sorter diagnostic", fixture.Tree.Errors.ToString());
    }

    // A real logfile-open error is diagnostic only and cannot interrupt failed-mail preservation or later routing.
    [Fact]
    public void LogFailureDoesNotBlockRetentionOrNextMessage()
    {
        using SorterTestDirectory tree = new();
        Directory.CreateDirectory(tree.Data("blocked-log"));
        using SorterDiagnostics diagnostics = SorterDiagnostics.Open(tree.Data("blocked-log"), false, stderr: tree.Errors);
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), tree.Errors, diagnostics);
        byte[] hdr = "Failed \r\nopaque malformed remainder"u8.ToArray();
        File.WriteAllBytes(tree.Input("rejected.hdr"), hdr);
        File.WriteAllBytes(tree.Input("rejected.eml"), [0, 255, 42]);

        Assert.Equal(MessageOutcome.Failed, processor.Process("rejected", watchMode: true));
        tree.AddMessage("later");
        Assert.Equal(MessageOutcome.Succeeded, processor.Process("later", watchMode: true));

        Assert.Equal(hdr, File.ReadAllBytes(tree.Work("failed/rejected.hdr.sort")));
        Assert.Equal([0, 255, 42], File.ReadAllBytes(tree.Work("failed/rejected.eml")));
        Assert.True(File.Exists(tree.Work("failed/rejected.sort.err")));
        Assert.True(File.Exists(tree.Spool("later.hdr")));
        Assert.Contains("ERROR logging to", tree.Errors.ToString());
        Assert.Empty(Directory.GetFiles(tree.Input("")));
    }

    // Failed-message stderr is emitted only after completed retention and describes the retained paths.
    [Fact]
    public void FailureReportingFollowsBothRetentionMoves()
    {
        using SorterTestDirectory tree = new();
        File.WriteAllBytes(tree.Input("rejected.hdr"), "Failed\r\nmalformed metadata"u8.ToArray());
        File.WriteAllBytes(tree.Input("rejected.eml"), [0, 255]);
        using StateObservingWriter errors = new(() => File.Exists(tree.Work("failed/rejected.hdr.sort"))
            && File.Exists(tree.Work("failed/rejected.eml"))
            && !File.Exists(tree.Input("rejected.hdr.sort")) && !File.Exists(tree.Input("rejected.eml")));
        SorterProcessor processor = new(tree.Data(""), tree.Spool(""), errors);

        Assert.Equal(MessageOutcome.Failed, processor.Process("rejected"));

        Assert.NotEmpty(errors.ObservedStates);
        Assert.All(errors.ObservedStates, complete => Assert.True(complete));
        Assert.Contains(ConsoleErrors.Quote(Path.Combine(tree.Work("failed"), "rejected.hdr.sort")), errors.ToString());
        Assert.Contains(ConsoleErrors.Quote(Path.Combine(tree.Work("failed"), "rejected.eml")), errors.ToString());
    }

    // Watch startup and residual reporting never inspect or replay locked files retained outside the input queue.
    [Fact]
    public void RetainedPairIsIgnoredOnWatcherRestart()
    {
        using FailureFixture fixture = new();
        var original = fixture.AddFailed("old-failed");
        Assert.Equal(MessageOutcome.Failed, fixture.Processor.Process("old-failed"));
        fixture.Tree.Errors.GetStringBuilder().Clear();
        fixture.ClearRecords();
        fixture.Tree.AddMessage("fresh");
        List<string> discovered = [];
        QueueWatcher? active = null;
        using (FileStream lockedHdr = new(fixture.Held("old-failed.hdr.sort"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (FileStream lockedEml = new(fixture.Held("old-failed.eml"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (FileStream lockedDiagnostic = new(fixture.Held("old-failed.sort.err"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            SorterProcessor restarted = new(fixture.Tree.Data(""), fixture.Tree.Spool(""), fixture.Tree.Errors, fixture.Diagnostics);
            restarted.ReportResiduals();
            using QueueWatcher watcher = new(fixture.Tree.Input(""), basename =>
            {
                discovered.Add(basename);
                MessageOutcome result = restarted.Process(basename, watchMode: true);
                active!.RequestStop();
                return result;
            }, inputExtension: ".eml");
            active = watcher;
            watcher.Run();
        }

        Assert.Equal(["fresh"], discovered);
        Assert.Contains("result=PASS", Assert.Single(fixture.Records()));
        Assert.Empty(fixture.Tree.Errors.ToString());
        Assert.Equal(original.Hdr, File.ReadAllBytes(fixture.Held("old-failed.hdr.sort")));
        Assert.Equal(original.Eml, File.ReadAllBytes(fixture.Held("old-failed.eml")));
        Assert.Equal(MessageOutcome.Stale, fixture.Processor.Process("old-failed", watchMode: true));
        Assert.Single(fixture.Records());
    }

    // Unrelated malformed/auth holds keep their established Proc state and never create the failed directory.
    [Theory]
    [InlineData("unsafe")]
    [InlineData("missing-auth-root")]
    public void OtherHoldsDoNotUseFailedDirectory(string arrangement)
    {
        using FailureFixture fixture = new();
        byte[] hdr = fixture.Tree.AddMessage("held", arrangement == "unsafe" ? "auth:\r\n" : "auth: account@example.com\r\n");

        Assert.Equal(MessageOutcome.Failed, fixture.Processor.Process("held"));

        Assert.Equal(hdr, File.ReadAllBytes(fixture.Tree.Input("held.hdr.sort")));
        Assert.True(File.Exists(fixture.Tree.Input("held.eml")));
        Assert.True(File.Exists(fixture.Tree.Input("held.sort.err")));
        Assert.False(Directory.Exists(fixture.Held("")));
        Assert.DoesNotContain("UPSTREAM_FAILED", Assert.Single(fixture.Records()));
    }

    // An unusable holding pathname cannot become a startup dependency for ordinary mail.
    [Fact]
    public void OrdinaryPassDoesNotInspectOrRequireFailedDirectory()
    {
        using FailureFixture fixture = new();
        Directory.CreateDirectory(fixture.Tree.Work(""));
        File.WriteAllText(fixture.Held(""), "Not a directory, but irrelevant to passing mail.");
        fixture.Tree.AddMessage("pass");
        fixture.Processor.ReportResiduals();

        Assert.Equal(MessageOutcome.Succeeded, fixture.Processor.Process("pass"));

        Assert.Equal("Not a directory, but irrelevant to passing mail.", File.ReadAllText(fixture.Held("")));
        Assert.Contains("result=PASS", Assert.Single(fixture.Records()));
        Assert.Empty(fixture.Tree.Errors.ToString());
    }

    private sealed class StateObservingWriter : StringWriter
    {
        private readonly Func<bool> observe;
        public List<bool> ObservedStates { get; } = [];

        // Bind an observation callback without adding filesystem work to normal processor paths.
        public StateObservingWriter(Func<bool> observe)
        {
            this.observe = observe;
        }

        // Capture filesystem completion at each real error emission without throwing inside best-effort logging.
        public override void WriteLine(string? value)
        {
            ObservedStates.Add(observe());
            base.WriteLine(value);
        }
    }

    private sealed class FailureFixture : IDisposable
    {
        private readonly MemoryStream records = new();
        public SorterTestDirectory Tree { get; } = new();
        public SorterDiagnostics Diagnostics { get; }
        public SorterProcessor Processor { get; }

        // Bind actual filesystem processing to a separately inspectable terminal diagnostic log.
        public FailureFixture()
        {
            Diagnostics = SorterDiagnostics.OpenForTesting(Tree.Data("sorter.log"), false, TextWriter.Null, Tree.Errors,
                () => new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero), _ => records);
            Processor = new SorterProcessor(Tree.Data(""), Tree.Spool(""), Tree.Errors, Diagnostics);
        }

        // Resolve only the test's retained-failure location inside its private Proc subtree.
        public string Held(string name) => Tree.Work(Path.Combine("failed", name));

        // Create deliberately opaque upstream-rejected bytes with no dependency on later HDR or EML parsing.
        public (byte[] Hdr, byte[] Eml) AddFailed(string basename, string status = "Failed ")
        {
            byte[] hdr = [.. Encoding.ASCII.GetBytes(status + "\r\ninvalid routing\n"), 255, 0];
            byte[] eml = [0, 255, 13, 10, 42, 0xef, 0xbb, 0xbf];
            File.WriteAllBytes(Tree.Input(basename + ".hdr"), hdr);
            File.WriteAllBytes(Tree.Input(basename + ".eml"), eml);
            return (hdr, eml);
        }

        // Read terminal messages independently of console debug events.
        public string[] Records() => Encoding.UTF8.GetString(records.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Reset only the in-memory test sink before observing a fresh watcher startup.
        public void ClearRecords() => records.SetLength(0);

        // Require one upstream-failure record and diagnostic naming the true retained filesystem boundary.
        public void AssertError(string basename, string hdr, string eml, string diagnostic)
        {
            string record = Assert.Single(Records());
            Assert.Contains("basename=" + ConsoleErrors.Quote(basename), record);
            Assert.Contains("result=ERROR", record);
            Assert.Contains("reason=\"UPSTREAM_FAILED\"", record);
            Assert.Contains("hdr=" + ConsoleErrors.Quote(hdr), record);
            Assert.Contains("eml=" + ConsoleErrors.Quote(eml), record);
            string details = File.ReadAllText(diagnostic);
            Assert.Contains("HDR=" + ConsoleErrors.Quote(hdr), details);
            Assert.Contains("EML=" + ConsoleErrors.Quote(eml), details);
            Assert.Contains("HDR=" + ConsoleErrors.Quote(hdr), Tree.Errors.ToString());
            Assert.Contains("EML=" + ConsoleErrors.Quote(eml), Tree.Errors.ToString());
        }

        // Release the diagnostic stream before the fixture removes its verified temporary subtree.
        public void Dispose()
        {
            Diagnostics.Dispose();
            records.Dispose();
            Tree.Dispose();
        }
    }
}
