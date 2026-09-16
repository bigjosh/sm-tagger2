using System.Security.Cryptography;
using System.Text;

namespace SmTagger.LabDkim;

internal static class DkimSelfTest
{
    private const string RawFrom = "fRoM:\t Sender <sender@sender.test> \t\r\n";
    private const string RawSubject = "SUBJECT:  Test\t subject \t\r\n\t folded \t\r\n";
    private const string RawTop = "X-Test: top\t value\r\n";
    private const string RawBottom = "X-Test: bottom  value \t\r\n";
    private const string RawTo = "To: receiver@example.net\r\n";
    private const string RawBody = "\t Hello \t world \t\r\n \t\r\n\r\n";
    private const string SimpleBody = "\t Hello \t world \t\r\n \t\r\n";
    private const string RelaxedBody = " Hello world\r\n";
    private const string HList = "from:subject:x-test:x-test:x-missing:from";
    private const string RelaxedHeaders = "from:Sender <sender@sender.test>\r\nsubject:Test subject folded\r\nx-test:bottom value\r\nx-test:top value\r\n";
    private static readonly DateTimeOffset VerificationTime = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(byte[] Message, string Key, string DnsName = "lab._domainkey.sender.test", string Domain = "sender.test");

    // Exercises fixed RFC bytes and independently assembled signing inputs without reading any private mail.
    public static int Run()
    {
        int passed = 0;
        int failed = 0;
        using RSA signer = RSA.Create(2048);
        Fixture relaxed = Sign(signer, "relaxed", "relaxed");
        Fixture simple = Sign(signer, "simple", "simple");

        // Records one bounded test without hiding failures from the command's exit status.
        void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception.Message); }
        }

        foreach (string headerAlgorithm in new[] { "simple", "relaxed" })
            foreach (string bodyAlgorithm in new[] { "simple", "relaxed" })
                Test("independent signature " + headerAlgorithm + "/" + bodyAlgorithm,
                    () => RequirePass(Verify(Sign(signer, headerAlgorithm, bodyAlgorithm))));
        Test("omitted c= defaults to simple/simple", () => RequirePass(Verify(Sign(signer, "simple", "simple", omitCanonicalization: true))));
        Test("single c=relaxed defaults body to simple", () => RequirePass(Verify(Sign(signer, "relaxed", "simple", singleCanonicalization: true))));
        Test("SPKI key", () => RequirePass(Verify(relaxed)));
        Test("PKCS#1 key", () => RequirePass(Verify(relaxed with { Key = "v=DKIM1; p=" + Convert.ToBase64String(signer.ExportRSAPublicKey()) })));
        Test("one terminal DNS dot and DNS case are allowed", () => RequirePass(Verify(relaxed with { DnsName = "LAB._domainkey.SENDER.TEST." })));
        Test("multiple terminal DNS dots fail", () => RequireFailure(Verify(relaxed with { DnsName = "lab._domainkey.sender.test.." })));

        Test("body tamper retains separate valid header result", () => RequireChecks(Verify(Change(relaxed, "world", "tampered")), body: false, header: true));
        Test("signed subject tamper retains valid body result", () => RequireChecks(Verify(Change(relaxed, "Test\t subject", "Changed\t subject")), body: true, header: false));
        Test("unsigned recipient change does not claim recipient protection", () => RequirePass(Verify(Change(relaxed, RawTo, "To: another@example.net\r\n"))));
        Test("relaxed header equivalent whitespace", () => RequirePass(Verify(Change(relaxed, RawSubject, "Subject:\tTest subject\r\n folded\r\n"))));
        Test("simple header whitespace tamper", () => RequireChecks(Verify(Change(simple, RawSubject, "Subject: Test subject folded\r\n")), body: true, header: false));
        Test("relaxed body equivalent whitespace", () => RequirePass(Verify(Change(relaxed, RawBody, "  Hello\t\tworld\t\r\n\t\r\n"))));
        Test("simple body whitespace tamper", () => RequireChecks(Verify(Change(simple, "world \t", "world\t")), body: false, header: true));
        Test("simple trailing empty lines are ignored", () => RequirePass(Verify(Append(simple, "\r\n\r\n"))));
        Test("relaxed trailing empty lines are ignored", () => RequirePass(Verify(Append(relaxed, "\r\n\r\n"))));
        Test("bottom repeated header tamper", () => RequireChecks(Verify(Change(relaxed, RawBottom, "X-Test: forged bottom\r\n")), body: true, header: false));
        Test("upper repeated header tamper", () => RequireChecks(Verify(Change(relaxed, RawTop, "X-Test: forged top\r\n")), body: true, header: false));
        Test("repeated headers swapped", () => RequireChecks(Verify(Change(relaxed, RawTop + RawBottom, RawBottom + RawTop)), body: true, header: false));
        Test("signed absent header inserted", () => RequireChecks(Verify(Change(relaxed, RawTo, "X-Missing: inserted\r\n" + RawTo)), body: true, header: false));
        Test("extra duplicate not selected by h= remains unprotected", () => RequirePass(Verify(Change(relaxed, RawTop, "X-Test: additional unsigned top\r\n" + RawTop))));
        Test("duplicate From rejected by explicit lab policy", () => RequireFailure(Verify(Change(relaxed, RawFrom, RawFrom + RawFrom))));
        Test("h= must include From", () => RequireFailure(Verify(Change(relaxed, HList, "subject:x-test:x-test"))));
        Test("unknown signed tag tamper", () => RequireChecks(Verify(Change(relaxed, "x_note=lab", "x_note=changed")), body: true, header: false));
        Test("b= signature tamper", () => RequireChecks(Verify(CorruptSignature(relaxed)), body: true, header: false));
        Test("b= internal folding whitespace is ignored for simple header", () => RequirePass(Verify(simple)));
        Test("wrong caller domain", () => RequireFailure(Verify(relaxed with { Domain = "other.test" })));
        Test("wrong selector owner", () => RequireFailure(Verify(relaxed with { DnsName = "other._domainkey.sender.test" })));
        Test("wrong key", () => { using RSA wrong = RSA.Create(2048); RequireChecks(Verify(relaxed with { Key = "p=" + Convert.ToBase64String(wrong.ExportSubjectPublicKeyInfo()) }), true, false); });
        Test("revoked key", () => RequireFailure(Verify(relaxed with { Key = "v=DKIM1; p=" })));
        Test("duplicate signature tags", () => RequireFailure(Verify(Change(relaxed, "x_note=lab", "d=sender.test"))));
        Test("duplicate key tags", () => RequireFailure(Verify(relaxed with { Key = relaxed.Key + "; p=AAAA" })));
        Test("SHA-1 excluded", () => RequireUnsupported(Verify(Change(relaxed, "a=rsa-sha256", "a=rsa-sha1"))));
        Test("l= partial body excluded", () => RequireUnsupported(Verify(Change(relaxed, "x_note=lab", "l=0"))));
        Test("key hash restriction", () => RequireFailure(Verify(relaxed with { Key = relaxed.Key + "; h=sha1" })));
        Test("key service restriction", () => RequireFailure(Verify(relaxed with { Key = relaxed.Key + "; s=other" })));
        Test("test key exposes successful cryptography without final pass", () =>
        {
            VerificationReport report = Verify(relaxed with { Key = relaxed.Key + "; t=y" });
            RequireUnsupported(report);
            Require(report.Signatures[0].BodyHashMatches == true && report.Signatures[0].HeaderSignatureMatches == true && report.Signatures[0].KeyTestingMode, "Missing testing-key detail.");
        });
        Test("subdomain identity accepted without strict key flag", () => RequirePass(Verify(Sign(signer, "relaxed", "relaxed", extraTags: "i=sender@sub.sender.test; "))));
        Test("malformed signed identity rejected", () => RequireFailure(Verify(Sign(signer, "relaxed", "relaxed", extraTags: "i=bad@@sender.test; "))));
        Test("quoted signed identity explicitly unsupported", () => RequireUnsupported(Verify(Sign(signer, "relaxed", "relaxed", extraTags: "i=\"quoted\"@sender.test; "))));
        Test("consecutive local-part dots rejected", () => RequireUnsupported(Verify(Sign(signer, "relaxed", "relaxed", extraTags: "i=bad..name@sender.test; "))));
        Test("signed empty query list entry rejected", () => RequireFailure(Verify(Sign(signer, "relaxed", "relaxed", extraTags: "q=dns/txt:; "))));
        Test("signed query extensions explicitly unsupported", () => RequireUnsupported(Verify(Sign(signer, "relaxed", "relaxed", extraTags: "q=dns/txt:other; "))));
        Test("query FWS around colon accepted", () => RequirePass(Verify(Sign(signer, "relaxed", "relaxed", extraTags: "q=dns/txt : dns/txt; "))));
        Test("signed c= slash whitespace rejected", () => RequireFailure(Verify(Sign(signer, "relaxed", "relaxed", canonicalizationValue: "relaxed / relaxed"))));
        Test("signed malformed selector rejected", () => RequireFailure(Verify(Sign(signer, "relaxed", "relaxed", selector: "lab-"))));
        Test("key hash empty entry rejected", () => RequireFailure(Verify(relaxed with { Key = relaxed.Key + "; h=sha256:" })));
        Test("key service empty entry rejected", () => RequireFailure(Verify(relaxed with { Key = relaxed.Key + "; s=email:" })));
        Test("key flag empty entry rejected", () => RequireFailure(Verify(relaxed with { Key = relaxed.Key + "; t=s:" })));
        Test("well-formed unknown key flags ignored", () => RequirePass(Verify(relaxed with { Key = relaxed.Key + "; t=future-flag:s" })));
        Test("bare LF inside key rejected", () => RequireFailure(Verify(relaxed with { Key = relaxed.Key.Insert(relaxed.Key.IndexOf("p=", StringComparison.Ordinal) + 18, "\n") })));
        Test("CRLF inside key must be followed by whitespace", () => RequireFailure(Verify(relaxed with { Key = relaxed.Key.Insert(relaxed.Key.IndexOf("p=", StringComparison.Ordinal) + 18, "\r\n") })));
        Test("proper key folding accepted", () => RequirePass(Verify(relaxed with { Key = relaxed.Key.Insert(relaxed.Key.IndexOf("p=", StringComparison.Ordinal) + 18, "\r\n\t") })));
        Test("single-label signing domain rejected", () => RequireFailure(Verify(Sign(signer, "relaxed", "relaxed", domain: "sender"))));
        Test("constructed DNS owner above 253 bytes rejected", () =>
        {
            string selector = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 55);
            RequireFailure(Verify(Sign(signer, "relaxed", "relaxed", selector: selector)));
        });
        Test("signed z= explicitly unsupported", () => RequireUnsupported(Verify(Sign(signer, "relaxed", "relaxed", extraTags: "z=Subject:test; "))));
        Test("strict key identity restriction", () =>
        {
            Fixture subdomain = Sign(signer, "relaxed", "relaxed", extraTags: "i=sender@sub.sender.test; ");
            RequireFailure(Verify(subdomain with { Key = subdomain.Key + "; t=s" }));
        });
        Test("expired valid signature", () =>
        {
            VerificationReport report = Verify(Sign(signer, "relaxed", "relaxed", extraTags: "t=100; x=200; "));
            RequireFailure(report);
            Require(report.Signatures[0].Expired && report.Signatures[0].HeaderSignatureMatches == true, "Expiration did not preserve successful cryptography.");
        });
        Test("expiration must follow timestamp", () => RequireFailure(Verify(Sign(signer, "relaxed", "relaxed", extraTags: "t=200; x=100; "))));
        Test("1024-bit verification retained with recommendation", () =>
        {
            using RSA small = RSA.Create(1024);
            VerificationReport report = Verify(Sign(small, "relaxed", "relaxed"));
            RequirePass(report);
            Require(report.Signatures[0].RsaBits == 1024 && report.Signatures[0].Reason.Contains("2048", StringComparison.Ordinal), "Missing key-size recommendation.");
        });
        Test("512-bit key forbidden", () => { using RSA weak = RSA.Create(512); RequireFailure(Verify(Sign(weak, "relaxed", "relaxed"))); });
        Test("trailing RSA DER rejected", () => RequireFailure(Verify(relaxed with { Key = "p=" + Convert.ToBase64String([.. signer.ExportSubjectPublicKeyInfo(), 0]) })));
        Test("bare LF rejected", () => RequireFailure(Verify(Change(relaxed, RawTo, "To: receiver@example.net\n"))));
        Test("bare CR rejected", () => RequireFailure(Verify(Append(relaxed, "\r"))));
        Test("empty simple body is CRLF", () => RequireBody("", "simple", "\r\n"));
        Test("empty relaxed body is empty", () => RequireBody("", "relaxed", ""));
        Test("blank simple body collapses to CRLF", () => RequireBody("\r\n\r\n", "simple", "\r\n"));
        Test("whitespace-only simple line preserved", () => RequireBody(" \t\r\n\r\n", "simple", " \t\r\n"));
        Test("whitespace-only relaxed body is empty", () => RequireBody(" \t\r\n\r\n", "relaxed", ""));
        Test("missing terminal CRLF supplied", () => RequireBody("body", "simple", "body\r\n"));
        Test("relaxed opaque octets preserved", () => RequireBody("\u0080\u00ff \t x \t\r\n", "relaxed", "\u0080\u00ff x\r\n"));
        Test("nonbreaking space is not DKIM whitespace", () => RequireBody("x\u00a0 \t\r\n", "relaxed", "x\u00a0\r\n"));
        Test("CRLF-heavy body uses bounded line scanning", () =>
        {
            string body = string.Create(4 * 1024 * 1024, 0, (span, _) =>
            {
                for (int i = 0; i < span.Length; i += 2) { span[i] = '\r'; span[i + 1] = '\n'; }
            });
            RequireBody(body, "relaxed", "");
        });
        Test("published RFC 8463 RSA fixture", VerifyRfcFixture);

        Console.WriteLine($"Synthetic/offline DKIM checks: {passed} passed, {failed} failed. These checks do not verify a live SmarterMail or deployment gate.");
        return failed == 0 ? 0 : 1;
    }

    // Signs hand-written canonical bytes, independently of the verifier's canonicalization implementation.
    private static Fixture Sign(RSA signer, string headerAlgorithm, string bodyAlgorithm, bool omitCanonicalization = false, bool singleCanonicalization = false, string extraTags = "", string? canonicalizationValue = null, string selector = "lab", string domain = "sender.test")
    {
        string canonicalBody = bodyAlgorithm == "simple" ? SimpleBody : RelaxedBody;
        string bodyHash = Convert.ToBase64String(SHA256.HashData(Encoding.Latin1.GetBytes(canonicalBody)));
        string c = omitCanonicalization ? "" : "c=" + (canonicalizationValue ?? headerAlgorithm + (singleCanonicalization ? "" : "/" + bodyAlgorithm)) + "; ";
        string firstLine = "v=1; a=rsa-sha256; " + c + "d=" + domain + "; s=" + selector + ";";
        string secondLine = "h=" + HList + "; bh=" + bodyHash + "; " + extraTags + "b=; x_note=lab";
        string blankSignature = "DKIM-Signature: " + firstLine + "\r\n " + secondLine + "\r\n";
        string signedHeaders = headerAlgorithm == "simple" ? RawFrom + RawSubject + RawBottom + RawTop : RelaxedHeaders;
        string signedDkim = headerAlgorithm == "simple" ? blankSignature[..^2] : "dkim-signature:" + firstLine + " " + secondLine;
        string b = Convert.ToBase64String(signer.SignData(Encoding.Latin1.GetBytes(signedHeaders + signedDkim), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        string foldedB = " \t" + b[..32] + "\r\n\t" + b[32..] + " \t";
        string complete = blankSignature.Replace("b=;", "b=" + foldedB + ";", StringComparison.Ordinal)
            + RawFrom + RawSubject + RawTop + RawBottom + RawTo + "\r\n" + RawBody;
        return new Fixture(Encoding.Latin1.GetBytes(complete), "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()), selector + "._domainkey." + domain, domain);
    }

    // Verifies a synthetic fixture at a fixed time for repeatable expiration checks.
    private static VerificationReport Verify(Fixture fixture) => DkimVerifier.Verify(fixture.Message, fixture.Key, fixture.DnsName, fixture.Domain, VerificationTime);

    // Changes exact synthetic bytes and fails if a test accidentally targets absent content.
    private static Fixture Change(Fixture fixture, string before, string after)
    {
        string text = Encoding.Latin1.GetString(fixture.Message);
        Require(text.Contains(before, StringComparison.Ordinal), "Test mutation target missing.");
        return fixture with { Message = Encoding.Latin1.GetBytes(text.Replace(before, after, StringComparison.Ordinal)) };
    }

    // Appends explicit synthetic bytes to exercise body canonicalization boundaries.
    private static Fixture Append(Fixture fixture, string value) => fixture with { Message = [.. fixture.Message, .. Encoding.Latin1.GetBytes(value)] };

    // Replaces the first base64 signature digit while leaving its decoded length unchanged.
    private static Fixture CorruptSignature(Fixture fixture)
    {
        string text = Encoding.Latin1.GetString(fixture.Message);
        int start = text.IndexOf("b= \t", StringComparison.Ordinal) + 4;
        Require(start >= 4, "Test b= target missing.");
        char replacement = text[start] == 'A' ? 'B' : 'A';
        return fixture with { Message = Encoding.Latin1.GetBytes(text[..start] + replacement + text[(start + 1)..]) };
    }

    // Requires a final pass with both cryptographic checks visible in the report.
    private static void RequirePass(VerificationReport report)
    {
        Require(report.Verified, report.MessageError ?? string.Join("; ", report.Signatures.Select(result => result.Reason)));
        Require(report.Signatures.Any(result => result.Status == "Pass" && result.BodyHashMatches == true && result.HeaderSignatureMatches == true), "Pass lacked cryptographic checks.");
    }

    // Requires a rejected or unsupported fixture without assuming the stage of rejection.
    private static void RequireFailure(VerificationReport report) => Require(!report.Verified, "Unexpected verification pass.");

    // Requires an explicit limitation instead of claiming unsupported data failed cryptography.
    private static void RequireUnsupported(VerificationReport report)
    {
        RequireFailure(report);
        Require(report.Signatures.Length == 1 && report.Signatures[0].Status == "Unsupported", "Expected Unsupported status.");
    }

    // Distinguishes a body-hash failure from a header-signature failure on synthetic tampering.
    private static void RequireChecks(VerificationReport report, bool body, bool header)
    {
        RequireFailure(report);
        Require(report.Signatures.Length == 1 && report.Signatures[0].BodyHashMatches == body && report.Signatures[0].HeaderSignatureMatches == header,
            "Unexpected cryptographic stage results: " + string.Join("; ", report.Signatures.Select(result => result.Reason)));
    }

    // Compares boundary cases to explicit expected bytes rather than another canonicalizer.
    private static void RequireBody(string raw, string algorithm, string expected) => Require(
        DkimVerifier.CanonicalizeBody(raw, algorithm).AsSpan().SequenceEqual(Encoding.Latin1.GetBytes(expected)), "Canonical body differs from expected octets.");

    // Uses an externally published RSA signature as an independent end-to-end interoperability check.
    private static void VerifyRfcFixture()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        byte[] message = File.ReadAllBytes(Path.Combine(directory, "rfc8463.eml"));
        string key = File.ReadAllText(Path.Combine(directory, "rfc8463-key.txt"), Encoding.UTF8);
        var fixture = new Fixture(message, key, "test._domainkey.football.example.com", "football.example.com");
        VerificationReport report = Verify(fixture);
        RequirePass(report);
        Require(report.Signatures.Count(result => result.Status == "Pass") == 1, "Expected exactly the published RSA signature to pass.");
        Require(report.Signatures.Any(result => result.Status == "Unsupported"), "The independent Ed25519 signature must remain explicitly unsupported.");
        VerificationReport changed = Verify(Change(fixture, "We lost the game.", "We won the game."));
        RequireFailure(changed);
        Require(changed.Signatures.Any(result => result.BodyHashMatches == false && result.HeaderSignatureMatches == true), "RFC body tamper did not isolate the body-hash failure.");
    }

    // Raises an ordinary test failure with a focused reason.
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
