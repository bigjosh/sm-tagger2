using System.Text;
using SmSorter;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class SorterReadinessTests
{
    // Without the final EML, even an unreadable HDR must remain completely outside processing.
    [Theory]
    [InlineData("Writing \r\nprovisional@example.com\r\npartial routing metadata")]
    [InlineData("Failed \r\nsender@example.com\r\nrecipient@example.net\r\n\r\n")]
    [InlineData("")]
    [InlineData("Writ")]
    public void MissingEmlLeavesHdrEntirelyUntouched(string content)
    {
        using ReadinessFixture fixture = new();
        byte[] provisional = Encoding.ASCII.GetBytes(content);
        File.WriteAllBytes(fixture.Tree.Input("waiting.hdr"), provisional);
        using (FileStream lockedHdr = new(fixture.Tree.Input("waiting.hdr"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(MessageOutcome.Stale, fixture.Processor.Process("waiting", watchMode: true));
        }

        AssertMissingEmlLeavesInput(fixture, "waiting", provisional);

        Assert.Contains("event=STALE", fixture.Output.ToString());
        Assert.Contains("operation=\"check EML readiness\"", fixture.Output.ToString());
        Assert.DoesNotContain("event=READ_HDR", fixture.Output.ToString());
        Assert.Empty(fixture.Tree.Errors.ToString());
        Assert.False(Directory.Exists(fixture.Tree.Data("senders/auth-addresses")));
    }

    // Only exact Written with optional trailing ASCII space/tab admits the existing HDR classification path.
    [Theory]
    [InlineData("Written")]
    [InlineData("Written ")]
    [InlineData("Written\t \t")]
    public void FinalEmlAndWrittenStatusMakePairEligible(string status)
    {
        using ReadinessFixture fixture = new();
        byte[] hdr = Encoding.ASCII.GetBytes(status + "\r\nsender@example.com\r\nrecipient@example.net\r\n\r\n");
        byte[] eml = [0, 255, 42];
        File.WriteAllBytes(fixture.Tree.Input("status.hdr"), hdr);
        File.WriteAllBytes(fixture.Tree.Input("status.eml"), eml);

        Assert.Equal(MessageOutcome.Succeeded, fixture.Processor.Process("status", watchMode: true));

        Assert.Equal(hdr, File.ReadAllBytes(fixture.Tree.Spool("status.hdr")));
        Assert.Equal(eml, File.ReadAllBytes(fixture.Tree.Spool("status.eml")));
        Assert.Contains("result=PASS", Assert.Single(fixture.Records()));
        Assert.Empty(fixture.Tree.Errors.ToString());
    }

    // Failed upstream messages are retained as owned errors without blocking the next complete pair.
    [Theory]
    [InlineData("Failed")]
    [InlineData("Failed \t")]
    public void FailedHdrIsHeldAndLaterMessagesContinue(string status)
    {
        using ReadinessFixture fixture = new();
        byte[] hdr = Encoding.ASCII.GetBytes(status + "\r\nsender@example.com\r\nrecipient@example.net\r\n\r\n");
        byte[] eml = [0, 255, 42];
        File.WriteAllBytes(fixture.Tree.Input("failed.hdr"), hdr);
        File.WriteAllBytes(fixture.Tree.Input("failed.eml"), eml);

        Assert.Equal(MessageOutcome.Failed, fixture.Processor.Process("failed", watchMode: true));

        Assert.Equal(hdr, File.ReadAllBytes(fixture.Tree.Work("failed/failed.hdr.sort")));
        Assert.Equal(eml, File.ReadAllBytes(fixture.Tree.Work("failed/failed.eml")));
        Assert.False(File.Exists(fixture.Tree.Input("failed.hdr")));
        Assert.False(File.Exists(fixture.Tree.Input("failed.hdr.sort")));
        Assert.False(File.Exists(fixture.Tree.Input("failed.eml")));
        Assert.True(File.Exists(fixture.Tree.Work("failed/failed.sort.err")));
        Assert.False(File.Exists(fixture.Tree.Spool("failed.hdr")));
        string record = Assert.Single(fixture.Records());
        Assert.Contains("result=ERROR", record);
        Assert.Contains("reason=\"UPSTREAM_FAILED\"", record);
        Assert.NotEmpty(fixture.Tree.Errors.ToString());

        fixture.Tree.AddMessage("later");
        Assert.Equal(MessageOutcome.Succeeded, fixture.Processor.Process("later", watchMode: true));
        Assert.Equal(2, fixture.Records().Length);
        Assert.Contains("result=PASS", fixture.Records()[1]);
    }

    // A readable Written status admits validation of the remaining HDR, whose malformed bytes are still held.
    [Theory]
    [InlineData("Written\r\n")]
    [InlineData("Written \r\ninvalid incomplete framing")]
    [InlineData("Written\r\nsender@example.com\r\nrecipient@example.net\r\ninvalid metadata\r\n\r\n")]
    public void MalformedHdrAfterWrittenStatusIsHeld(string content)
    {
        using ReadinessFixture fixture = new();
        byte[] hdr = Encoding.ASCII.GetBytes(content);
        File.WriteAllBytes(fixture.Tree.Input("malformed.hdr"), hdr);
        File.WriteAllBytes(fixture.Tree.Input("malformed.eml"), [42]);

        Assert.Equal(MessageOutcome.Failed, fixture.Processor.Process("malformed", watchMode: true));

        Assert.Equal(hdr, File.ReadAllBytes(fixture.Tree.Input("malformed.hdr.sort")));
        Assert.True(File.Exists(fixture.Tree.Input("malformed.eml")));
        Assert.True(File.Exists(fixture.Tree.Input("malformed.sort.err")));
        Assert.Contains("reason=\"UNSAFE_HDR\"", Assert.Single(fixture.Records()));
        Assert.NotEmpty(fixture.Tree.Errors.ToString());
    }

    // A completed HDR waits for its final companion without retaining an error or caching routing state.
    [Fact]
    public void WrittenHdrWithoutEmlIsAcceptedOnALaterAttempt()
    {
        using ReadinessFixture fixture = new();
        byte[] hdr = fixture.Tree.AddMessage("later-eml");
        byte[] eml = File.ReadAllBytes(fixture.Tree.Input("later-eml.eml"));
        File.Delete(fixture.Tree.Input("later-eml.eml"));

        AssertMissingEmlLeavesInput(fixture, "later-eml", hdr);
        Assert.Contains("operation=\"check EML readiness\"", fixture.Output.ToString());
        Assert.Empty(fixture.Tree.Errors.ToString());

        File.WriteAllBytes(fixture.Tree.Input("later-eml.eml"), eml);
        Assert.Equal(MessageOutcome.Succeeded, fixture.Processor.Process("later-eml", watchMode: true));
        Assert.Equal(hdr, File.ReadAllBytes(fixture.Tree.Spool("later-eml.hdr")));
        Assert.Equal(eml, File.ReadAllBytes(fixture.Tree.Spool("later-eml.eml")));
        Assert.Contains("result=PASS", Assert.Single(fixture.Records()));
        Assert.False(File.Exists(fixture.Tree.Input("later-eml.sort.err")));
    }

    // A sharing violation before ownership is temporary even when the producer permits renaming.
    [Fact]
    public void WriterHeldHdrStaysPlainUntilTheHandleCloses()
    {
        using ReadinessFixture fixture = new();
        byte[] hdr = fixture.Tree.AddMessage("busy");
        using (FileStream writer = new(fixture.Tree.Input("busy.hdr"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.Delete))
        {
            Assert.Equal(MessageOutcome.Deferred, fixture.Processor.Process("busy", watchMode: true));
            Assert.True(File.Exists(fixture.Tree.Input("busy.hdr")));
            Assert.False(File.Exists(fixture.Tree.Input("busy.hdr.sort")));
            Assert.False(File.Exists(fixture.Tree.Input("busy.sort.err")));
            Assert.True(File.Exists(fixture.Tree.Input("busy.eml")));
            Assert.Empty(fixture.Records());
            Assert.Empty(fixture.Tree.Errors.ToString());
        }

        Assert.Contains("reason=\"HDR_BUSY\"", fixture.Output.ToString());
        Assert.Equal(MessageOutcome.Succeeded, fixture.Processor.Process("busy", watchMode: true));
        Assert.Equal(hdr, File.ReadAllBytes(fixture.Tree.Spool("busy.hdr")));
        Assert.Contains("result=PASS", Assert.Single(fixture.Records()));
    }

    // A producer that permits readers is still excluded by the sorter's reciprocal sharing contract.
    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public void ReaderPermittingHdrWriterStillDefersUntilClosed(FileShare producerSharing)
    {
        using ReadinessFixture fixture = new();
        byte[] eml = [0, 255, 42, 13, 10];
        byte[] prefix = Encoding.ASCII.GetBytes("Written \r\nsender@example.com\r\n");
        byte[] suffix = Encoding.ASCII.GetBytes("recipient@example.net\r\n\r\n");
        File.WriteAllBytes(fixture.Tree.Input("reader-shared.eml"), eml);

        using (FileStream writer = new(fixture.Tree.Input("reader-shared.hdr"), FileMode.CreateNew,
            FileAccess.Write, producerSharing))
        {
            writer.Write(prefix);
            writer.Flush();
            using (FileStream permissiveReader = new(fixture.Tree.Input("reader-shared.hdr"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] visiblePrefix = new byte[prefix.Length];
                permissiveReader.ReadExactly(visiblePrefix);
                Assert.Equal(prefix, visiblePrefix);
            }

            Assert.Equal(MessageOutcome.Deferred, fixture.Processor.Process("reader-shared", watchMode: true));
            Assert.True(File.Exists(fixture.Tree.Input("reader-shared.hdr")));
            Assert.True(File.Exists(fixture.Tree.Input("reader-shared.eml")));
            Assert.False(File.Exists(fixture.Tree.Input("reader-shared.hdr.sort")));
            Assert.False(File.Exists(fixture.Tree.Input("reader-shared.sort.err")));
            Assert.False(File.Exists(fixture.Tree.Spool("reader-shared.hdr")));
            Assert.False(File.Exists(fixture.Tree.Spool("reader-shared.eml")));
            Assert.Empty(fixture.Records());
            Assert.Empty(fixture.Tree.Errors.ToString());
            Assert.Contains("reason=\"HDR_BUSY\"", fixture.Output.ToString());

            writer.Write(suffix);
            writer.Flush();
        }

        Assert.Equal(MessageOutcome.Succeeded, fixture.Processor.Process("reader-shared", watchMode: true));
        Assert.Equal([.. prefix, .. suffix], File.ReadAllBytes(fixture.Tree.Spool("reader-shared.hdr")));
        Assert.Equal(eml, File.ReadAllBytes(fixture.Tree.Spool("reader-shared.eml")));
        Assert.Contains("result=PASS", Assert.Single(fixture.Records()));
        Assert.Empty(fixture.Tree.Errors.ToString());
    }

    // Replacement at completion must use the final envelope and auth instead of the provisional header.
    [Fact]
    public void FinalEmlPublicationUsesTheCompletedReplacementHdr()
    {
        using ReadinessFixture fixture = new();
        byte[] provisional = Encoding.ASCII.GetBytes("Writing \r\nearly@example.com\r\nearly@example.net\r\n\r\n");
        File.WriteAllBytes(fixture.Tree.Input("replacement.hdr"), provisional);
        AssertMissingEmlLeavesInput(fixture, "replacement", provisional);

        Directory.CreateDirectory(fixture.Tree.Data("senders/auth-addresses/final@example.com"));
        byte[] finalHdr = Encoding.ASCII.GetBytes("Written \t\r\nfinal@example.com\r\nfinal@example.net\r\n"
            + "auth: FINAL@EXAMPLE.COM\r\nopaque: completed metadata\r\n\r\n");
        byte[] finalEml = [0, 255, 42, 13, 10, 0, 128];
        File.WriteAllBytes(fixture.Tree.Input("replacement.eml.tmp"), finalEml);
        AssertMissingEmlLeavesInput(fixture, "replacement", provisional);
        File.WriteAllBytes(fixture.Tree.Input("replacement.pending"), finalHdr);
        File.Move(fixture.Tree.Input("replacement.pending"), fixture.Tree.Input("replacement.hdr"), overwrite: true);
        AssertMissingEmlLeavesInput(fixture, "replacement", finalHdr);
        File.Move(fixture.Tree.Input("replacement.eml.tmp"), fixture.Tree.Input("replacement.eml"));

        Assert.Equal(MessageOutcome.Succeeded, fixture.Processor.Process("replacement", watchMode: true));

        Assert.Equal(finalHdr, File.ReadAllBytes(fixture.Tree.Work("process/replacement.hdr")));
        Assert.Equal(finalEml, File.ReadAllBytes(fixture.Tree.Work("process/replacement.eml")));
        Assert.False(File.Exists(fixture.Tree.Spool("replacement.hdr")));
        Assert.False(File.Exists(fixture.Tree.Input("replacement.sort.err")));
        string record = Assert.Single(fixture.Records());
        Assert.Contains("result=DIVERT", record);
        Assert.Contains("auth=\"final@example.com\"", record);
        Assert.DoesNotContain("early@example.com", record);
        Assert.Empty(fixture.Tree.Errors.ToString());
    }

    // Check both the temporary outcome and every externally visible ownership or terminal-log boundary.
    private static void AssertMissingEmlLeavesInput(ReadinessFixture fixture, string basename, byte[] hdr)
    {
        Assert.Equal(MessageOutcome.Stale, fixture.Processor.Process(basename, watchMode: true));
        Assert.Equal(hdr, File.ReadAllBytes(fixture.Tree.Input(basename + ".hdr")));
        Assert.False(File.Exists(fixture.Tree.Input(basename + ".eml")));
        Assert.False(File.Exists(fixture.Tree.Input(basename + ".hdr.sort")));
        Assert.False(File.Exists(fixture.Tree.Input(basename + ".sort.err")));
        Assert.False(File.Exists(fixture.Tree.Spool(basename + ".hdr")));
        Assert.False(File.Exists(fixture.Tree.Spool(basename + ".eml")));
        Assert.False(Directory.Exists(fixture.Tree.Work("process")));
        Assert.Empty(fixture.Records());
    }

    private sealed class ReadinessFixture : IDisposable
    {
        private readonly MemoryStream log = new();
        private readonly SorterDiagnostics diagnostics;
        public SorterTestDirectory Tree { get; } = new();
        public StringWriter Output { get; } = new();
        public SorterProcessor Processor { get; }

        // Bind real routing and independently inspectable diagnostic sinks to an isolated test queue.
        public ReadinessFixture()
        {
            diagnostics = SorterDiagnostics.OpenForTesting(Tree.Data("sorter.log"), true, Output, Tree.Errors,
                () => new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero), _ => log);
            Processor = new SorterProcessor(Tree.Data(""), Tree.Spool(""), Tree.Errors, diagnostics);
        }

        // Decode only synthesized terminal records without requiring disposal of the active log stream.
        public string[] Records() => Encoding.UTF8.GetString(log.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Close diagnostic outputs before deleting the fixture's verified temporary subtree.
        public void Dispose()
        {
            diagnostics.Dispose();
            log.Dispose();
            Output.Dispose();
            Tree.Dispose();
        }
    }
}
