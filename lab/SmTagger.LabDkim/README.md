# Offline DKIM verifier for the synthetic lab

This separate, dependency-free `net10.0` console verifies full-body RSA-SHA256
DKIM against a caller-supplied DNS TXT public key. It does not reference the
production projects, add a production dependency, query a network, or modify
messages. Use synthetic captures only. Private `samples/` are never test inputs.

Build and check from the repository root with the approved SDK:

```powershell
$labDotnet = 'dotnet'
& $labDotnet build lab/SmTagger.LabDkim/SmTagger.LabDkim.csproj -c Release
& $labDotnet run --project lab/SmTagger.LabDkim/SmTagger.LabDkim.csproj -c Release --no-build -- --self-test
```

An immediately runnable verification example uses the published RFC fixture:

```powershell
& $labDotnet lab/SmTagger.LabDkim/bin/Release/net10.0/sm-tagger-lab-dkim.dll --message lab/SmTagger.LabDkim/Fixtures/rfc8463.eml --key-file lab/SmTagger.LabDkim/Fixtures/rfc8463-key.txt --dns-name test._domainkey.football.example.com --expected-domain football.example.com
```

For a captured lab message, replace those four input values. The key file must
contain the UTF-8 text of **one** TXT resource record, with its character-string
segments concatenated in their original order. Remove DNS presentation quotes,
parentheses, owner name, TTL and `IN TXT`; never concatenate separate resource
records. Record the DNS query time, server and response separately. The tool
requires the exact selector owner name and expected signing domain to bind
that supplied evidence. DNS name comparison ignores case and allows one final
dot on `--dns-name` only. It cannot authenticate supplied DNS evidence.

The `.eml` must contain the received RFC message bytes, excluding SMTP commands,
dot-stuffing and the SMTP terminator. Preserve CRLF, header folding, MIME transfer
encoding and body bytes; do not export a decoded body or normalize the message.
The file is opened for a bounded read that denies concurrent writes on Windows.

The normal command emits JSON. Exit `0` means at least one signature for the
specified domain and key passed the lab policy; `1` means none passed, and `2`
means a command/file input error. Each signature has a zero-based parsed
`HeaderIndex`, status (`Pass`, `Fail`, `Unsupported`), reason, canonicalization,
ordered `SignedHeaders`, RSA size, canonical body length, and separate
`BodyHashMatches` / `HeaderSignatureMatches` values. A null check was not reached.
The report includes the verification time and SHA-256 of the supplied message
and key-file text, without emitting message content or public-key material.
Keep the report with those exact inputs and their capture provenance.

The implementation follows the header selection, canonicalization and signature
input rules in [RFC 6376 §§3.4, 3.5, 3.7 and 5.4.2](https://www.rfc-editor.org/rfc/rfc6376.html).
It supports all four simple/relaxed combinations, the default `simple/simple`,
and a single `c=` token's default simple body. Repeated `h=` names select distinct
headers from the bottom upward; absent occurrences add no bytes. The current
signature is excluded from that selection and appended separately with only
its `b=` value and surrounding folding whitespace erased, without its final
CRLF. Both the full canonical body hash and RSA PKCS#1 v1.5 signature must match.

Bounds and intentional limits:

- Message: 32 MiB; complete headers: 256 KiB; one header: 64 KiB; header count and
  `h=` entries: 2,048; signatures: 16; UTF-8 key record: 32 KiB. Body processing
  scans spans without per-line objects and caches only two possible body digests.
- Only RSA-SHA256 and 1,024–4,096-bit RSA keys are admitted. RSA-SHA1 and keys below
  1,024 bits are rejected; 1,024-bit verification reports the recommendation to
  use 2,048-bit signing keys. See [RFC 8301 §3](https://www.rfc-editor.org/rfc/rfc8301.html#section-3).
  Ed25519 is explicitly unsupported, including the second RFC fixture signature.
- Both complete RSA SubjectPublicKeyInfo and raw PKCS#1 public-key DER are
  accepted, with trailing bytes rejected. This accommodates the RFC's deployed
  examples and the documentation discrepancy described in
  [erratum 3017](https://www.rfc-editor.org/errata/eid3017), which is held for a
  document update rather than a verified change to the standard.
- Every `l=` signature is unsupported, even if its claimed length covers this
  body. `z=` copied-header diagnostics, query methods beyond `dns/txt`, quoted or
  DKIM-QP-encoded `i=` local parts, and non-ASCII/non-LDH selector or domain labels
  are unsupported. Unquoted `i=` dot-atoms are limited to 64 characters. Signing
  domains require at least two labels; the complete DNS owner is at most 253
  ASCII characters. Bare CR/LF and malformed key folding are rejected.
- Exactly one physical From field must exist and `h=` must include From. The
  verifier does not parse that field's mailbox syntax or check alignment with
  `d=`. It does not enforce a recommended signed-header set. Unsigned headers and
  extra duplicates beyond `h=` coverage can change without invalidating DKIM;
  canonicalization-equivalent whitespace changes can also pass.
- Empty `p=` is revoked. Key hash/service restrictions and `t=s` identity-domain
  restrictions are enforced. A key with `t=y` can report successful cryptography
  but is `Unsupported` for final lab evidence. Expired `x=` signatures fail; no
  replay protection or future-signing-time policy is asserted.
- Unknown extension tags remain in the signed input. This is a bounded
  cryptographic checker, not a complete RFC 5322, DNS, SPF, DMARC, ARC, or mail
  receiver implementation. A pass does not establish byte-for-byte preservation,
  delivery, client compatibility, signer trust, or a live deployment gate.

The self-tests use fresh in-memory RSA keys with independently hand-assembled
signing inputs, all canonicalization pairs, body/header tampering, repeated and
absent headers, key and identity restrictions, expiry, malformed metadata, and
body boundaries. Private keys are neither saved nor printed. An independent
published signature from [RFC 8463 Appendix A](https://www.rfc-editor.org/rfc/rfc8463.html#appendix-A)
is verified and then body-tampered; its source, byte hashes and license are in
[Fixtures/README.md](Fixtures/README.md). Passing these checks is local tooling
evidence only. The final captured SmarterMail messages still need verification
with their corresponding independently recorded key evidence.
