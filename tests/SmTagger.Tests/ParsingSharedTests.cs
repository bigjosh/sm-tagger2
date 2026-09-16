using System.Text;
using SmTagger.Mail;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ParsingSharedTests
{
    // Exercises every allowed atom punctuation without introducing a transport length gate.
    [Theory]
    [InlineData("User.Name@EXAMPLE.COM", "user.name@example.com")]
    [InlineData("!#$%&'*+-/=?^_`{|}~@xn--bcher-kva.example", "!#$%&'*+-/=?^_`{|}~@xn--bcher-kva.example")]
    [InlineData("a@localhost", "a@localhost")]
    public void CanonicalizationPreservesAllowedAsciiIdentity(string original, string canonical)
    {
        Assert.Equal(canonical, AddressSyntax.Canonicalize(original));
        Assert.True(AddressSyntax.TryCanonicalize(original, out string actual));
        Assert.Equal(canonical, actual);
        Assert.Equal(new string('a', 300) + "@example.com", AddressSyntax.Canonicalize(new string('a', 300) + "@example.com"));
    }

    // Rejects syntax that could otherwise create ambiguous persisted or sender identities.
    [Theory]
    [InlineData("")]
    [InlineData("<>")]
    [InlineData(".a@example.com")]
    [InlineData("a..b@example.com")]
    [InlineData("a.@example.com")]
    [InlineData("a@-example.com")]
    [InlineData("a@example-.com")]
    [InlineData("a@example..com")]
    [InlineData("a@example.com.")]
    [InlineData("a@@example.com")]
    [InlineData("a b@example.com")]
    [InlineData("a@éxample.com")]
    [InlineData("\"a\"@example.com")]
    [InlineData("a@[127.0.0.1]")]
    public void InvalidAddressSyntaxIsRejected(string value)
    {
        Assert.False(AddressSyntax.TryCanonicalize(value, out _));
        Assert.Throws<MailContractException>(() => AddressSyntax.Canonicalize(value));
    }

    // Covers literal Windows containment rules separately from email-address syntax.
    [Theory]
    [InlineData("-mail", true)]
    [InlineData("joe@example.com", true)]
    [InlineData("x.eml", true)]
    [InlineData("con.txt", false)]
    [InlineData("COM1", false)]
    [InlineData("LPT².log", false)]
    [InlineData("COM0", true)]
    [InlineData("x/y", false)]
    [InlineData("x\\y", false)]
    [InlineData("x:stream", false)]
    [InlineData("x.", false)]
    [InlineData("x ", false)]
    [InlineData("..", false)]
    public void LiteralWindowsComponentRulesAreStable(string value, bool expected) => Assert.Equal(expected, WindowsNames.IsUsableComponent(value));

    // Ensures a later auth or malformed metadata line cannot hide behind an earlier decision.
    [Fact]
    public void FastClassifierExaminesTheCompleteHdr()
    {
        string prefix = "Written \r\nnot parsed\r\nnot parsed either\r\n";
        Assert.Equal(FastHdrKind.NoAuth, FastHdrClassifier.Classify(Bytes(prefix + "opaque: \u00ff\r\n\r\n")).Kind);
        FastHdrResult valid = FastHdrClassifier.Classify(Bytes(prefix + "other: x\r\nAuTh:\tUSER@Example.Com \r\n\r\n"));
        Assert.Equal(FastHdrKind.ValidAuth, valid.Kind);
        Assert.Equal("user@example.com", valid.CanonicalAuth);
        foreach (string suffix in new[] { "auth: a@example.com\r\nAUTH: a@example.com\r\n\r\n", "auth: <>\r\n\r\n", "auth: \r\n\r\n", "auth : a@example.com\r\n\r\n", "bad\r\n\r\n", "\r\nextra\r\n", "auth: a@example.com\n\n", "auth: a@example.com\r\n" })
            Assert.Equal(FastHdrKind.Unsafe, FastHdrClassifier.Classify(Bytes(prefix + suffix)).Kind);
    }

    // Establishes exact reversible identities, independent of source recipient ordering and spelling.
    [Fact]
    public void RecipientIdentityIsSortedReversibleAndStrictOnLoad()
    {
        string encoded = RecipientIdentity.Encode(new[] { "b@example.net", "a%tag@example.net", "b@example.net" });
        Assert.Equal("a%%tag@example.net;b@example.net;", encoded);
        Assert.Equal(new[] { "a%tag@example.net", "b@example.net" }, RecipientIdentity.Decode(encoded + "\r\n"));
        foreach (string invalid in new[] { "", "a@example.net", ";", "a%q@example.net;", "b@example.net;a@example.net;", "a@example.net;a@example.net;", "A@example.net;", "a%;b@example.net;" })
            Assert.Throws<MailContractException>(() => RecipientIdentity.Decode(invalid));
    }

    // Synthesizes one-byte fixtures so invalid non-ASCII input remains visible to boundary validation.
    private static byte[] Bytes(string value) => Encoding.Latin1.GetBytes(value);
}
