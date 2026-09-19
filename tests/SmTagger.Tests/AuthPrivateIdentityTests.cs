using System.Text;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class AuthPrivateIdentityTests
{
    // Auth remains only a selector when its shared private identity occurs nowhere in the sender surface.
    [Fact]
    public void SharedAuthAddressAloneDoesNotActivateTagging()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteProfile("private-address.txt", "AUTH@EXAMPLE.COM\r\n");
        byte[] body = [0, 255, .. Encoding.ASCII.GetBytes("auth@example.com remains opaque body data")];
        var original = fixture.WriteMessage("auth-only", ProcessorFixture.Header("unparsed recipient data"), body: body);
        using ProcessorHarness harness = fixture.Open(random: _ => throw new InvalidOperationException("Auth alone cannot allocate a tag."));

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("auth-only"));
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "auth-only.hdr")));
        Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "auth-only.eml")));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Empty(fixture.Errors.ToString());
    }

    // Shared-role identities rewrite sender fields while preserving auth, opaque bytes, and mapping reuse across aliases.
    [Theory]
    [InlineData("Auth@Example.COM", "alice@example.net")]
    [InlineData("Alias@Example.COM", "alice@example.net")]
    [InlineData("Auth@Example.COM", "alice@example.net,bob@example.net")]
    [InlineData("Alias@Example.COM", "alice@example.net,bob@example.net")]
    public void SharedPrivateIdentityTagsAndReusesMappingsAcrossAliases(string auth, string recipients)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteProfile("private-address.txt", "AUTH@EXAMPLE.COM\r\n");
        string aliasDirectory = Path.Combine(fixture.DataDirectory, "senders", "auth-addresses", "alias@example.com");
        Directory.CreateDirectory(aliasDirectory);
        File.WriteAllText(Path.Combine(aliasDirectory, "sender-id.txt"), ProcessorFixture.SenderId);
        Dictionary<string, string> originalTags;
        using (ProcessorHarness first = fixture.Open())
        {
            ProcessAndAssert(fixture, first, "first", auth, recipients);
            originalTags = first.Tags.Mappings.ToDictionary(item => item.Key.RecipientId, item => item.Value.TagAddress);
        }

        using ProcessorHarness restarted = fixture.Open(random: _ => throw new InvalidOperationException("Aliases must reuse published tags."));
        string otherAuth = auth.StartsWith("Auth", StringComparison.Ordinal) ? "Alias@Example.COM" : "Auth@Example.COM";
        ProcessAndAssert(fixture, restarted, "restarted", otherAuth, recipients);
        Assert.Equal(originalTags.Count, restarted.Tags.Mappings.Count);
        foreach ((string recipientId, string tag) in originalTags)
            Assert.Equal(tag, restarted.Tags.Mappings[(ProcessorFixture.SenderId, recipientId)].TagAddress);
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Empty(fixture.Errors.ToString());
    }

    // Verify each child byte-for-byte while the source auth shares its configured private address.
    private static void ProcessAndAssert(ProcessorFixture fixture, ProcessorHarness harness,
        string basename, string auth, string recipients)
    {
        const string identity = "Auth@Example.COM";
        byte[] body = [0, 255, 13, 10, .. Encoding.ASCII.GetBytes(identity + " in body")];
        string eml = $"Return-Path: <{identity}>\r\nFrom: Sender <{identity}>\r\n" +
            $"To: Visible <visible@example.org>\r\nSender: <{identity}>\r\nReply-To: <{identity}>\r\n" +
            $"X-Opaque: {identity}\r\nContent-Type: text/plain\r\n\r\n";
        fixture.WriteMessage(basename, ProcessorFixture.Header(recipients, identity, auth), eml, body);
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process(basename));

        string[] addresses = recipients.Split(',');
        string replyTag = harness.Tags.Mappings[(ProcessorFixture.SenderId, string.Join(';', addresses) + ";")].TagAddress;
        Assert.Equal(addresses.Length == 1 ? 1 : 3, harness.Tags.Mappings.Count);
        for (int index = 0; index < addresses.Length; index++)
        {
            string tag = harness.Tags.Mappings[(ProcessorFixture.SenderId, addresses[index] + ";")].TagAddress;
            string outputBasename = Path.Combine(fixture.SpoolDirectory, $"{basename}c{index + 1}");
            Assert.Equal(Encoding.ASCII.GetBytes(ProcessorFixture.Header(addresses[index], tag, auth)),
                File.ReadAllBytes(outputBasename + ".hdr"));
            string expectedHeaders = $"From: Sender <{tag}>\r\nTo: Visible <visible@example.org>\r\n" +
                $"Sender: <{tag}>\r\nReply-To: <{replyTag}>\r\nX-Opaque: {identity}\r\nContent-Type: text/plain\r\n\r\n";
            Assert.Equal([.. Encoding.ASCII.GetBytes(expectedHeaders), .. body], File.ReadAllBytes(outputBasename + ".eml"));
        }
    }
}
