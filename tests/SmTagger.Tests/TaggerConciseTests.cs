using System.Text;
using SmTagger.Engine;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class TaggerConciseTests
{
    // Distinguishes allocation from both in-memory and freshly loaded permanent mapping reuse.
    [Fact]
    public void CreatedAndUsedDescribeEachLookupAcrossMessagesAndRestarts()
    {
        using var fixture = new ProcessorFixture();
        using var output = new StringWriter();
        string tag;
        using (var harness = Open(fixture, output))
        {
            fixture.WriteMessage("first");
            Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("first"));
            tag = Assert.Single(harness.Tags.Mappings).Value.TagAddress;
            fixture.WriteMessage("second", ProcessorFixture.Header("ALICE@EXAMPLE.NET"));
            Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("second"));
        }

        using (var restarted = Open(fixture, output,
            random: _ => throw new InvalidOperationException("An existing mapping must be reused.")))
        {
            fixture.WriteMessage("third");
            Assert.Equal(MessageOutcome.Succeeded, restarted.Processor.Process("third"));
            Assert.Equal(tag, Assert.Single(restarted.Tags.Mappings).Value.TagAddress);
        }

        string[] lines = Lines(output);
        Assert.Equal(3, lines.Length);
        Assert.Contains($": firstc1 from <{ProcessorFixture.Private}> to <alice@example.net>, auth <{ProcessorFixture.Auth}>, created tag <{tag}>, moved to spool", lines[0]);
        Assert.Contains($": secondc1 from <{ProcessorFixture.Private}> to <ALICE@EXAMPLE.NET>, auth <{ProcessorFixture.Auth}>, used tag <{tag}>, moved to spool", lines[1]);
        Assert.Contains($": thirdc1 from <{ProcessorFixture.Private}> to <alice@example.net>, auth <{ProcessorFixture.Auth}>, used tag <{tag}>, moved to spool", lines[2]);
        Assert.Equal(3, File.ReadAllLines(Path.Combine(fixture.DataDirectory, "tag-addresses", tag, "tag-log.txt")).Length);
        Assert.Empty(fixture.Errors.ToString());
    }

    // Reports each deduplicated child immediately after publication without a group or parent summary.
    [Fact]
    public void FanoutReportsOneLinePerPublishedPairUsingOriginalEnvelopeAndRecipientSpelling()
    {
        using var fixture = new ProcessorFixture();
        const string existing = "stable@reply.example.com";
        fixture.WriteMapping(existing, "alice@example.org;");
        fixture.WriteMessage("fanout", ProcessorFixture.Header("Bob@example.net, ALICE@example.org,bob@EXAMPLE.NET",
            sender: "Envelope@example.com"));
        using var output = new StringWriter();
        using var harness = Open(fixture, output);
        var published = 0;
        harness.Processor.ObserveCheckpoint = checkpoint =>
        {
            if (checkpoint.Phase == "ALL_CHILDREN_READY")
                Assert.Empty(output.ToString());
            if (checkpoint.Phase == "CHILD_PUBLISHED")
            {
                Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, checkpoint.ChildBasename + ".eml")));
                Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, checkpoint.ChildBasename + ".hdr")));
                Assert.Equal(++published, Lines(output).Length);
            }
        };

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("fanout"));
        string[] lines = Lines(output);
        Assert.Equal(2, lines.Length);
        Assert.Equal(2, published);
        string created = harness.Tags.Mappings[(ProcessorFixture.SenderId, "bob@example.net;")].TagAddress;
        string group = harness.Tags.Mappings[(ProcessorFixture.SenderId, "alice@example.org;bob@example.net;")].TagAddress;
        Assert.Contains($": fanoutc1 from <Envelope@example.com> to <ALICE@example.org>, auth <{ProcessorFixture.Auth}>, used tag <{existing}>, moved to spool", lines[0]);
        Assert.Contains($": fanoutc2 from <Envelope@example.com> to <Bob@example.net>, auth <{ProcessorFixture.Auth}>, created tag <{created}>, moved to spool", lines[1]);
        Assert.DoesNotContain(group, output.ToString());
        Assert.DoesNotContain("visible@example.org", output.ToString());
        Assert.Empty(fixture.Errors.ToString());
    }

    // Adds display information without turning previously unparsed recipient text into a mail validation rule.
    [Fact]
    public void NoMatchPassesMalformedRecipientsUnchangedAndReportsOriginalPair()
    {
        using var fixture = new ProcessorFixture();
        var originals = fixture.WriteMessage("unchanged",
            ProcessorFixture.Header("not a recipient list", "other@example.org"),
            ProcessorFixture.Message("From: other@example.org\r\n").Replace("<private@example.com>", "<>"));
        using var output = new StringWriter();
        using var harness = Open(fixture, output);

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("unchanged"));
        string line = Assert.Single(Lines(output));
        Assert.Contains(": unchanged from <other@example.org> to ", line);
        Assert.Contains("not a recipient list", line);
        Assert.Contains($", auth <{ProcessorFixture.Auth}>, moved to spool", line);
        Assert.DoesNotContain(" tag ", line);
        Assert.Empty(harness.Tags.Mappings);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "unchanged.hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "unchanged.eml")));
        Assert.Empty(fixture.Errors.ToString());
    }

    // Omits the failed child and every later child while preserving the earlier complete publication report.
    [Theory]
    [InlineData("eml")]
    [InlineData("hdr")]
    public void LaterPublicationCollisionReportsOnlyTheCompletedChild(string extension)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("partial", ProcessorFixture.Header("alice@example.net,bob@example.net,carol@example.net"));
        File.WriteAllText(Path.Combine(fixture.SpoolDirectory, "partialc2." + extension), "existing artifact");
        using var output = new StringWriter();
        using var harness = Open(fixture, output);

        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("partial"));
        Assert.Contains(": partialc1 from ", Assert.Single(Lines(output)));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "partialc1.hdr")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "partialc3.hdr.pend")));
        Assert.Contains("PUBLISHED", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "partial.err")));
        Assert.NotEmpty(fixture.Errors.ToString());
    }

    // A cleanup failure after publication cannot erase or duplicate the already accurate child report.
    [Fact]
    public void ParentCleanupFailureKeepsPublishedChildLineAndExistingErrorDiagnostics()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("cleanup");
        using var output = new StringWriter();
        using var harness = Open(fixture, output);
        FileStream? heldHeader = null;
        harness.Processor.ObserveCheckpoint = checkpoint =>
        {
            if (checkpoint.Phase == "BEFORE_PARENT_CLEANUP")
            {
                Assert.Single(Lines(output));
                heldHeader = new FileStream(Path.Combine(fixture.ProcessDirectory, "cleanup.hdr.break"),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        };
        try
        {
            Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("cleanup"));
            Assert.Contains(": cleanupc1 from ", Assert.Single(Lines(output)));
            Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "cleanupc1.hdr")));
            Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "cleanup.hdr.break")));
            Assert.Contains("PUBLISHED", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "cleanup.err")));
            Assert.NotEmpty(fixture.Errors.ToString());
        }
        finally
        {
            heldHeader?.Dispose();
        }
    }

    // Neither an incomplete pass-through move nor retained From-count evidence is a completed output pair.
    [Fact]
    public void UnsuccessfulPassThroughAndFromRetentionHaveNoConciseLines()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("pass", ProcessorFixture.Header(sender: "other@example.org"),
            ProcessorFixture.Message("From: other@example.org\r\n").Replace("<private@example.com>", "<>"));
        File.WriteAllText(Path.Combine(fixture.SpoolDirectory, "pass.hdr"), "existing artifact");
        fixture.WriteMessage("from", eml: ProcessorFixture.Message(""));
        using var output = new StringWriter();
        using var harness = Open(fixture, output);

        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("pass"));
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("from"));
        Assert.Equal(MessageOutcome.Stale, harness.Processor.Process("gone", watchMode: true));
        Assert.Empty(output.ToString());
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "pass.eml")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "pass.hdr.start")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "from.hdr.err")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "from.eml.err")));
        Assert.Contains("exactly one From", fixture.Errors.ToString());
    }

    // A broken concise console cannot interrupt publication, permanent tag logs, or the selected file trace.
    [Fact]
    public void BrokenConsoleStillPublishesAllChildrenAndWritesIndependentFileLogs()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("console", ProcessorFixture.Header("alice@example.net,bob@example.net"));
        using var output = new BrokenWriter();
        using (var harness = Open(fixture, output, log: true))
        {
            Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("console"));
            Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.hdr").Length);
            foreach (var mapping in harness.Tags.Mappings.Values)
                Assert.NotEmpty(File.ReadAllText(Path.Combine(mapping.DirectoryPath, "tag-log.txt")));
        }

        string trace = File.ReadAllText(Path.Combine(fixture.DataDirectory, "log.txt"));
        Assert.Contains("event=MESSAGE result=SUCCESS", trace);
        Assert.DoesNotContain("created tag", trace);
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Contains("console unavailable", fixture.Errors.ToString());
    }

    // Failed file diagnostics remain best effort and cannot suppress the same pair's concise success line.
    [Fact]
    public void FileLogFailuresStillReportTheCurrentPublishedPair()
    {
        using var fixture = new ProcessorFixture();
        const string tag = "stable@reply.example.com";
        string mapping = fixture.WriteMapping(tag, "alice@example.net;");
        Directory.CreateDirectory(Path.Combine(mapping, "tag-log.txt"));
        Directory.CreateDirectory(Path.Combine(fixture.DataDirectory, "log.txt"));
        fixture.WriteMessage("logs");
        using var output = new StringWriter();
        using var harness = Open(fixture, output, log: true);

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("logs"));
        Assert.Contains($", used tag <{tag}>, moved to spool", Assert.Single(Lines(output)));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "logsc1.hdr")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Contains("ERROR logging to", fixture.Errors.ToString());
    }

    // Composes ordinary production collaborators with deterministic allocation and a caller-owned console writer.
    private static ProcessorHarness Open(ProcessorFixture fixture, TextWriter output, bool log = false,
        Action<byte[]>? random = null)
    {
        var trace = TraceLog.Open(log ? Path.Combine(fixture.DataDirectory, "log.txt") : null, stderr: fixture.Errors);
        try
        {
            var configuration = SenderConfiguration.Load(fixture.DataDirectory, trace);
            byte counter = 0;
            var tags = TagStore.Load(fixture.DataDirectory, configuration, trace,
                randomBytes: random ?? (bytes => Array.Fill(bytes, ++counter)));
            var concise = new ConciseOutput(true, output, fixture.Errors);
            return new ProcessorHarness(trace, configuration, tags,
                new TaggerProcessor(fixture.SpoolDirectory, configuration, tags, trace, concise: concise));
        }
        catch
        {
            trace.Dispose();
            throw;
        }
    }

    // Counts physical console records without treating escaped message text as an additional line.
    private static string[] Lines(StringWriter output)
    {
        return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class BrokenWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        // Fails the actual console write boundary while leaving independent logs usable.
        public override void WriteLine(string? value)
        {
            throw new IOException("console unavailable");
        }
    }
}
