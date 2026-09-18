using System.Text;
using SmTagger.Mail;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class EmlBomTests
{
    private const string Private = "private@example.com";
    private const string Tag = "tag@example.net";
    private const string Group = "grp@example.net";
    private static readonly byte[] Bom = [0xef, 0xbb, 0xbf];

    // Preserves an optional leading BOM while keeping sender spans in original-byte coordinates.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleChildPreservesPrefixAndAllUneditedBytes(bool leadingBom)
    {
        const string header = "From:\t\"A Sender\" <private@example.com>\r\nSubject: unchanged\r\n\r\n";
        byte[] body = [0, 0xef, 0xbb, 0xbf, 0xff, 0xfe, 13, 10, 0];
        byte[] original = Message(header, body, leadingBom);
        EmlDocument document = EmlDocument.Parse(original);
        MailboxAddress address = Assert.Single(Assert.Single(document.FromFields).Addresses);

        Assert.Equal(leadingBom ? 3 : 0, document.Fields[0].Start);
        Assert.Equal(Encoding.ASCII.GetBytes(Private), original.AsSpan(address.Start, address.Length).ToArray());
        Assert.Equal(Message(header.Replace(Private, Tag, StringComparison.Ordinal), body, leadingBom),
            document.CreateChild(Private, Tag, null));
    }

    // Copies a folded From into group Reply-To without copying the message prefix into that field.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupReplyToCopyPreservesExactlyOneLeadingBom(bool leadingBom)
    {
        const string from = "From:\t\"A Sender\"\r\n <private@example.com>\t\r\n";
        const string tail = "Subject: preserved\r\n\r\nbody";
        byte[] original = Message(from + tail, [], leadingBom);
        string expected = from.Replace(Private, Tag, StringComparison.Ordinal)
            + from.Replace("From:", "Reply-To:", StringComparison.Ordinal).Replace(Private, Group, StringComparison.Ordinal) + tail;

        Assert.Equal(Message(expected, [], leadingBom), EmlDocument.Parse(original).CreateChild(Private, Tag, Group));
    }

    // Removing first folded and repeated Return-Path fields must leave the message BOM ahead of the next field.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeadingReturnPathsAreRemovedWithoutRemovingBom(bool opaqueFirstSurvivor)
    {
        string opaque = opaqueFirstSurvivor ? "X-Opaque: unchanged\r\n" : "";
        string original = "Return-Path:\r\n\t<private@example.com>\r\nReturn-Path: <>\r\n"
            + opaque + "From: private@example.com\r\nReturn-Path: <other@example.org>\r\n\r\nbody";
        string expected = opaque + "From: " + Tag + "\r\n\r\nbody";

        Assert.Equal(Message(expected), EmlDocument.Parse(Message(original)).CreateChild(Private, Tag, null));
    }

    // A BOM-prefixed opaque field must not disturb later address offsets or an existing mixed Reply-To.
    [Fact]
    public void OpaqueFirstFieldAndExistingReplyToRetainExactBytes()
    {
        const string original = "X-Opaque: untouched\r\n\tfolded bytes\r\nFrom: private@example.com\r\n"
            + "Reply-To: private@example.com, Other <other@example.org>\r\n\r\nbody";
        string expected = "X-Opaque: untouched\r\n\tfolded bytes\r\nFrom: " + Tag + "\r\n"
            + "Reply-To: " + Group + ", Other <other@example.org>\r\n\r\nbody";

        Assert.Equal(Message(expected), EmlDocument.Parse(Message(original)).CreateChild(Private, Tag, Group));
    }

    // BOM sequences inside opaque values and bodies remain data rather than additional encoding prefixes.
    [Fact]
    public void OpaqueValueAndBinaryBodyBomBytesRemainUntouched()
    {
        const string original = "From: private@example.com\r\nX-Opaque: \ufeffvalue\r\n\t\ufeffcontinuation\r\n\r\n";
        byte[] body = [0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf, 0xff, 0xfe, 0, 13, 10];
        byte[] input = Message(original, body);

        Assert.Equal(Message(original.Replace(Private, Tag, StringComparison.Ordinal), body),
            EmlDocument.Parse(input).CreateChild(Private, Tag, null));
    }

    // Missing From remains a cardinality decision even when a valid BOM precedes an empty header block.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyHeaderBlockWithOptionalBomHasNoFields(bool leadingBom)
    {
        EmlDocument document = EmlDocument.Parse(Message("\r\n\r\nbody", [], leadingBom));

        Assert.Empty(document.Fields);
        Assert.Empty(document.FromFields);
    }

    // Only one complete UTF-8 BOM at byte zero is an admitted prefix; other encodings and positions fail.
    [Theory]
    [InlineData("EFBBBFEFBBBF")]
    [InlineData("EF")]
    [InlineData("EFBB")]
    [InlineData("FFFE")]
    [InlineData("FEFF")]
    [InlineData("FFFE0000")]
    [InlineData("0000FEFF")]
    [InlineData("20EFBBBF")]
    [InlineData("09EFBBBF")]
    public void MalformedEncodingPrefixesAreRejected(string prefixHex)
    {
        byte[] input = [.. Convert.FromHexString(prefixHex), .. Encoding.ASCII.GetBytes("From: private@example.com\r\n\r\nbody")];

        Assert.Throws<MailContractException>(() => EmlDocument.Parse(input));
    }

    // An encoding marker in a subsequent field name is invalid even when the message prefix is valid.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BomInLaterFieldNameIsRejected(bool leadingBom)
    {
        byte[] input = Message("Subject: first\r\n\ufeffFrom: private@example.com\r\n\r\nbody", [], leadingBom);

        Assert.Throws<MailContractException>(() => EmlDocument.Parse(input));
    }

    // Charges the retained three-byte prefix to the first edited output line, including after Return-Path removal.
    [Theory]
    [InlineData(false, 998, true)]
    [InlineData(false, 999, false)]
    [InlineData(true, 998, true)]
    [InlineData(true, 999, false)]
    public void FirstEditedOutputLineIncludesBomBytes(bool removeLeadingReturnPath, int outputLength, bool success)
    {
        string leading = removeLeadingReturnPath ? "Return-Path:\r\n <private@example.com>\r\nReturn-Path: <>\r\n" : "";
        string from = SizedFrom(Tag, outputLength - Bom.Length);
        EmlDocument document = EmlDocument.Parse(Message(leading + from + "\r\nbody"));

        if (success)
        {
            byte[] child = document.CreateChild(Private, Tag, null);
            Assert.Equal(Message(from.Replace(Private, Tag, StringComparison.Ordinal) + "\r\nbody"), child);
            Assert.Equal(outputLength, child.AsSpan().IndexOf("\r\n"u8));
        }
        else
        {
            HeaderLineLengthException error = Assert.Throws<HeaderLineLengthException>(() => document.CreateChild(Private, Tag, null));
            Assert.Equal("From", error.FieldName);
            Assert.Equal(1, error.PhysicalLineNumber);
            Assert.Equal(outputLength, error.MeasuredLength);
        }
    }

    // A later edited physical line keeps its own boundary without being charged for the message prefix.
    [Theory]
    [InlineData(998, true)]
    [InlineData(999, false)]
    public void NonfirstEditedLineDoesNotIncludeBomBytes(int outputLength, bool success)
    {
        string original = "X-Opaque: first\r\n" + SizedFrom(Tag, outputLength) + "\r\nbody";
        EmlDocument document = EmlDocument.Parse(Message(original));

        if (success)
        {
            byte[] child = document.CreateChild(Private, Tag, null);
            Assert.Equal(Message(original.Replace(Private, Tag, StringComparison.Ordinal)), child);
            Assert.Equal(outputLength, Encoding.UTF8.GetString(child).Split("\r\n")[1].Length);
        }
        else
        {
            HeaderLineLengthException error = Assert.Throws<HeaderLineLengthException>(() => document.CreateChild(Private, Tag, null));
            Assert.Equal("From", error.FieldName);
            Assert.Equal(outputLength, error.MeasuredLength);
        }
    }

    // The synthesized Reply-To has no BOM and uses its actual new field-name and address byte lengths.
    [Theory]
    [InlineData(998, true)]
    [InlineData(999, false)]
    public void SynthesizedReplyToBoundaryDoesNotIncludeBomBytes(int outputLength, bool success)
    {
        string from = SizedFrom(Group, outputLength - "Reply-To".Length + "From".Length);
        EmlDocument document = EmlDocument.Parse(Message(from + "\r\nbody"));

        if (success)
        {
            byte[] child = document.CreateChild(Private, Tag, Group);
            string reply = from.Replace("From:", "Reply-To:", StringComparison.Ordinal).Replace(Private, Group, StringComparison.Ordinal);
            Assert.Equal(Message(from.Replace(Private, Tag, StringComparison.Ordinal) + reply + "\r\nbody"), child);
            Assert.Equal(outputLength, Encoding.UTF8.GetString(child).Split("\r\n")[1].Length);
        }
        else
        {
            HeaderLineLengthException error = Assert.Throws<HeaderLineLengthException>(() => document.CreateChild(Private, Tag, Group));
            Assert.Equal("Reply-To", error.FieldName);
            Assert.Equal(outputLength, error.MeasuredLength);
        }
    }

    // Build a private From whose rewritten physical line has the requested ASCII length before CRLF.
    private static string SizedFrom(string target, int length) => "From: \""
        + new string('x', length - Encoding.ASCII.GetByteCount("From: \"\" <" + target + ">"))
        + "\" <" + Private + ">\r\n";

    // Encode text faithfully and append arbitrary body bytes without letting a writer add or strip a BOM.
    private static byte[] Message(string text, byte[]? body = null, bool leadingBom = true) =>
        [.. leadingBom ? Bom : [], .. Encoding.UTF8.GetBytes(text), .. body ?? []];
}
