using System.Text;
using SmSorter;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class SorterTests
{
    // Pass no-auth mail unchanged even when no enrollment/configuration tree exists.
    [Fact]
    public void NoAuthPassesWithoutConfigurationOrReadingEml()
    {
        using SorterTestDirectory tree = new();
        byte[] hdr = tree.AddMessage("-message", metadata: "opaque: untouched\r\n");
        byte[] eml = [0, 255, 13, 10, 42];
        File.WriteAllBytes(tree.Input("-message.eml"), eml);
        using (FileStream unreadableEml = new(tree.Input("-message.eml"), FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
        {
            Assert.Equal(MessageOutcome.Succeeded, tree.Processor.Process("-message"));
        }

        Assert.Equal(hdr, File.ReadAllBytes(tree.Spool("-message.hdr")));
        Assert.Equal(eml, File.ReadAllBytes(tree.Spool("-message.eml")));
        Assert.False(File.Exists(tree.Input("-message.hdr")));
        Assert.False(File.Exists(tree.Input("-message.hdr.sort")));
        Assert.False(Directory.Exists(tree.Data("senders")));
        Assert.False(File.Exists(tree.Data("log.txt")));
    }

    // Folder existence alone diverts mail without requiring the pointer file or a valid profile.
    [Fact]
    public void EnrolledAuthDivertsWithoutLoadingTaggerConfiguration()
    {
        using SorterTestDirectory tree = new();
        byte[] hdr = tree.AddMessage("enrolled", "AuTh: Account@Example.com\r\n");
        Directory.CreateDirectory(tree.Data("senders/account@example.com"));
        Assert.Equal(MessageOutcome.Succeeded, tree.Processor.Process("enrolled"));
        Assert.Equal(hdr, File.ReadAllBytes(tree.Data("process/enrolled.hdr")));
        Assert.True(File.Exists(tree.Data("process/enrolled.eml")));
        Assert.False(File.Exists(tree.Spool("enrolled.hdr")));
        Assert.False(Directory.Exists(tree.Data("profiles")));
    }

    // A definitively absent child beneath an accessible senders root is safe to pass.
    [Fact]
    public void UnenrolledAuthPassesUnderAccessibleRoot()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("unrelated", "auth: account@example.com\r\n");
        Directory.CreateDirectory(tree.Data("senders"));
        Assert.Equal(MessageOutcome.Succeeded, tree.Processor.Process("unrelated"));
        Assert.True(File.Exists(tree.Spool("unrelated.hdr")));
    }

    // A valid email address that cannot be a literal Windows folder must bypass auth lookup.
    [Fact]
    public void StructurallyUnenrollableAuthPassesWithoutSendersRoot()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("slash-auth", "auth: account/part@example.com\r\n");
        Assert.Equal(MessageOutcome.Succeeded, tree.Processor.Process("slash-auth"));
        Assert.False(Directory.Exists(tree.Data("senders")));
    }

    // Missing or non-directory routing roots and entries must hold rather than misclassify absence.
    [Theory]
    [InlineData("missing-root")]
    [InlineData("file-root")]
    [InlineData("file-entry")]
    public void UnavailableAuthLookupRetainsOwnedPair(string arrangement)
    {
        using SorterTestDirectory tree = new();
        byte[] hdr = tree.AddMessage("held", "auth: account@example.com\r\n");
        if (arrangement == "file-root")
        {
            File.WriteAllText(tree.Data("senders"), "not a directory");
        }
        else if (arrangement == "file-entry")
        {
            Directory.CreateDirectory(tree.Data("senders"));
            File.WriteAllText(tree.Data("senders/account@example.com"), "not a directory");
        }

        Assert.Equal(MessageOutcome.Failed, tree.Processor.Process("held", watchMode: true));
        Assert.Equal(hdr, File.ReadAllBytes(tree.Input("held.hdr.sort")));
        Assert.True(File.Exists(tree.Input("held.eml")));
        Assert.True(File.Exists(tree.Input("held.sort.err")));
        Assert.False(File.Exists(tree.Spool("held.hdr")));
        Assert.Contains("safe routing could not be established", tree.Errors.ToString());
    }

    // Duplicate auth and malformed metadata cannot establish safe no-auth classification.
    [Theory]
    [InlineData("auth: account@example.com\r\nAUTH: account@example.com\r\n")]
    [InlineData("not a metadata line\r\n")]
    [InlineData("auth:\r\n")]
    public void UnsafeHdrBecomesInert(string metadata)
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("unsafe", metadata);
        Assert.Equal(MessageOutcome.Failed, tree.Processor.Process("unsafe"));
        Assert.True(File.Exists(tree.Input("unsafe.hdr.sort")));
        Assert.True(File.Exists(tree.Input("unsafe.eml")));
    }

    // A producer's open HDR remains untouched even when its sharing flags would allow a rename.
    [Fact]
    public void BusyHdrWaitsForProducerBeforeOwnership()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("unreadable");
        using (FileStream unreadableHdr = new(tree.Input("unreadable.hdr"), FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
        {
            Assert.Equal(MessageOutcome.Deferred, tree.Processor.Process("unreadable", watchMode: true));
            Assert.True(File.Exists(tree.Input("unreadable.hdr")));
            Assert.False(File.Exists(tree.Input("unreadable.hdr.sort")));
        }

        Assert.True(File.Exists(tree.Input("unreadable.eml")));
        Assert.False(File.Exists(tree.Input("unreadable.sort.err")));
        Assert.Equal(MessageOutcome.Succeeded, tree.Processor.Process("unreadable"));
    }

    // Failure to remove the selected live trigger is process-fatal and never overwrites a residual.
    [Fact]
    public void OwnershipCollisionIsFatalAndLeavesPlainInput()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("collision");
        File.WriteAllText(tree.Input("collision.hdr.sort"), "existing residual");
        Assert.Throws<FatalProcessingException>(() => tree.Processor.Process("collision", watchMode: true));
        Assert.True(File.Exists(tree.Input("collision.hdr")));
        Assert.True(File.Exists(tree.Input("collision.eml")));
        Assert.Equal("existing residual", File.ReadAllText(tree.Input("collision.hdr.sort")));
        Assert.False(File.Exists(tree.Input("collision.sort.err")));
    }

    // An EML destination collision preserves the owned source pair and permits later fresh mail.
    [Fact]
    public void EmlMoveCollisionIsMessageLocal()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("blocked");
        tree.AddMessage("later");
        File.WriteAllText(tree.Spool("blocked.eml"), "existing destination");
        Assert.Equal(MessageOutcome.Failed, tree.Processor.Process("blocked", watchMode: true));
        Assert.True(File.Exists(tree.Input("blocked.hdr.sort")));
        Assert.True(File.Exists(tree.Input("blocked.eml")));
        Assert.Equal("existing destination", File.ReadAllText(tree.Spool("blocked.eml")));
        Assert.Equal(MessageOutcome.Succeeded, tree.Processor.Process("later", watchMode: true));
    }

    // Preserve the destination EML and inert HDR when the second publication move fails.
    [Fact]
    public void HdrMoveCollisionRetainsPartialPublicationWithoutRollback()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("partial");
        byte[] eml = File.ReadAllBytes(tree.Input("partial.eml"));
        File.WriteAllText(tree.Spool("partial.hdr"), "existing trigger");
        Assert.Equal(MessageOutcome.Failed, tree.Processor.Process("partial"));
        Assert.Equal(eml, File.ReadAllBytes(tree.Spool("partial.eml")));
        Assert.False(File.Exists(tree.Input("partial.eml")));
        Assert.True(File.Exists(tree.Input("partial.hdr.sort")));
        Assert.Equal("existing trigger", File.ReadAllText(tree.Spool("partial.hdr")));
        Assert.Contains("publish HDR", File.ReadAllText(tree.Input("partial.sort.err")));
    }

    // A missing final EML prevents ownership because the producer may still be receiving it.
    [Fact]
    public void MissingEmlLeavesPlainHdrForProducer()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("missing-eml");
        File.Delete(tree.Input("missing-eml.eml"));
        Assert.Equal(MessageOutcome.Deferred, tree.Processor.Process("missing-eml"));
        Assert.True(File.Exists(tree.Input("missing-eml.hdr")));
        Assert.False(File.Exists(tree.Input("missing-eml.hdr.sort")));
        Assert.False(File.Exists(tree.Input("missing-eml.sort.err")));
    }

    // Missing scan entries are silent stale results while missing one-shot requests fail.
    [Fact]
    public void MissingUnownedHdrHasModeSpecificOutcome()
    {
        using SorterTestDirectory tree = new();
        File.WriteAllText(tree.Input("gone.eml"), "selected final EML");
        Assert.Equal(MessageOutcome.Stale, tree.Processor.Process("gone", watchMode: true));
        Assert.Empty(tree.Errors.ToString());
        Assert.Equal(MessageOutcome.Failed, tree.Processor.Process("gone"));
        Assert.NotEmpty(tree.Errors.ToString());
        Assert.False(File.Exists(tree.Input("gone.sort.err")));
    }

    // Failure to create a diagnostic neither overwrites an artifact nor changes the retained mail.
    [Fact]
    public void DiagnosticFailureIsBestEffort()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("held", "auth:\r\n");
        File.WriteAllText(tree.Input("held.sort.err"), "previous evidence");
        Assert.Equal(MessageOutcome.Failed, tree.Processor.Process("held", watchMode: true));
        Assert.Equal("previous evidence", File.ReadAllText(tree.Input("held.sort.err")));
        Assert.True(File.Exists(tree.Input("held.hdr.sort")));
        Assert.Contains("Cannot write sorter diagnostic", tree.Errors.ToString());
    }

    // Startup reporting reads only names and excludes unrelated tagger or debugging artifacts.
    [Fact]
    public void ResidualReportingDoesNotOpenOrRepairFiles()
    {
        using SorterTestDirectory tree = new();
        File.WriteAllText(tree.Input("owned.hdr.sort"), "residual");
        File.WriteAllText(tree.Input("old.sort.err"), "diagnostic");
        File.WriteAllText(tree.Input("debug.hdr.in"), "debug");
        File.WriteAllText(tree.Input("tagger.hdr.err"), "tagger");
        using (FileStream lockedResidual = new(tree.Input("owned.hdr.sort"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            tree.Processor.ReportResiduals();
        }

        string output = tree.Errors.ToString();
        Assert.Contains("owned.hdr.sort", output);
        Assert.Contains("old.sort.err", output);
        Assert.DoesNotContain("debug.hdr.in", output);
        Assert.DoesNotContain("tagger.hdr.err", output);
        Assert.Equal("residual", File.ReadAllText(tree.Input("owned.hdr.sort")));
    }
}

internal sealed class SorterTestDirectory : IDisposable
{
    public string Root { get; }
    public StringWriter Errors { get; } = new();
    public SorterProcessor Processor { get; }

    // Create one isolated tree whose trusted queue roots are all on the same temporary volume.
    public SorterTestDirectory()
    {
        Root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "sm-sorter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Input(""));
        Directory.CreateDirectory(Data(""));
        Processor = new SorterProcessor(Data(""), Spool(""), Errors);
    }

    // Resolve a path below this fixture's sorter input queue.
    public string Input(string name) => Path.Combine(Root, "spool", "proc", name);

    // Resolve a path below this fixture's common publication queue.
    public string Spool(string name) => Path.Combine(Root, "spool", name);

    // Resolve a path below this fixture's datadir.
    public string Data(string name) => Path.Combine(Root, "data", name);

    // Write separately synthesized example-domain bytes, never private server captures.
    public byte[] AddMessage(string basename, string metadata = "")
    {
        byte[] hdr = Encoding.ASCII.GetBytes("Written \r\nsender@example.com\r\nrecipient@example.net\r\n" + metadata + "\r\n");
        File.WriteAllBytes(Input(basename + ".hdr"), hdr);
        File.WriteAllBytes(Input(basename + ".eml"), Encoding.ASCII.GetBytes("From: sender@example.com\r\nSubject: fixture\r\n\r\nBody untouched.\r\n"));
        return hdr;
    }

    // Delete only the absolute, unique temporary root allocated by this fixture.
    public void Dispose()
    {
        string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(Root) != expectedParent || !Path.GetFileName(Root).StartsWith("sm-sorter-tests-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean a path outside this test's temporary root.");
        }

        Directory.Delete(Root, recursive: true);
        Errors.Dispose();
    }
}
