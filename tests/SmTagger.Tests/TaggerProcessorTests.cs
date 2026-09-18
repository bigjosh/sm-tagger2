using System.Text;
using SmTagger.Engine;
using SmTagger.Mail;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class TaggerProcessorTests
{
    // Confirms recipient ordering/deduplication, first spelling, group replies, body preservation, and evidence retention together.
    [Fact]
    public void FanOutPreservesOpaqueBytesAndCreatesStableIndividualAndGroupMappings()
    {
        using var fixture = new ProcessorFixture();
        byte[] body = [0, 255, 13, 10, 10, .. Encoding.ASCII.GetBytes("private@example.com in body")];
        var originals = fixture.WriteMessage("mail", ProcessorFixture.Header("Bob@example.net, ALICE@example.org,bob@EXAMPLE.NET",
            extra: "notify: bob@example.net=first\r\nnotify: ALICE@example.org=\r\nnotify: bob@example.net=second\r\nnotify: invalid\r\nx-extra: raw\r\n"), body: body);
        using var harness = fixture.Open(keep: true, log: true);

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        Assert.Equal(3, harness.Tags.Mappings.Count);
        var first = harness.Tags.Mappings[(ProcessorFixture.SenderId, "alice@example.org;")].TagAddress;
        var second = harness.Tags.Mappings[(ProcessorFixture.SenderId, "bob@example.net;")].TagAddress;
        var group = harness.Tags.Mappings[(ProcessorFixture.SenderId, "alice@example.org;bob@example.net;")].TagAddress;
        var firstHdr = File.ReadAllText(Path.Combine(fixture.SpoolDirectory, "mail-1.hdr"));
        var secondHdr = File.ReadAllText(Path.Combine(fixture.SpoolDirectory, "mail-2.hdr"));
        Assert.Contains($"\r\n{first}\r\nALICE@example.org\r\n", firstHdr);
        Assert.Contains($"\r\n{second}\r\nBob@example.net\r\n", secondHdr);
        Assert.Contains("notify: ALICE@example.org=\r\n", firstHdr);
        Assert.DoesNotContain("notify: bob", firstHdr);
        Assert.Contains("notify: bob@example.net=first\r\nnotify: bob@example.net=second\r\n", secondHdr);
        Assert.DoesNotContain("notify: invalid", secondHdr);
        Assert.Contains("auth: " + ProcessorFixture.Auth, firstHdr);
        for (var ordinal = 1; ordinal <= 2; ordinal++)
        {
            var output = File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, $"mail-{ordinal}.eml"));
            var headers = Encoding.ASCII.GetString(output[..^body.Length]);
            Assert.DoesNotContain("Return-Path:", headers);
            Assert.Contains($"Reply-To: \"Fixture Sender\" <{group}>\r\n", headers);
            Assert.Contains("To: Visible <visible@example.org>\r\n", headers);
            Assert.Equal(body, output[^body.Length..]);
        }

        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr.in")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.in")));
        Assert.Equal(6, Directory.GetFiles(fixture.ProcessDirectory).Length);
        Assert.All(Directory.GetFiles(fixture.ProcessDirectory), path => Assert.True(path.EndsWith(".in") || path.EndsWith(".out")));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(fixture.DataDirectory, "tag-addresses", group, "tag-log.txt")).Length);
    }

    // Confirms non-private Reply-To suppression does not allocate or log a group identity.
    [Fact]
    public void NonPrivateReplyToSuppressesGroupMapping()
    {
        using var fixture = new ProcessorFixture();
        const string reply = "rEpLy-To:\tOther <other@example.org>\r\n";
        fixture.WriteMessage("mail", ProcessorFixture.Header("alice@example.net,bob@example.net"),
            ProcessorFixture.Message(extra: reply));
        using var harness = fixture.Open();

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        Assert.Equal(2, harness.Tags.Mappings.Count);
        Assert.All(Directory.GetFiles(fixture.SpoolDirectory, "*.eml"),
            file => Assert.Contains(reply, File.ReadAllText(file)));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
    }

    // Rewrites only matching addresses in an existing group Reply-To mailbox list.
    [Fact]
    public void ExistingMixedReplyToUsesGroupTagAndPreservesOtherMailboxes()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail", ProcessorFixture.Header("alice@example.net,bob@example.net"),
            ProcessorFixture.Message(extra: "Reply-To: private@example.com,\r\n\tOther <other@example.org>\r\n"));
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        var group = harness.Tags.Mappings[(ProcessorFixture.SenderId, "alice@example.net;bob@example.net;")].TagAddress;
        Assert.All(Directory.GetFiles(fixture.SpoolDirectory, "*.eml"),
            file => Assert.Contains($"Reply-To: {group},\r\n\tOther <other@example.org>\r\n", File.ReadAllText(file)));
    }

    // Enforces activated From counts before recipient parsing and mapping allocation.
    [Theory]
    [InlineData("")]
    [InlineData("From: private@example.com\r\nfRoM: other@example.org\r\n")]
    [InlineData("From: private@example.com, other@example.org\r\n")]
    public void InvalidActivatedFromCountsRetainBothOriginalsAsErr(string from)
    {
        using var fixture = new ProcessorFixture();
        var originals = fixture.WriteMessage("mail", ProcessorFixture.Header("malformed recipient list"), ProcessorFixture.Message(from));
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr.err")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.err")));
        Assert.Contains("exactly one From", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err")));
        Assert.Contains("exactly one From", fixture.Errors.ToString());
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // Keeps a legitimate non-private From when another supported identity activates tagging.
    [Fact]
    public void ActivationDoesNotRequirePrivateFrom()
    {
        using var fixture = new ProcessorFixture();
        const string from = "From: Legitimate <other@example.org>\r\n";
        fixture.WriteMessage("mail", eml: ProcessorFixture.Message(from));
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        Assert.Contains(from, File.ReadAllText(Path.Combine(fixture.SpoolDirectory, "mail-1.eml")));
    }

    // Avoids recipient parsing and activated cardinality checks for syntax-valid no-match pairs.
    [Theory]
    [InlineData("")]
    [InlineData("From: other@example.org\r\nFrom: another@example.org\r\n")]
    [InlineData("From: other@example.org, another@example.org\r\n")]
    public void NoMatchPassesCountsAndRecipientBytesUnchanged(string from)
    {
        using var fixture = new ProcessorFixture();
        var hdr = ProcessorFixture.Header("not a recipient list", "other@example.org");
        var eml = ProcessorFixture.Message(from, "Reply-To: x@example.org\r\nReply-To: y@example.org\r\n")
            .Replace("Return-Path: <private@example.com>", "Return-Path: <>");
        var originals = fixture.WriteMessage("mail", hdr, eml);
        using var harness = fixture.Open(keep: true);
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "mail.hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "mail.eml")));
        Assert.Equal(4, Directory.GetFiles(fixture.ProcessDirectory).Length);
    }

    // Refuses syntactically ambiguous supported sender fields even when no other identity matches.
    [Theory]
    [InlineData("From: other@example.org (comment)\r\n")]
    [InlineData("Sender: one@example.org,two@example.org\r\n")]
    [InlineData("Reply-To: \"local\"@example.org\r\n")]
    public void MalformedSupportedFieldFailsBeforeNoMatchPassThrough(string malformed)
    {
        using var fixture = new ProcessorFixture();
        var eml = ProcessorFixture.Message("", malformed).Replace("<private@example.com>", "<>");
        fixture.WriteMessage("mail", ProcessorFixture.Header(sender: "other@example.org"), eml);
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.start")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // Distinguishes Reply-To repetition from the specially renamed From contract failures.
    [Fact]
    public void ActivatedRepeatedReplyToRetainsStartState()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail", eml: ProcessorFixture.Message(extra: "Reply-To: a@example.org\r\nReply-To: b@example.org\r\n"));
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.start")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.eml.start")));
        Assert.Empty(harness.Tags.Mappings);
    }

    // A former private address remains ordinary data even when another identity activates tagging.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FormerPrivateAddressDoesNotBlockCurrentPrivateTagging(bool hdrMatch)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteProfile("retired-private-addresses.txt", "former@example.com\r\n");
        fixture.WriteMessage("mail", ProcessorFixture.Header(sender: hdrMatch ? "former@example.com" : "other@example.org"),
            ProcessorFixture.Message(extra: hdrMatch ? "" : "Sender: former@example.com\r\n"));
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        string tag = Assert.Single(harness.Tags.Mappings).Value.TagAddress;
        string eml = File.ReadAllText(Path.Combine(fixture.SpoolDirectory, "mail-1.eml"));
        Assert.Contains($"From: \"Fixture Sender\" <{tag}>\r\n", eml);
        Assert.Contains("former@example.com", hdrMatch
            ? File.ReadAllText(Path.Combine(fixture.SpoolDirectory, "mail-1.hdr")) : eml);
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Empty(fixture.Errors.ToString());
    }

    // Only the current private identity activates edits; a former value passes unchanged in every supported location.
    [Theory]
    [InlineData("HDR line 2", false)]
    [InlineData("HDR line 2", true)]
    [InlineData("HDR from", false)]
    [InlineData("HDR from", true)]
    [InlineData("From", false)]
    [InlineData("From", true)]
    [InlineData("Reply-To", false)]
    [InlineData("Reply-To", true)]
    [InlineData("Sender", false)]
    [InlineData("Sender", true)]
    [InlineData("Resent-From", false)]
    [InlineData("Resent-From", true)]
    [InlineData("Resent-Sender", false)]
    [InlineData("Resent-Sender", true)]
    [InlineData("Return-Path", false)]
    [InlineData("Return-Path", true)]
    [InlineData("Disposition-Notification-To", false)]
    [InlineData("Disposition-Notification-To", true)]
    [InlineData("Return-Receipt-To", false)]
    [InlineData("Return-Receipt-To", true)]
    [InlineData("X-Confirm-Reading-To", false)]
    [InlineData("X-Confirm-Reading-To", true)]
    public void EachSupportedSenderLocationTagsCurrentOrPassesFormerIdentity(string location, bool former)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteProfile("retired-private-addresses.txt", "former@example.com\r\n");
        string identity = former ? "FORMER@EXAMPLE.COM" : "PRIVATE@EXAMPLE.COM";
        string envelope = location == "HDR line 2" ? identity : "<>";
        string metadataSender = location == "HDR from" ? identity : "";
        string recipient = former ? "unparsed recipients" : "alice@example.net";
        string hdr = $"Written \r\n{envelope}\r\n{recipient}\r\nauth: {ProcessorFixture.Auth}\r\nfRoM:\t{metadataSender} \r\n\r\n";
        string fromAddress = location == "From" ? identity : "other@example.org";
        string identityField = location switch
        {
            "HDR line 2" or "HDR from" or "From" => "",
            "Return-Path" => $"rEtUrN-pAtH:\r\n\t<{identity}>\r\n",
            _ => $"{location}:\t\"Identity\" <{identity}>\r\n"
        };
        string eml = $"fRoM: \"Fixture Sender\" <{fromAddress}>\r\n{identityField}"
            + "To: Visible <visible@example.org>\r\nContent-Type: text/plain\r\n\r\nunchanged body";
        var originals = fixture.WriteMessage("mail", hdr, eml);
        using var harness = fixture.Open();

        if (former)
        {
            Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
            Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "mail.hdr")));
            Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "mail.eml")));
            Assert.Empty(harness.Tags.Mappings);
            Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "tag-addresses")));
            Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
            Assert.Empty(fixture.Errors.ToString());
        }
        else
        {
            Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
            string tag = Assert.Single(harness.Tags.Mappings).Value.TagAddress;
            string expectedHdr = hdr.Replace(identity, tag, StringComparison.Ordinal);
            string expectedEml = location == "Return-Path"
                ? eml.Replace(identityField, "", StringComparison.Ordinal)
                : eml.Replace(identity, tag, StringComparison.Ordinal);
            Assert.Equal(Encoding.ASCII.GetBytes(expectedHdr), File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "mail-1.hdr")));
            Assert.Equal(Encoding.ASCII.GetBytes(expectedEml), File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "mail-1.eml")));
            Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        }
    }

    // Enforces MDN blocking before trigger classification while allowing explicit approved policy to skip classification.
    [Theory]
    [InlineData("multipart/report; report-type=disposition-notification", false, false)]
    [InlineData("message/disposition-notification", false, false)]
    [InlineData("message/disposition-notification", true, true)]
    [InlineData("bad malformed content type", true, true)]
    [InlineData("multipart/report; report-type=delivery-status", false, true)]
    public void MdnPolicyControlsClassificationWithoutForcingTagging(string contentType, bool allowMdn, bool success)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteProfile("allow-mdn.txt", allowMdn ? "true" : "false");
        fixture.WriteMessage("mail", ProcessorFixture.Header("unparsed recipients", "other@example.org"),
            ProcessorFixture.Message("From: other@example.org\r\n", contentType: contentType).Replace("<private@example.com>", "<>"));
        using var harness = fixture.Open();
        Assert.Equal(success ? MessageOutcome.Succeeded : MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Equal(success, File.Exists(Path.Combine(fixture.SpoolDirectory, "mail.hdr")));
    }

    // Rejects unknown and absent authenticated identities already inside the private process queue.
    [Theory]
    [InlineData("auth: unknown@example.com\r\n")]
    [InlineData("")]
    [InlineData("auth: <>\r\n")]
    [InlineData("auth: auth@example.com\r\nAuth: auth@example.com\r\n")]
    public void ProcessQueueNeverUsesSorterUnknownAuthPassRule(string authLine)
    {
        using var fixture = new ProcessorFixture();
        var hdr = ProcessorFixture.Header().Replace("auth: auth@example.com\r\n", authLine);
        fixture.WriteMessage("mail", hdr);
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // Reuses published mappings both later in one invocation and after a fresh startup load.
    [Fact]
    public void MappingsSurviveMessagesAndRestarts()
    {
        using var fixture = new ProcessorFixture();
        string firstTag;
        using (var harness = fixture.Open())
        {
            fixture.WriteMessage("first");
            Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("first"));
            firstTag = Assert.Single(harness.Tags.Mappings).Value.TagAddress;
            fixture.WriteMessage("second", ProcessorFixture.Header("ALICE@EXAMPLE.NET"));
            Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("second"));
            Assert.Single(harness.Tags.Mappings);
        }

        using var restarted = fixture.Open(random: _ => throw new InvalidOperationException("Existing identity must not allocate."));
        fixture.WriteMessage("third");
        Assert.Equal(MessageOutcome.Succeeded, restarted.Processor.Process("third"));
        Assert.Equal(firstTag, Assert.Single(restarted.Tags.Mappings).Value.TagAddress);
        Assert.Equal(3, File.ReadAllLines(Path.Combine(fixture.DataDirectory, "tag-addresses", firstTag, "tag-log.txt")).Length);
    }
}
