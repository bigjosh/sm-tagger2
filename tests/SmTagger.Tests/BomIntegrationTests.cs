using System.Text;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class BomIntegrationTests
{
    private static readonly byte[] Bom = [0xef, 0xbb, 0xbf];

    // A syntax-valid enrolled no-match pair passes byte-for-byte, including its BOM and invalid unused recipients.
    [Fact]
    public void EnrolledNoMatchPassesBomAndBodyUnchanged()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail", ProcessorFixture.Header("not parsed", "other@example.org"));
        byte[] original = WithBom("X-Opaque: first\r\nFrom: Other <other@example.org>\r\n\r\n", [0xef, 0xbb, 0xbf, 0, 0xff]);
        File.WriteAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml"), original);
        byte[] hdr = File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr"));
        using var harness = fixture.Open(keep: true);

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "mail.eml")));
        Assert.Equal(hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "mail.hdr")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.in")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.out")));
    }

    // Real tagged publication preserves original evidence, the output BOM, exact bodies, and group-copy bytes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TaggedBomChildrenPreserveKeepEvidenceAndBodies(bool group)
    {
        using var fixture = new ProcessorFixture();
        string recipients = group ? "alice@example.net,bob@example.net" : "alice@example.net";
        var originalPair = fixture.WriteMessage("mail", ProcessorFixture.Header(recipients));
        const string from = "From:\t\"Synthetic\"\r\n <private@example.com>\r\n";
        const string tail = "X-Opaque: unchanged\r\nContent-Type: application/octet-stream\r\n\r\n";
        byte[] body = [0xef, 0xbb, 0xbf, 0, 0xff, 13, 10, 0xef, 0xbb, 0xbf];
        byte[] original = WithBom("Return-Path:\r\n <private@example.com>\r\n" + from + tail, body);
        File.WriteAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml"), original);
        using var harness = fixture.Open(keep: true);

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.in")));
        Assert.Equal(originalPair.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr.in")));
        string? groupTag = group ? harness.Tags.Mappings[(ProcessorFixture.SenderId, "alice@example.net;bob@example.net;")].TagAddress : null;
        string[] addresses = group ? ["alice@example.net", "bob@example.net"] : ["alice@example.net"];
        for (int index = 0; index < addresses.Length; index++)
        {
            string individual = harness.Tags.Mappings[(ProcessorFixture.SenderId, addresses[index] + ";")].TagAddress;
            string reply = groupTag is null ? "" : from.Replace("From:", "Reply-To:", StringComparison.Ordinal)
                .Replace(ProcessorFixture.Private, groupTag, StringComparison.Ordinal);
            byte[] expected = WithBom(from.Replace(ProcessorFixture.Private, individual, StringComparison.Ordinal) + reply + tail, body);
            string child = "mail-" + (index + 1);
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, child + ".eml")));
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, child + ".eml.out")));
        }
        Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.eml.break")));
        Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
    }

    // A valid BOM must not turn an activated missing-From violation into a parser failure or bypass its .err disposition.
    [Fact]
    public void BomWithMissingFromUsesNormalFromContractRetention()
    {
        using var fixture = new ProcessorFixture();
        var originalPair = fixture.WriteMessage("mail", ProcessorFixture.Header("not parsed"));
        byte[] original = WithBom("Subject: deliberately no From\r\n\r\nbody");
        File.WriteAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml"), original);
        using var harness = fixture.Open(keep: true);

        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.err")));
        Assert.Equal(originalPair.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr.err")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.in")));
        Assert.Contains("exactly one From", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err")));
        Assert.Contains("exactly one From", fixture.Errors.ToString());
    }

    // A leading BOM must not hide either recognized MDN form from the default no-receipt policy.
    [Theory]
    [InlineData("message/disposition-notification")]
    [InlineData("multipart/report; report-type=disposition-notification; boundary=synthetic")]
    public void BomBeforeContentTypeStillRejectsMdnWithoutPrivateMatch(string contentType)
    {
        using var fixture = new ProcessorFixture();
        var originalPair = fixture.WriteMessage("mail", ProcessorFixture.Header(sender: "other@example.org"));
        byte[] original = WithBom("Content-Type: " + contentType + "\r\nFrom: other@example.org\r\n\r\nopaque report body");
        File.WriteAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml"), original);
        using var harness = fixture.Open(keep: true);

        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory, "mail-*"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.start")));
        Assert.Equal(originalPair.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr.start")));
        Assert.Contains("message-disposition notifications are disabled", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err")));
    }

    // A BOM on the first valid From cannot hide a second From from activated cardinality validation.
    [Fact]
    public void BomBeforeRepeatedFromUsesNormalFromContractRetention()
    {
        using var fixture = new ProcessorFixture();
        var originalPair = fixture.WriteMessage("mail");
        byte[] original = WithBom("From: private@example.com\r\nFrom: other@example.org\r\n\r\nbody");
        File.WriteAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml"), original);
        using var harness = fixture.Open(keep: true);

        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory, "mail-*"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.err")));
        Assert.Equal(originalPair.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr.err")));
        string diagnostic = File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err"));
        Assert.Contains("exactly one From", diagnostic);
        Assert.Contains("observed fields=2", diagnostic);
    }

    // Malformed encodings retain the claimed input without allocating mappings or publishing any child.
    [Theory]
    [InlineData("EFBBBFEFBBBF")]
    [InlineData("EFBB")]
    [InlineData("FFFE")]
    public void MalformedBomRetainsOriginalStartState(string prefixHex)
    {
        using var fixture = new ProcessorFixture();
        var originalPair = fixture.WriteMessage("mail");
        byte[] original = [.. Convert.FromHexString(prefixHex), .. Encoding.ASCII.GetBytes("From: private@example.com\r\n\r\nbody")];
        File.WriteAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml"), original);
        using var harness = fixture.Open(keep: true);

        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory, "mail-*"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.start")));
        Assert.Equal(originalPair.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr.start")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.in")));
        Assert.Contains("operation=\"parse-eml\"", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err")));
    }

    // Write the one allowed leading UTF-8 BOM explicitly and retain arbitrary message body bytes.
    private static byte[] WithBom(string header, byte[]? body = null) => [.. Bom, .. Encoding.UTF8.GetBytes(header), .. body ?? []];
}
