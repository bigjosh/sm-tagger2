using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SmTagger.LabDkim;

internal sealed class SignatureResult
{
    public int HeaderIndex { get; init; }
    public string Status { get; set; } = "Fail";
    public string Reason { get; set; } = "";
    public string? Domain { get; set; }
    public string? Selector { get; set; }
    public string? HeaderCanonicalization { get; set; }
    public string? BodyCanonicalization { get; set; }
    public string[] SignedHeaders { get; set; } = [];
    public bool? BodyHashMatches { get; set; }
    public bool? HeaderSignatureMatches { get; set; }
    public int? RsaBits { get; set; }
    public int? CanonicalBodyBytes { get; set; }
    public bool KeyTestingMode { get; set; }
    public bool Expired { get; set; }
}

internal sealed record VerificationReport(bool Verified, string Scope, string? InputSha256, string? SuppliedKeyRecordSha256, string ExpectedDomain,
    string SuppliedDnsName, DateTimeOffset CheckedUtc, string? MessageError, SignatureResult[] Signatures);

internal sealed record HeaderField(string Name, string Raw, int Colon);
internal sealed record Tag(string Name, string Value, int ValueStart, int ValueEnd);
internal sealed record BodyDigest(int Length, byte[] Hash);

internal static class DkimVerifier
{
    public const int MaximumMessageBytes = 32 * 1024 * 1024;
    public const int MaximumKeyRecordBytes = 32768;
    private const string Scope = "Offline full-body RSA-SHA256 verification against caller-supplied DNS TXT evidence; not live DNS, SPF, DMARC, or deployment evidence.";

    // Distinguishes malformed or unsupported DKIM data from a programming failure.
    private sealed class Rejection(string message, bool unsupported = false) : Exception(message)
    {
        public bool Unsupported { get; } = unsupported;
    }

    // Verifies every bounded DKIM header against the explicitly named supplied key and expected signing domain.
    public static VerificationReport Verify(byte[] message, string keyRecord, string dnsName, string expectedDomain, DateTimeOffset now)
    {
        string? digest = null;
        string? keyDigest = null;
        var results = new List<SignatureResult>();
        string? error = null;
        try
        {
            if (message.Length > MaximumMessageBytes) throw new Rejection("Message exceeds 32 MiB.", true);
            if (Encoding.UTF8.GetByteCount(keyRecord) > MaximumKeyRecordBytes) throw new Rejection("DNS TXT record exceeds 32 KiB.", true);
            digest = Convert.ToHexStringLower(SHA256.HashData(message));
            keyDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyRecord)));
            RequireDomain(expectedDomain);
            string text = Encoding.Latin1.GetString(message);
            RequireCrLf(text);
            int separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator < 0 || separator > 256 * 1024) throw new Rejection("Missing header/body separator or headers exceed 256 KiB.");
            List<HeaderField> headers = ParseHeaders(text[..(separator + 2)]);
            string body = text[(separator + 4)..];
            int fromCount = headers.Count(header => header.Name == "from");
            if (fromCount != 1) throw new Rejection("The lab requires exactly one From header; it does not parse its mailbox syntax.", true);
            int[] signatureIndices = headers.Select((header, index) => (header, index)).Where(item => item.header.Name == "dkim-signature").Select(item => item.index).ToArray();
            if (signatureIndices.Length is 0 or > 16) throw new Rejection("Expected between one and sixteen DKIM signatures.", true);
            var bodyDigests = new Dictionary<string, BodyDigest>(StringComparer.Ordinal);
            foreach (int index in signatureIndices)
                results.Add(VerifySignature(headers, body, index, keyRecord, dnsName, expectedDomain, now, bodyDigests));
        }
        catch (Rejection exception) { error = exception.Message; }
        return new VerificationReport(error is null && results.Any(result => result.Status == "Pass"), Scope, digest, keyDigest,
            expectedDomain, dnsName, now, error, results.ToArray());
    }

    // Checks DKIM metadata, hashes canonical bytes, and verifies RSA PKCS#1 v1.5 with the bound DNS key.
    private static SignatureResult VerifySignature(List<HeaderField> headers, string body, int index, string keyRecord, string dnsName, string expectedDomain, DateTimeOffset now, Dictionary<string, BodyDigest> bodyDigests)
    {
        var result = new SignatureResult { HeaderIndex = index };
        try
        {
            HeaderField signature = headers[index];
            string value = signature.Raw[(signature.Colon + 1)..^2];
            List<Tag> tags = ParseTags(value);
            Dictionary<string, Tag> map = tags.ToDictionary(tag => tag.Name, StringComparer.Ordinal);
            foreach (string required in new[] { "v", "a", "b", "bh", "d", "h", "s" })
                if (!map.ContainsKey(required)) throw new Rejection("Missing required DKIM tag: " + required);
            if (tags[0].Name != "v" || map["v"].Value != "1") throw new Rejection("DKIM v=1 must be the first tag.");
            if (map["a"].Value != "rsa-sha256") throw new Rejection("Only rsa-sha256 signatures are supported; rsa-sha1 is forbidden.", true);
            if (map.ContainsKey("l")) throw new Rejection("Partial-body l= signatures are excluded from whole-message lab evidence.", true);
            if (map.ContainsKey("z")) throw new Rejection("Copied-header z= diagnostics are outside the bounded lab grammar.", true);
            if (map.TryGetValue("q", out Tag? query))
            {
                string[] methods = query.Value.Split(':').Select(TrimFws).ToArray();
                if (methods.Any(method => method.Length == 0)) throw new Rejection("Malformed q= query list.");
                if (methods.Any(method => method != "dns/txt")) throw new Rejection("Only dns/txt query-list entries are supported.", true);
            }
            result.Domain = map["d"].Value;
            result.Selector = map["s"].Value;
            RequireDomain(result.Domain);
            RequireSelector(result.Selector);
            if (!result.Domain.Equals(expectedDomain, StringComparison.OrdinalIgnoreCase)) throw new Rejection("The signature domain differs from the caller's expected signing domain.");
            string expectedName = result.Selector + "._domainkey." + result.Domain;
            if (expectedName.Length > 253) throw new Rejection("The complete selector DNS name exceeds 253 ASCII characters.", true);
            string normalizedDnsName = dnsName.EndsWith('.') ? dnsName[..^1] : dnsName;
            if (!normalizedDnsName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)) throw new Rejection("The supplied DNS owner name does not match this selector and signing domain.");
            string identityDomain = result.Domain;
            if (map.TryGetValue("i", out Tag? identity))
            {
                int at = identity.Value.LastIndexOf('@');
                if (at < 0 || identity.Value.IndexOf('@') != at) throw new Rejection("Malformed i= identity.");
                RequireIdentityLocalPart(identity.Value[..at]);
                identityDomain = identity.Value[(at + 1)..];
                RequireDomain(identityDomain);
                if (!identityDomain.Equals(result.Domain, StringComparison.OrdinalIgnoreCase) && !identityDomain.EndsWith("." + result.Domain, StringComparison.OrdinalIgnoreCase))
                    throw new Rejection("The i= identity domain is outside the signing domain.");
            }
            ulong? timestamp = GetTimeTag(map, "t");
            ulong? expiry = GetTimeTag(map, "x");
            if (timestamp is not null && expiry is not null && expiry <= timestamp) throw new Rejection("DKIM expiration must be later than signing time.");
            result.Expired = expiry is not null && now.ToUnixTimeSeconds() >= 0 && (ulong)now.ToUnixTimeSeconds() > expiry;
            string[] canonicalization = (map.TryGetValue("c", out Tag? canon) ? canon.Value : "simple/simple").Split('/');
            if (canonicalization.Length is < 1 or > 2) throw new Rejection("Malformed canonicalization pair.");
            if (canonicalization.Any(item => !IsHyphenatedWord(item))) throw new Rejection("Malformed canonicalization token.");
            result.HeaderCanonicalization = canonicalization[0];
            result.BodyCanonicalization = canonicalization.Length == 2 ? canonicalization[1] : "simple";
            if (result.HeaderCanonicalization is not ("simple" or "relaxed") || result.BodyCanonicalization is not ("simple" or "relaxed"))
                throw new Rejection("Only simple and relaxed canonicalization are supported.", true);
            result.SignedHeaders = map["h"].Value.Split(':').Select(name => TrimFws(name).ToLowerInvariant()).ToArray();
            if (result.SignedHeaders.Length > 2048 || result.SignedHeaders.Any(name => !IsHeaderName(name))) throw new Rejection("Malformed or excessive h= header list.");
            if (!result.SignedHeaders.Contains("from", StringComparer.Ordinal)) throw new Rejection("The From header must be signed.");
            BodyDigest bodyDigest = GetBodyDigest(body, result.BodyCanonicalization, bodyDigests);
            result.CanonicalBodyBytes = bodyDigest.Length;
            byte[] claimedBodyHash = DecodeBase64(map["bh"].Value);
            if (claimedBodyHash.Length != 32) throw new Rejection("bh= must contain a SHA-256 digest.");
            result.BodyHashMatches = CryptographicOperations.FixedTimeEquals(bodyDigest.Hash, claimedBodyHash);
            string keyText = keyRecord.Trim('\uFEFF', ' ', '\t', '\r', '\n');
            RequireFolding(keyText);
            List<Tag> keyTags = ParseTags(keyText);
            Dictionary<string, Tag> key = keyTags.ToDictionary(tag => tag.Name, StringComparer.Ordinal);
            if (key.TryGetValue("v", out Tag? keyVersion) && (keyTags[0].Name != "v" || keyVersion.Value != "DKIM1")) throw new Rejection("Unsupported or misplaced key v= tag.");
            if (key.TryGetValue("k", out Tag? keyType) && keyType.Value != "rsa") throw new Rejection("Only RSA DNS public keys are supported.", true);
            if (key.TryGetValue("h", out Tag? hashes) && !ParseKeyTokens(hashes.Value, false).Contains("sha256", StringComparer.Ordinal)) throw new Rejection("DNS key does not authorize SHA-256.");
            if (key.TryGetValue("s", out Tag? service) && !ParseKeyTokens(service.Value, true).Any(item => item is "*" or "email")) throw new Rejection("DNS key does not authorize email signing.");
            string[] flags = key.TryGetValue("t", out Tag? keyFlags) ? ParseKeyTokens(keyFlags.Value, false) : [];
            result.KeyTestingMode = flags.Contains("y", StringComparer.Ordinal);
            if (flags.Contains("s", StringComparer.Ordinal) && !identityDomain.Equals(result.Domain, StringComparison.OrdinalIgnoreCase)) throw new Rejection("DNS key t=s forbids a subdomain i= identity.");
            if (!key.TryGetValue("p", out Tag? publicKey) || RemoveFws(publicKey.Value).Length == 0) throw new Rejection("DNS key is missing or revoked by an empty p= value.");
            using RSA rsa = ImportRsa(DecodeBase64(publicKey.Value));
            result.RsaBits = rsa.KeySize;
            if (rsa.KeySize < 1024) throw new Rejection("RSA keys below 1024 bits are forbidden.");
            if (rsa.KeySize > 4096) throw new Rejection("This bounded verifier supports RSA keys through 4096 bits.", true);
            using var input = new MemoryStream();
            var consumed = new HashSet<int>();
            foreach (string name in result.SignedHeaders)
            {
                for (int candidate = headers.Count - 1; candidate >= 0; candidate--)
                {
                    if (candidate == index || consumed.Contains(candidate) || headers[candidate].Name != name) continue;
                    consumed.Add(candidate);
                    input.Write(CanonicalizeHeader(headers[candidate], result.HeaderCanonicalization));
                    break;
                }
            }
            Tag b = map["b"];
            string blankValue = value[..b.ValueStart] + value[b.ValueEnd..];
            string blankRaw = signature.Raw[..(signature.Colon + 1)] + blankValue + "\r\n";
            byte[] canonicalSignature = CanonicalizeHeader(new HeaderField(signature.Name, blankRaw, signature.Colon), result.HeaderCanonicalization);
            input.Write(canonicalSignature.AsSpan(0, canonicalSignature.Length - 2));
            byte[] signatureBytes = DecodeBase64(map["b"].Value);
            result.HeaderSignatureMatches = rsa.VerifyData(input.ToArray(), signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (result.BodyHashMatches != true) throw new Rejection("Body hash mismatch.");
            if (result.HeaderSignatureMatches != true) throw new Rejection("RSA header signature mismatch.");
            if (result.Expired) throw new Rejection("The cryptographically valid signature is expired at the supplied verification time.");
            if (result.KeyTestingMode) throw new Rejection("Cryptography verified, but a DNS t=y testing key is not accepted as final lab evidence.", true);
            result.Status = "Pass";
            result.Reason = rsa.KeySize < 2048 ? "Whole-body cryptography verified; the 1024-bit key is below the recommended 2048-bit signing size." : "Whole-body RSA-SHA256 cryptography verified against the supplied DNS key.";
        }
        catch (Rejection exception) { result.Status = exception.Unsupported ? "Unsupported" : "Fail"; result.Reason = exception.Message; }
        catch (CryptographicException) { result.Reason = "Malformed RSA key or signature encoding."; }
        return result;
    }

    // Preserves complete physical header bytes while grouping valid continuation lines.
    private static List<HeaderField> ParseHeaders(string block)
    {
        var headers = new List<HeaderField>();
        int offset = 0;
        while (offset < block.Length)
        {
            int lineEnd = block.IndexOf("\r\n", offset, StringComparison.Ordinal);
            if (lineEnd < 0 || lineEnd == offset) throw new Rejection("Malformed header line.");
            string physical = block[offset..(lineEnd + 2)];
            if (physical.IndexOf('\0') >= 0) throw new Rejection("NUL is not admitted in a header.");
            if (physical[0] is ' ' or '\t')
            {
                if (headers.Count == 0) throw new Rejection("A header continuation lacks a preceding field.");
                HeaderField prior = headers[^1];
                if (prior.Raw.Length + physical.Length > 65536) throw new Rejection("A header exceeds 64 KiB.", true);
                headers[^1] = prior with { Raw = prior.Raw + physical };
            }
            else
            {
                int colon = physical.IndexOf(':');
                if (colon <= 0 || physical.Length > 65536) throw new Rejection("Malformed or oversized header.");
                string name = physical[..colon].TrimEnd(' ', '\t').ToLowerInvariant();
                if (!IsHeaderName(name)) throw new Rejection("Malformed header name.");
                headers.Add(new HeaderField(name, physical, colon));
                if (headers.Count > 2048) throw new Rejection("More than 2048 headers.", true);
            }
            offset = lineEnd + 2;
        }
        return headers;
    }

    // Computes each supported body canonicalization at most once per message and retains only its digest.
    private static BodyDigest GetBodyDigest(string body, string algorithm, Dictionary<string, BodyDigest> cache)
    {
        if (cache.TryGetValue(algorithm, out BodyDigest? existing)) return existing;
        byte[] canonical = CanonicalizeBody(body, algorithm);
        var digest = new BodyDigest(canonical.Length, SHA256.HashData(canonical));
        cache.Add(algorithm, digest);
        return digest;
    }

    // Applies the RFC body canonicalization rules directly to preserved octets represented as Latin-1.
    internal static byte[] CanonicalizeBody(string body, string algorithm)
    {
        using var canonical = new MemoryStream(checked(body.Length + 2));
        int start = 0;
        long nonemptyEnd = 0;
        while (start < body.Length)
        {
            int end = body.IndexOf("\r\n", start, StringComparison.Ordinal);
            if (end < 0) end = body.Length;
            ReadOnlySpan<char> line = body.AsSpan(start, end - start);
            if (algorithm == "relaxed") line = line.TrimEnd(" \t".AsSpan());
            bool previousWsp = false;
            foreach (char character in line)
            {
                bool wsp = character is ' ' or '\t';
                if (algorithm != "relaxed" || !wsp || !previousWsp) canonical.WriteByte((byte)(algorithm == "relaxed" && wsp ? ' ' : character));
                previousWsp = wsp;
            }
            canonical.WriteByte(13);
            canonical.WriteByte(10);
            if (!line.IsEmpty) nonemptyEnd = canonical.Length;
            start = end + 2;
        }
        if (nonemptyEnd == 0) return algorithm == "simple" ? "\r\n"u8.ToArray() : [];
        canonical.SetLength(nonemptyEnd);
        return canonical.ToArray();
    }

    // Canonicalizes one header, retaining its final CRLF for ordinary h= inputs.
    private static byte[] CanonicalizeHeader(HeaderField header, string algorithm)
    {
        if (algorithm == "simple") return Encoding.Latin1.GetBytes(header.Raw);
        string value = header.Raw[(header.Colon + 1)..^2].Replace("\r\n", "", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(header.Name + ":" + CollapseWsp(value).Trim(' ', '\t') + "\r\n");
    }

    // Parses case-sensitive tag names and retains exact raw value spans needed to erase only b= data.
    private static List<Tag> ParseTags(string value)
    {
        if (value.Any(character => character > 126 || (character < 32 && character is not ('\t' or '\r' or '\n')))) throw new Rejection("Non-ASCII or control data in a DKIM tag list.");
        var tags = new List<Tag>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int start = 0;
        while (start < value.Length)
        {
            int end = value.IndexOf(';', start);
            if (end < 0) end = value.Length;
            string segment = value[start..end];
            if (TrimFws(segment).Length == 0)
            {
                if (end == value.Length && tags.Count > 0) break;
                throw new Rejection("Empty DKIM tag.");
            }
            int equals = segment.IndexOf('=');
            if (equals < 1) throw new Rejection("Malformed DKIM tag.");
            string name = TrimFws(segment[..equals]);
            if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') || !names.Add(name))
                throw new Rejection("Malformed or duplicate DKIM tag.");
            tags.Add(new Tag(name, TrimFws(segment[(equals + 1)..]), start + equals + 1, end));
            start = end + 1;
        }
        if (tags.Count == 0) throw new Rejection("Empty DKIM tag list.");
        return tags;
    }

    // Accepts both deployed RSA DER forms while rejecting trailing data or non-RSA SubjectPublicKeyInfo.
    private static RSA ImportRsa(byte[] encoded)
    {
        RSA rsa = RSA.Create();
        try
        {
            int consumed;
            try { rsa.ImportSubjectPublicKeyInfo(encoded, out consumed); }
            catch (CryptographicException) { rsa.ImportRSAPublicKey(encoded, out consumed); }
            if (consumed != encoded.Length) throw new Rejection("Trailing bytes in the RSA public key.");
            return rsa;
        }
        catch { rsa.Dispose(); throw; }
    }

    // Decodes DKIM base64 while allowing only the protocol's folding whitespace.
    private static byte[] DecodeBase64(string value)
    {
        try
        {
            string compact = RemoveFws(value);
            if (compact.Length == 0 || compact.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('+' or '/' or '='))) throw new Rejection("Malformed DKIM base64.");
            return Convert.FromBase64String(compact);
        }
        catch (FormatException) { throw new Rejection("Malformed DKIM base64."); }
    }

    // Requires ASCII LDH domain labels, excluding internationalized U-label and trailing-dot signature syntax.
    private static void RequireDomain(string value)
    {
        if (value.Length is 0 or > 253 || !value.Contains('.') || value.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
            throw new Rejection("Unsupported or malformed ASCII signing domain.", true);
    }

    // Requires bounded RFC ASCII selector labels without silently accepting nonstandard underscores.
    private static void RequireSelector(string value)
    {
        if (value.Length is 0 or > 253 || value.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
            throw new Rejection("Unsupported or malformed ASCII selector.", true);
    }

    // Accepts empty or unquoted ASCII dot-atom identities, excluding quoted and DKIM-QP local parts.
    private static void RequireIdentityLocalPart(string value)
    {
        const string punctuation = "!#$%&'*+-/?^_`{|}~";
        if (value.Length == 0) return;
        if (value.Length > 64 || value.Split('.').Any(atom => atom.Length == 0 || atom.Any(character => !char.IsAsciiLetterOrDigit(character) && !punctuation.Contains(character))))
            throw new Rejection("Only empty or unquoted ASCII dot-atom i= local parts without DKIM-QP encoding are supported.", true);
    }

    // Validates extension-capable key lists before interpreting recognized algorithms, services, or flags.
    private static string[] ParseKeyTokens(string value, bool allowStar)
    {
        string[] tokens = value.Split(':').Select(TrimFws).ToArray();
        if (tokens.Any(token => !(allowStar && token == "*") && !IsHyphenatedWord(token))) throw new Rejection("Malformed DNS key token list.");
        return tokens;
    }

    // Implements the RFC hyphenated-word grammar for canonicalization and key extension tokens.
    private static bool IsHyphenatedWord(string value) => value.Length > 0 && char.IsAsciiLetter(value[0]) && char.IsAsciiLetterOrDigit(value[^1])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    // Rejects bare newline octets instead of silently changing the captured transport representation.
    private static void RequireCrLf(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\r' && (i + 1 == value.Length || value[i + 1] != '\n')) throw new Rejection("Bare CR is outside the lab capture grammar.", true);
            if (value[i] == '\n' && (i == 0 || value[i - 1] != '\r')) throw new Rejection("Bare LF is outside the lab capture grammar.", true);
        }
    }

    // Permits key-record CRLF only as folding whitespace followed by SP or HTAB.
    private static void RequireFolding(string value)
    {
        RequireCrLf(value);
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '\n' && (i + 1 == value.Length || value[i + 1] is not (' ' or '\t'))) throw new Rejection("Malformed folding whitespace in the DNS key record.");
    }

    // Checks the ASCII field-name grammar used by both parsed headers and h= entries.
    private static bool IsHeaderName(string value) => value.Length > 0 && value.All(character => character is >= '!' and <= '~' && character != ':');

    // Collapses consecutive SP and HTAB without interpreting any other message octet.
    private static string CollapseWsp(string value)
    {
        var result = new StringBuilder(value.Length);
        bool inWhitespace = false;
        foreach (char character in value)
        {
            if (character is ' ' or '\t')
            {
                if (!inWhitespace) result.Append(' ');
                inWhitespace = true;
            }
            else { result.Append(character); inWhitespace = false; }
        }
        return result.ToString();
    }

    // Removes only DKIM folding whitespace from base64 values.
    private static string RemoveFws(string value) => string.Concat(value.Where(character => character is not (' ' or '\t' or '\r' or '\n')));

    // Trims only DKIM folding whitespace around a parsed tag value.
    private static string TrimFws(string value) => value.Trim(' ', '\t', '\r', '\n');

    // Parses optional unsigned decimal DKIM timestamps without accepting signs or overflow.
    private static ulong? GetTimeTag(Dictionary<string, Tag> tags, string name)
    {
        if (!tags.TryGetValue(name, out Tag? tag)) return null;
        if (tag.Value.Length == 0 || tag.Value.Any(character => !char.IsAsciiDigit(character)) || !ulong.TryParse(tag.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
            throw new Rejection("Malformed DKIM timestamp: " + name);
        return value;
    }
}
