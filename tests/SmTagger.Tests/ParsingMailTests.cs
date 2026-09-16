using System.Text;
using SmTagger.Mail;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ParsingMailTests
{
    private const string Private = "secret@example.com";
    private const string Tag = "tag-00000@reply.example.com";
    private const string Group = "tag-11111@reply.example.com";

    // Verifies HDR sender partial matches, null spellings, duplicate recipients, and lenient notify association.
    [Fact]
    public void HdrChildrenPreserveOriginalSpellingAndUnrelatedBytes()
    {
        string text = "Written \r\nsecret@example.com\r\n B@example.net, a=b@example.net, b@EXAMPLE.NET\t\r\n"
            + "auth: USER@example.com\r\nfrom:\t<>  \r\nfrom: SECRET@EXAMPLE.COM\t\r\n"
            + "notify: B@example.net=opaque\r\nnotify:\ta=b@example.net=x=y\r\nnotify: a=b@example.net=\r\n"
            + "notify: broken\r\nnotify: a=b@example.net =bad\r\nextra: \u00ff\r\n\r\n";
        HdrDocument document = HdrDocument.Parse(Bytes(text));
        Assert.Equal("user@example.com", document.AuthAddress);
        Assert.Equal(new[] { Private, Private }, document.SenderAddresses);
        IReadOnlyList<Recipient> recipients = document.ParseRecipients();
        Assert.Equal(new[] { "a=b@example.net", "b@example.net" }, recipients.Select(recipient => recipient.CanonicalAddress));
        Assert.Equal("B@example.net", recipients[1].OriginalAddress);
        string child = Text(document.CreateChild(Private, Tag, recipients[0]));
        Assert.Equal("Written \r\n" + Tag + "\r\na=b@example.net\r\nauth: USER@example.com\r\nfrom:\t<>  \r\nfrom: " + Tag
            + "\t\r\nnotify:\ta=b@example.net=x=y\r\nnotify: a=b@example.net=\r\nextra: \u00ff\r\n\r\n", child);
    }

    // Confirms malformed recipients are intentionally deferred until activated fan-out.
    [Fact]
    public void HdrRecipientParsingIsDeferred()
    {
        HdrDocument document = HdrDocument.Parse(Bytes("Written \r\n<>\r\nnot a recipient\r\nauth: user@example.com\r\n\r\n"));
        Assert.Empty(document.SenderAddresses);
        Assert.Throws<MailContractException>(() => document.ParseRecipients());
    }

    // Requires exact positional null paths and validates every present HDR sender before trigger selection.
    [Theory]
    [InlineData(" <> ", "from: <>\r\n")]
    [InlineData("", "from: name <other@example.net>\r\n")]
    [InlineData("other@example.net", "from: a@example.net,b@example.net\r\n")]
    [InlineData("other@example.net", "from: \"other\"@example.net\r\n")]
    public void InvalidHdrSenderSyntaxFailsEvenWithoutPrivateMatch(string envelope, string metadata)
        => Assert.Throws<MailContractException>(() => HdrDocument.Parse(Bytes("Written \r\n" + envelope + "\r\na@example.net\r\nauth: user@example.com\r\n" + metadata + "\r\n")));

    // Exercises supported mailbox forms and exact address spans across opaque encoded-word punctuation.
    [Theory]
    [InlineData("secret@example.com")]
    [InlineData("<secret@example.com>")]
    [InlineData("Joe Public <SECRET@example.com>")]
    [InlineData("\"Joe (Public), \\\"J\\\"\" <secret@example.com>")]
    [InlineData("=?utf-8?Q?Joe,_Public?= <secret@example.com>")]
    [InlineData("=?utf-8?Q?Joe_(Public)<\"J\">?= <secret@example.com>")]
    [InlineData("=?literal <secret@example.com>")]
    [InlineData("=?utf-8?unknown?text?= <secret@example.com>")]
    [InlineData("=?utf-8?Q?text?=suffix <secret@example.com>")]
    [InlineData("Joe\r\n\tPublic <\r\n secret@example.com\t>")]
    public void MailboxFormsPreserveEverythingExceptTheAddress(string value)
    {
        string original = "fRoM: " + value + "\r\nX-Test: untouched\r\n\r\nbody\n\u0000\u00ff";
        EmlDocument document = EmlDocument.Parse(Bytes(original));
        Assert.Single(document.FromFields);
        MailboxAddress parsed = Assert.Single(document.FromFields[0].Addresses);
        Assert.Equal(Private, parsed.CanonicalAddress);
        byte[] child = document.CreateChild(Private, Tag, null);
        string expected = original[..parsed.Start] + Tag + original[(parsed.Start + parsed.Length)..];
        Assert.Equal(Bytes(expected), child);
    }

    // Prevents the encoded-word boundary scanner from rejecting legal literal local-part punctuation.
    [Theory]
    [InlineData("a=?b@example.com")]
    [InlineData("=?x@example.com")]
    [InlineData("a=b@example.com")]
    public void LiteralEncodedWordPrefixesInAddressesRemainAddresses(string address)
    {
        EmlDocument document = EmlDocument.Parse(Bytes("From: " + address + "\r\n\r\nbody"));
        Assert.Equal(address, Assert.Single(document.FromFields[0].Addresses).OriginalAddress);
    }

    // Requires every present supported field to have valid individual syntax before trigger policy.
    [Theory]
    [InlineData("From: \r\n")]
    [InlineData("From: Joe (comment) <secret@example.com>\r\n")]
    [InlineData("From: Group:secret@example.com;\r\n")]
    [InlineData("From: <secret@example.com\r\n")]
    [InlineData("From: secret@example.com,\r\n")]
    [InlineData("From: \"unterminated <secret@example.com>\r\n")]
    [InlineData("From: Joe <\"secret\"@example.com>\r\n")]
    [InlineData("From: \u00ff <secret@example.com>\r\n")]
    [InlineData("Sender: a@example.net,b@example.net\r\n")]
    [InlineData("Return-Path: secret@example.com\r\n")]
    [InlineData(" orphan continuation\r\n")]
    [InlineData("Bad Name: value\r\n")]
    public void UnsupportedIndividualSenderSyntaxFails(string headers)
        => Assert.Throws<MailContractException>(() => EmlDocument.Parse(Bytes(headers + "\r\nbody")));

    // Exposes individually valid counts without prematurely enforcing activated-only restrictions.
    [Fact]
    public void ParserLeavesActivatedCardinalityPolicyToTheCaller()
    {
        EmlDocument document = EmlDocument.Parse(Bytes("From: a@example.net,b@example.net\r\nFROM: c@example.net\r\nReply-To: a@example.net\r\nReply-To: b@example.net\r\n\r\nbody"));
        Assert.Equal(2, document.FromFields.Count);
        Assert.Equal(2, document.FromFields[0].Addresses.Count);
        Assert.Equal(2, document.ReplyToFields.Count);
        Assert.Empty(EmlDocument.Parse(Bytes("Subject: no From\r\n\r\nbody")).FromFields);
    }

    // Exercises field removal adjacent to insertion and all matching repeated supported identities.
    [Fact]
    public void GroupSynthesisAndReturnPathRemovalHaveNonoverlappingEdits()
    {
        string text = "From:\t\"Joe\" <secret@example.com>\r\nReturn-Path:\r\n <secret@example.com>\r\n"
            + "Return-Path: <>\r\nSender: secret@example.com\r\nResent-From: other@example.net,secret@example.com\r\n"
            + "Disposition-Notification-To: secret@example.com\r\nReturn-Receipt-To: secret@example.com\r\nX-Confirm-Reading-To: secret@example.com\r\n"
            + "To: secret@example.com\r\nX-Opaque: secret@example.com\r\n\r\nsecret@example.com";
        string child = Text(EmlDocument.Parse(Bytes(text)).CreateChild(Private, Tag, Group));
        Assert.StartsWith("From:\t\"Joe\" <" + Tag + ">\r\nReply-To:\t\"Joe\" <" + Group + ">\r\nSender: " + Tag, child);
        Assert.DoesNotContain("Return-Path:", child);
        Assert.Contains("Resent-From: other@example.net," + Tag + "\r\n", child);
        Assert.Contains("Disposition-Notification-To: " + Tag + "\r\n", child);
        Assert.EndsWith("To: secret@example.com\r\nX-Opaque: secret@example.com\r\n\r\nsecret@example.com", child);
    }

    // Keeps deliberate nonmatching Reply-To mailboxes and uses the group tag only at matching spans.
    [Fact]
    public void ExistingReplyToUsesThePreparedGroupTag()
    {
        EmlDocument document = EmlDocument.Parse(Bytes("From: other@example.net\r\nReply-To: secret@example.com, Other <other@example.net>\r\n\r\nbody"));
        Assert.Equal("From: other@example.net\r\nReply-To: " + Group + ", Other <other@example.net>\r\n\r\nbody", Text(document.CreateChild(Private, Tag, Group)));
    }

    // Checks output length after all address replacements on a physical line have been combined.
    [Fact]
    public void RepeatedAddressesContributeTogetherToThePhysicalLineLimit()
    {
        string list = string.Join(',', Enumerable.Repeat(Private, 50));
        EmlDocument document = EmlDocument.Parse(Bytes("From: " + Private + "\r\nReply-To: " + list + "\r\n\r\nbody"));
        HeaderLineLengthException error = Assert.Throws<HeaderLineLengthException>(() => document.CreateChild(Private, Tag, null));
        Assert.Equal("Reply-To", error.FieldName);
        Assert.Equal("Reply-To: ".Length + Tag.Length * 50 + 49, error.MeasuredLength);
    }

    // Separately exercises the top-level MDN classifier with parameters, casing, folds, and quoted pairs.
    [Theory]
    [InlineData("multipart/report; report-type=disposition-notification", true)]
    [InlineData(" MULTIPART / REPORT ; boundary=abc; REPORT-TYPE=\"Disposi\\tion-Notification\"\r\n\t; other=value", true)]
    [InlineData("message/disposition-notification", true)]
    [InlineData("multipart/report; report-type=delivery-status", false)]
    [InlineData("text/plain; charset=utf-8", false)]
    [InlineData("multipart/report; report-type=\"disposition- notification\"", false)]
    public void MdnClassificationUsesOnlySupportedTopLevelTypes(string value, bool expected)
    {
        EmlDocument document = EmlDocument.Parse(Bytes("From: secret@example.com\r\nContent-Type: " + value + "\r\n\r\nMIME body is not parsed"));
        Assert.Equal(expected, document.IsMdn());
    }

    // Demonstrates that allow-mdn callers can skip Content-Type validation entirely.
    [Theory]
    [InlineData("Content-Type: multipart/report; report-type=\r\n")]
    [InlineData("Content-Type: text/plain; x=\"\"\r\n")]
    [InlineData("Content-Type: text/plain; x=a; X=b\r\n")]
    [InlineData("Content-Type: text/plain (comment)\r\n")]
    [InlineData("Content-Type: text/plain\r\nContent-Type: text/plain\r\n")]
    public void ContentTypeErrorsAreDeferredToMdnClassification(string field)
    {
        EmlDocument document = EmlDocument.Parse(Bytes("From: secret@example.com\r\n" + field + "\r\nbody"));
        Assert.Throws<MailContractException>(() => document.IsMdn());
        Assert.Contains("From: " + Tag, Text(document.CreateChild(Private, Tag, null)));
    }

    // Checks exact output physical-line boundaries without folding or rejecting untouched long content.
    [Fact]
    public void EditedPhysicalLinesHaveAnExact998ByteLimit()
    {
        string prefix = "From: \"";
        string suffix = "\" <" + Private + ">";
        int displayLength = 998 - prefix.Length - ("\" <" + Tag + ">").Length;
        string field = prefix + new string('x', displayLength) + suffix;
        EmlDocument valid = EmlDocument.Parse(Bytes(field + "\r\n\r\nbody"));
        Assert.Equal(998, Text(valid.CreateChild(Private, Tag, null)).IndexOf("\r\n", StringComparison.Ordinal));
        EmlDocument invalid = EmlDocument.Parse(Bytes(prefix + new string('x', displayLength + 1) + suffix + "\r\n\r\nbody"));
        HeaderLineLengthException error = Assert.Throws<HeaderLineLengthException>(() => invalid.CreateChild(Private, Tag, null));
        Assert.Equal(999, error.MeasuredLength);
        Assert.Equal("From", error.FieldName);
        string untouched = "From: \"" + new string('x', 1100) + "\" <other@example.net>\r\nReply-To: " + Private + "\r\nX-Opaque: " + new string('x', 1200) + "\r\n\r\n" + new string('x', 1500);
        Assert.Contains(new string('x', 1200), Text(EmlDocument.Parse(Bytes(untouched)).CreateChild(Private, Tag, null)));
    }

    // Covers the four-byte synthesized field-name growth and unchanged continuations in edited fields.
    [Fact]
    public void SynthesizedReplyToChecksAllPhysicalLines()
    {
        string equalLengthTag = "public@example.com";
        string prefix = "From: \"";
        int displayLength = 998 - prefix.Length - ("\" <" + equalLengthTag + ">").Length;
        EmlDocument document = EmlDocument.Parse(Bytes(prefix + new string('x', displayLength) + "\" <" + Private + ">\r\n\r\nbody"));
        HeaderLineLengthException error = Assert.Throws<HeaderLineLengthException>(() => document.CreateChild(Private, equalLengthTag, equalLengthTag));
        Assert.Equal("Reply-To", error.FieldName);
        Assert.Equal(1002, error.MeasuredLength);
        string folded = "From: " + new string('x', 1100) + "\r\n <" + Private + ">\r\n\r\nbody";
        EmlDocument unchangedFirstLine = EmlDocument.Parse(Bytes(folded));
        Assert.Contains(Tag, Text(unchangedFirstLine.CreateChild(Private, Tag, null)));
        Assert.Throws<HeaderLineLengthException>(() => unchangedFirstLine.CreateChild(Private, Tag, Group));
        string twoValidLines = "From: " + new string('x', 700) + "\r\n " + new string('y', 700) + " <" + Private + ">\r\n\r\nbody";
        Assert.Contains(Group, Text(EmlDocument.Parse(Bytes(twoValidLines)).CreateChild(Private, Tag, Group)));
    }

    // Includes continuation indentation and retained trailing whitespace at the exact edited-line boundary.
    [Theory]
    [InlineData(998, true)]
    [InlineData(999, false)]
    public void ChangedContinuationHasAnExact998ByteLimit(int outputLength, bool success)
    {
        const string prefix = "\t\"";
        string padding = new('x', outputLength - prefix.Length - ("\" <" + Tag + ">\t").Length);
        string original = "From:\r\n" + prefix + padding + "\" <" + Private + ">\t\r\n\r\nbody";
        EmlDocument document = EmlDocument.Parse(Bytes(original));
        if (success)
        {
            byte[] child = document.CreateChild(Private, Tag, null);
            Assert.Equal(Bytes(original.Replace(Private, Tag, StringComparison.Ordinal)), child);
            Assert.Equal(998, Bytes(Text(child).Split("\r\n")[1]).Length);
        }
        else
        {
            HeaderLineLengthException error = Assert.Throws<HeaderLineLengthException>(() => document.CreateChild(Private, Tag, null));
            Assert.Equal("From", error.FieldName);
            Assert.Equal(2, error.PhysicalLineNumber);
            Assert.Equal(999, error.MeasuredLength);
            Assert.Equal(998, error.Limit);
        }
    }

    // Checks a synthesized continuation independently of an unchanged, shorter non-private From line.
    [Theory]
    [InlineData(998, true)]
    [InlineData(999, false)]
    public void SynthesizedContinuationHasAnExact998ByteLimit(int outputLength, bool success)
    {
        const string prefix = "\t\"";
        string padding = new('x', outputLength - prefix.Length - ("\" <" + Group + ">\t").Length);
        string from = "From:\r\n" + prefix + padding + "\" <other@example.net>\t\r\n";
        string original = from + "Sender: " + Private + "\r\n\r\nbody";
        Assert.True(Bytes(from.Split("\r\n")[1]).Length <= 998);
        EmlDocument document = EmlDocument.Parse(Bytes(original));
        if (success)
        {
            byte[] child = document.CreateChild(Private, Tag, Group);
            string reply = "Reply-To:\r\n" + prefix + padding + "\" <" + Group + ">\t\r\n";
            Assert.Equal(Bytes(from + reply + "Sender: " + Tag + "\r\n\r\nbody"), child);
            Assert.Equal(998, Bytes(Text(child).Split("\r\n")[3]).Length);
        }
        else
        {
            HeaderLineLengthException error = Assert.Throws<HeaderLineLengthException>(() => document.CreateChild(Private, Tag, Group));
            Assert.Equal("Reply-To", error.FieldName);
            Assert.Equal(2, error.PhysicalLineNumber);
            Assert.Equal(999, error.MeasuredLength);
            Assert.Equal(998, error.Limit);
        }
    }

    // Creates byte-accurate fixtures including opaque non-ASCII and binary body bytes.
    private static byte[] Bytes(string value) => Encoding.Latin1.GetBytes(value);

    // Projects one-byte fixture output into text without replacing opaque bytes.
    private static string Text(byte[] value) => Encoding.Latin1.GetString(value);
}
