using System.Text;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class HeaderLengthIntegrationTests
{
    // Verifies the physical byte boundary using final allocated tag bytes, with no automatic folding.
    [Theory]
    [InlineData(998, true)]
    [InlineData(999, false)]
    public void EditedLineBoundaryControlsPublicationAndRetainsMappings(int outputLength, bool success)
    {
        using var fixture = new ProcessorFixture();
        const string tag = "tag-11111@reply.example.com";
        fixture.WriteMessage("mail", eml: ProcessorFixture.Message(FromSizedFor(tag, outputLength)));
        using var harness = fixture.Open();
        Assert.Equal(success ? MessageOutcome.Succeeded : MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Single(harness.Tags.Mappings);
        if (success)
        {
            var from = File.ReadAllLines(Path.Combine(fixture.SpoolDirectory, "mail-1.eml"))
                .Single(line => line.StartsWith("From:", StringComparison.Ordinal));
            Assert.Equal(998, Encoding.ASCII.GetByteCount(from));
            Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        }
        else
        {
            Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
            Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
            Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.eml.break")));
            var diagnostic = File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err"));
            Assert.Contains("999", diagnostic);
            Assert.Contains("998", diagnostic);
            Assert.Contains("mail-1", diagnostic);
            Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.err")));
        }
    }

    // Includes the synthesized Reply-To field-name growth even when its address has equal length.
    [Fact]
    public void SynthesizedReplyToOverflowRejectsAllChildren()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail", ProcessorFixture.Header("alice@example.net,bob@example.net"),
            ProcessorFixture.Message(FromSizedFor("tag-22222@reply.example.com", 998)));
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.Equal(2, harness.Tags.Mappings.Count);
        var diagnostic = File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err"));
        Assert.Contains("Reply-To", diagnostic);
        Assert.Contains("1002", diagnostic);
    }

    // A later child's longer permanent tag cannot publish an earlier ready child from the same parent.
    [Fact]
    public void LaterChildOverflowLeavesEarlierChildPendingAndMappingsReusable()
    {
        using var fixture = new ProcessorFixture();
        const string shortTag = "a@reply.example.com";
        const string longTag = "longer@reply.example.com";
        fixture.WriteMapping(shortTag, "alice@example.net;");
        fixture.WriteMapping(longTag, "bob@example.net;");
        fixture.WriteMessage("mail", ProcessorFixture.Header("alice@example.net,bob@example.net"),
            ProcessorFixture.Message(FromSizedFor(shortTag, 998), "Reply-To: deliberate@example.org\r\n"));
        using (var harness = fixture.Open())
        {
            Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
            Assert.Equal(2, harness.Tags.Mappings.Count);
            Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail-1.hdr.pend")));
            Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail-1.eml.pend")));
            Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail-2.hdr.pend")));
            Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
            Assert.Contains("mail-2", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err")));
        }

        using var restarted = fixture.Open(random: _ => throw new InvalidOperationException("must reuse mapping"));
        fixture.WriteMessage("fresh", ProcessorFixture.Header("bob@example.net"));
        Assert.Equal(MessageOutcome.Succeeded, restarted.Processor.Process("fresh"));
        Assert.Contains(longTag, File.ReadAllText(Path.Combine(fixture.SpoolDirectory, "fresh-1.eml")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail-1.hdr.pend")));
    }

    // Reloads and reuses a newly published mapping after its first message fails the output-length contract.
    [Fact]
    public void LengthFailurePreservesNewMappingForReuseAfterRestart()
    {
        using var fixture = new ProcessorFixture();
        const string proposedTag = "tag-11111@reply.example.com";
        var originals = fixture.WriteMessage("mail", eml: ProcessorFixture.Message(FromSizedFor(proposedTag, 999)));
        string allocatedTag;
        using (var harness = fixture.Open())
        {
            Assert.Empty(harness.Tags.Mappings);
            Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
            var mapping = Assert.Single(harness.Tags.Mappings).Value;
            allocatedTag = mapping.TagAddress;
            Assert.Equal(proposedTag, allocatedTag);
            Assert.Equal("alice@example.net;", File.ReadAllText(Path.Combine(mapping.DirectoryPath, "recipient-id.txt")));
            Assert.Equal(ProcessorFixture.SenderId, File.ReadAllText(Path.Combine(mapping.DirectoryPath, "sender-id.txt")));
            Assert.Empty(File.ReadAllBytes(Path.Combine(mapping.DirectoryPath, "tag-log.txt")));
            Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        }

        using var restarted = fixture.Open(random: _ => throw new InvalidOperationException("Retained mapping must be reused without allocation."));
        Assert.Equal(allocatedTag, Assert.Single(restarted.Tags.Mappings).Value.TagAddress);
        fixture.WriteMessage("fresh");
        Assert.Equal(MessageOutcome.Succeeded, restarted.Processor.Process("fresh"));
        Assert.Single(restarted.Tags.Mappings);
        Assert.Contains(allocatedTag, File.ReadAllText(Path.Combine(fixture.SpoolDirectory, "fresh-1.eml")));
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.break")));
        Assert.False(File.Exists(Path.Combine(fixture.SpoolDirectory, "mail-1.hdr")));
        Assert.Equal(new[] { "20260905T123456Z fresh-1" },
            File.ReadAllLines(Path.Combine(fixture.DataDirectory, "tag-addresses", allocatedTag, "tag-log.txt")));
    }

    // Failure to write the length diagnostic does not release an overlong rewritten message.
    [Fact]
    public void LoggingAndDiagnosticFailuresCannotOverrideHeaderRejection()
    {
        using var fixture = new ProcessorFixture();
        Directory.CreateDirectory(Path.Combine(fixture.DataDirectory, "log.txt"));
        Directory.CreateDirectory(Path.Combine(fixture.ProcessDirectory, "mail.err"));
        fixture.WriteMessage("mail", eml: ProcessorFixture.Message(FromSizedFor("tag-11111@reply.example.com", 999)));
        using var harness = fixture.Open(log: true);
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.Contains("999", fixture.Errors.ToString());
    }

    // Leaves existing oversized opaque lines and body lines untouched during an otherwise valid rewrite.
    [Fact]
    public void UnchangedOpaqueAndBodyLinesAreOutsideOutputCheck()
    {
        using var fixture = new ProcessorFixture();
        var opaque = "X-Opaque: " + new string('x', 1500) + "\r\n";
        var body = new string('b', 2000);
        fixture.WriteMessage("mail", eml: ProcessorFixture.Message(extra: opaque, body: body));
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        var output = File.ReadAllText(Path.Combine(fixture.SpoolDirectory, "mail-1.eml"));
        Assert.Contains(opaque, output);
        Assert.EndsWith(body, output);
    }

    // Selects display-name padding so the rewritten physical From line has an exact byte count.
    private static string FromSizedFor(string tag, int outputLength)
    {
        var overhead = Encoding.ASCII.GetByteCount($"From: \"\" <{tag}>");
        return $"From: \"{new string('N', outputLength - overhead)}\" <{ProcessorFixture.Private}>\r\n";
    }
}
