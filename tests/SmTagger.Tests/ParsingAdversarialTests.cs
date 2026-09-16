using System.Text;
using SmTagger.Mail;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ParsingAdversarialTests
{
    private const int MalformedSeed = 0x5A17;
    private const int ValidSeed = 0x71C3;
    private const string AtomCharacters = "abcdefghijklmnopqrstuvwxyz0123456789!#$%&'*+-/=?^_`{|}~";
    private static readonly string[] Whitespace = ["", " ", "\t", "\r\n ", "\r\n\t", " \r\n\t "];

    // Exercises arbitrary framing and small mutations without allowing incidental indexing exceptions.
    [Fact]
    public void ArbitraryShortFixturesHaveStableClassificationAndOnlyContractFailures()
    {
        var random = new Random(MalformedSeed);
        byte[] hdrSeed = Bytes("Written \r\nsecret@example.com\r\na=b@example.net,b@example.org\r\nauth: user@example.com\r\nfrom: <>\r\nnotify: a=b@example.net=x=y\r\n\r\n");
        byte[] emlSeed = Bytes("From: =?utf-8?Q?A,(B)?= <secret@example.com>\r\nReply-To:\r\n \"A\\\"B\" <secret@example.com>,other@example.net\r\nReturn-Path: <>\r\nContent-Type: multipart/report; report-type=\"disposition-notification\"\r\n\r\nbody");
        for (int iteration = 0; iteration < 3072; iteration++)
        {
            byte[] hdr = iteration % 3 == 0 ? ArbitraryBytes(random) : Mutate(random, hdrSeed);
            byte[] eml = iteration % 3 == 0 ? Join(ArbitraryBytes(random), "\r\n\r\n"u8.ToArray()) : Mutate(random, emlSeed);
            FastHdrResult? first = null;
            AssertContractOutcome(() => first = FastHdrClassifier.Classify(hdr), "HDR classifier", iteration, hdr);
            Assert.NotNull(first);
            Assert.Equal(first, FastHdrClassifier.Classify(hdr));
            AssertContractOutcome(() => HdrDocument.Parse(hdr).ParseRecipients(), "HDR parser", iteration, hdr);
            AssertContractOutcome(() => EmlDocument.Parse(eml).IsMdn(), "EML parser/MDN", iteration, eml);
        }
    }

    // Composes expected headers independently of parser spans across randomized mailbox and folding forms.
    [Fact]
    public void GeneratedValidMailPreservesEveryByteOutsideItsSpecifiedEdits()
    {
        var random = new Random(ValidSeed);
        for (int iteration = 0; iteration < 1024; iteration++)
        {
            string privateAddress = "sender." + Atom(random, 8) + "@example.com";
            string originalPrivate = ChangeCase(random, privateAddress);
            string tag = "tag-" + iteration.ToString("x") + "@reply.example.com";
            string? group = random.Next(3) == 0 ? null : "group-" + iteration.ToString("x") + "@reply.example.com";
            bool existingReply = random.Next(2) == 0;
            bool matchingFrom = random.Next(2) == 0;
            int returnPathPosition = random.Next(3);
            string returnPath = ChangeCase(random, "Return-Path") + ":" + White(random) + "<" + originalPrivate + ">" + White(random) + "\r\n";
            var original = new StringBuilder();
            var expected = new StringBuilder();
            if (returnPathPosition == 0) original.Append(returnPath);

            MailboxShape from = Shape(random);
            string fromName = ChangeCase(random, "From");
            string fromAddress = matchingFrom ? originalPrivate : "Deliberate@other.example";
            original.Append(fromName).Append(':').Append(from.Render(fromAddress)).Append("\r\n");
            expected.Append(fromName).Append(':').Append(from.Render(matchingFrom ? tag : fromAddress)).Append("\r\n");
            if (group is not null && !existingReply)
                expected.Append("Reply-To:").Append(from.Render(group)).Append("\r\n");
            if (returnPathPosition == 1) original.Append(returnPath);

            if (existingReply)
            {
                MailboxShape reply = Shape(random);
                MailboxShape otherReply = Shape(random);
                string name = ChangeCase(random, "Reply-To");
                string separator = White(random) + "," + White(random);
                string other = otherReply.Render("Other@other.example");
                original.Append(name).Append(':').Append(reply.Render(originalPrivate)).Append(separator).Append(other).Append("\r\n");
                expected.Append(name).Append(':').Append(reply.Render(group ?? tag)).Append(separator).Append(other).Append("\r\n");
            }

            MailboxShape sender = Shape(random);
            string senderName = ChangeCase(random, "Sender");
            original.Append(senderName).Append(':').Append(sender.Render(originalPrivate)).Append("\r\n");
            expected.Append(senderName).Append(':').Append(sender.Render(tag)).Append("\r\n");
            MailboxShape repeated = Shape(random);
            string repeatedName = ChangeCase(random, "Resent-From");
            string repeatedSeparator = "," + White(random);
            original.Append(repeatedName).Append(':').Append(repeated.Render(originalPrivate)).Append(repeatedSeparator).Append(originalPrivate).Append("\r\n");
            expected.Append(repeatedName).Append(':').Append(repeated.Render(tag)).Append(repeatedSeparator).Append(tag).Append("\r\n");
            if (returnPathPosition == 2) original.Append(returnPath);

            string opaque = "To: " + originalPrivate + "\r\nX-Opaque: \u00ff" + originalPrivate + "\r\n\t=unknown\r\nMessage-ID: <" + originalPrivate + ">\r\n";
            original.Append(opaque).Append("\r\n");
            expected.Append(opaque).Append("\r\n");
            byte[] body = new byte[random.Next(80)];
            random.NextBytes(body);
            byte[] input = Join(Bytes(original.ToString()), body);
            byte[] expectedBytes = Join(Bytes(expected.ToString()), body);
            byte[] actual = [];
            Exception? error = Record.Exception(() => actual = EmlDocument.Parse(input).CreateChild(privateAddress, tag, group));
            Assert.True(error is null, $"Valid seed {ValidSeed}, iteration {iteration}: {error}");
            Assert.True(expectedBytes.AsSpan().SequenceEqual(actual), $"Byte mismatch for valid seed {ValidSeed}, iteration {iteration}.");
        }
    }

    // Independently renders child envelopes with varied punctuation, duplicate spellings, and opaque notify data.
    [Fact]
    public void GeneratedValidHdrChildrenUseFirstRecipientSpellingAndExactNotifyAssociation()
    {
        var random = new Random(ValidSeed + 1);
        for (int iteration = 0; iteration < 512; iteration++)
        {
            string privateAddress = "sender." + Atom(random, 8) + "@example.com";
            string originalPrivate = ChangeCase(random, privateAddress);
            string recipient = "a." + Atom(random, 8) + "@example.net";
            string originalRecipient = ChangeCase(random, recipient);
            string tag = "tag-" + iteration.ToString("x") + "@reply.example.com";
            string envelope = iteration % 3 == 0 ? "" : iteration % 3 == 1 ? "<>" : originalPrivate;
            string expectedEnvelope = iteration % 3 == 2 ? tag : envelope;
            string auth = ChangeCase(random, "auth") + ":\tUsEr@Example.Com \r\n";
            string fromName = ChangeCase(random, "from");
            string keptNotify = ChangeCase(random, "notify") + ":\t" + originalRecipient + "==" + Atom(random, 12) + "\r\n";
            string otherKeptNotify = "notify: " + ChangeCase(random, recipient) + "=\u00ff\r\n";
            string opaque = "other: \u0000\u00ff=untouched\r\nfrom: <>\t\r\n\r\n";
            string input = "Written \r\n" + envelope + "\r\n\t" + originalRecipient + " , other@example.org," + ChangeCase(random, recipient)
                + "\t\r\n" + auth + fromName + ": \t" + originalPrivate + " \t\r\n"
                + keptNotify + "notify: other@example.org=drop\r\nnotify: " + recipient + " =drop\r\nnotify: broken\r\n" + otherKeptNotify + opaque;
            string expected = "Written \r\n" + expectedEnvelope + "\r\n" + originalRecipient + "\r\n" + auth + fromName + ": \t" + tag + " \t\r\n"
                + keptNotify + otherKeptNotify + opaque;
            byte[] inputBytes = Bytes(input);
            FastHdrResult classified = FastHdrClassifier.Classify(inputBytes);
            Assert.Equal(FastHdrKind.ValidAuth, classified.Kind);
            Assert.Equal("user@example.com", classified.CanonicalAuth);
            HdrDocument document = HdrDocument.Parse(inputBytes);
            Assert.Equal(classified.CanonicalAuth, document.AuthAddress);
            Recipient selected = Assert.Single(document.ParseRecipients(), value => value.CanonicalAddress == recipient);
            Assert.Equal(originalRecipient, selected.OriginalAddress);
            byte[] actual = document.CreateChild(privateAddress, tag, selected);
            Assert.True(Bytes(expected).AsSpan().SequenceEqual(actual), $"HDR byte mismatch for seed {ValidSeed + 1}, iteration {iteration}.");
        }
    }

    // Reports a minimal reproducible seed and byte fixture when malformed input escapes its contract exception.
    private static void AssertContractOutcome(Action action, string phase, int iteration, byte[] input)
    {
        Exception? error = Record.Exception(action);
        if (error is not null and not MailContractException)
            Assert.Fail($"{phase}; seed {MalformedSeed}; iteration {iteration}; input {Convert.ToHexString(input)}; unexpected {error}");
    }

    // Produces short arbitrary bytes with frequent grammar punctuation and occasional unrestricted binary bytes.
    private static byte[] ArbitraryBytes(Random random)
    {
        const string punctuation = "abcXYZ019@.<>(),:;\\\"=?% /\t\r\n";
        byte[] result = new byte[random.Next(193)];
        for (int index = 0; index < result.Length; index++)
            result[index] = random.Next(4) == 0 ? (byte)random.Next(256) : (byte)punctuation[random.Next(punctuation.Length)];
        return result;
    }

    // Keeps valid structural neighborhoods reachable while varying delimiters, lengths, controls, and endings.
    private static byte[] Mutate(Random random, byte[] seed)
    {
        var result = new List<byte>(seed);
        int operations = random.Next(1, 5);
        for (int operation = 0; operation < operations; operation++)
        {
            int position = random.Next(result.Count + 1);
            switch (random.Next(4))
            {
                case 0:
                    if (position < result.Count) result[position] = (byte)random.Next(256);
                    break;
                case 1:
                    result.Insert(position, (byte)random.Next(256));
                    break;
                case 2:
                    if (position < result.Count) result.RemoveAt(position);
                    break;
                default:
                    result.RemoveRange(position, result.Count - position);
                    break;
            }
        }
        return result.ToArray();
    }

    // Selects a bounded mailbox form while keeping independently known prefix and suffix bytes.
    private static MailboxShape Shape(Random random)
    {
        string leading = White(random);
        string trailing = White(random);
        if (random.Next(6) == 0) return new(leading, trailing);
        string display = random.Next(7) switch
        {
            0 => "",
            1 => "Name" + WhiteRequired(random) + "Public",
            2 => "\"Name (Public), \\\"Q\\\" \\\\" + White(random) + "Text\"",
            3 => "=?utf-8?Q?Name_(Public),<\"Q\">?=",
            4 => "Name" + WhiteRequired(random) + "=?utf-8?Q?Public,(Q)?=",
            5 => "=?literal" + WhiteRequired(random) + "=?utf-8?Q?word?=suffix",
            _ => Atom(random, 12)
        };
        return new(leading + display + White(random) + "<" + White(random), White(random) + ">" + trailing);
    }

    // Builds a legal ASCII local-part atom without using the production grammar or canonicalizer.
    private static string Atom(Random random, int length)
    {
        var result = new StringBuilder(length);
        for (int index = 0; index < length; index++) result.Append(AtomCharacters[random.Next(AtomCharacters.Length)]);
        return result.ToString();
    }

    // Varies only ASCII letter casing so equality expectations do not depend on the production canonicalizer.
    private static string ChangeCase(Random random, string value)
        => string.Concat(value.Select(character => character is >= 'a' and <= 'z' && random.Next(2) == 0 ? (char)(character - 32) : character));

    // Samples optional syntactically legal folding whitespace.
    private static string White(Random random) => Whitespace[random.Next(Whitespace.Length)];

    // Samples required whitespace between distinct display-name tokens.
    private static string WhiteRequired(Random random) => Whitespace[random.Next(1, Whitespace.Length)];

    // Encodes fixture text one byte per character, retaining deliberate binary header bytes.
    private static byte[] Bytes(string value) => Encoding.Latin1.GetBytes(value);

    // Combines independent header and body expectations without a mail serializer or parsed offsets.
    private static byte[] Join(byte[] first, byte[] second) => [.. first, .. second];

    private sealed record MailboxShape(string Prefix, string Suffix)
    {
        // Inserts a chosen address into the generated mailbox's independently known source formatting.
        public string Render(string address) => Prefix + address + Suffix;
    }
}
