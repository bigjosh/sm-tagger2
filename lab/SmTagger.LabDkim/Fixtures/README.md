# RFC 8463 DKIM fixture

`rfc8463.eml` is the public synthetic signed message in John Levine's
[RFC 8463, Appendix A.3](https://www.rfc-editor.org/rfc/rfc8463.html#appendix-A.3).
`rfc8463-key.txt` is the RSA TXT record from
[Appendix A.2](https://www.rfc-editor.org/rfc/rfc8463.html#appendix-A.2).
Both were extracted from the [official RFC text](https://www.rfc-editor.org/rfc/rfc8463.txt)
on 2026-09-10. No private key or private server capture is included.

The message retains both published signatures. The lab verifier selects the
RSA-SHA256 signature with `d=football.example.com`, `s=test`, and DNS owner
`test._domainkey.football.example.com`. Its canonicalization is
`relaxed/relaxed`; its header list oversigns From, Subject, and Date. The RSA
key is 1024 bits and is encoded as SubjectPublicKeyInfo. This historic test
key is only for the published fixture.

Formatting is explicit: both files contain ASCII bytes without a BOM and
use CRLF line endings. The RFC's three-space page indentation is removed
from message lines; each signature continuation retains its one leading
space. All message text, header folds, signature values, and the final
blank line required by Appendix A.3 are preserved. The TXT file concatenates
the RFC's four quoted character strings without added separators, removes
zone-file syntax, and appends one CRLF.

The message was compared with
[MimeKit's upstream RFC 8463 fixture](https://github.com/jstedfast/MimeKit/blob/master/UnitTests/TestData/dkim/rfc8463-example.msg).
After normalizing its LF line endings to CRLF, that copy is identical except
that it omits the RFC's final blank line. This fixture retains that blank
line. The independently calculated relaxed body hash matches the published
`bh=2jUSOH9NhtVGCQWNr9BrIAPreKQjO6Sn7XIkfJVOzv8=`. These checks establish
fixture identity and body-hash agreement, not live DNS or deployment evidence.

| File | Bytes | SHA-256 |
|---|---:|---|
| `rfc8463.eml` | 1090 | `ba153be9c487ba96d3c2fbf1d0040583a53fa3987c1cd01aa42e118258031a97` |
| `rfc8463-key.txt` | 236 | `3745d945763b0e6bf3819b6c1b2ad78d2cf2c3008be0f3531092a6410200e1a4` |

## Attribution and license notice

The example is reproduced from RFC 8463, "A New Cryptographic Signature
Method for DomainKeys Identified Mail (DKIM)", John Levine, September 2018.
Copyright (c) 2018 IETF Trust and the persons identified as the document
authors. All rights reserved. The RFC is subject to
[BCP 78 and the IETF Trust Legal Provisions](https://trustee.ietf.org/license-info).
The following Simplified BSD notice is included for the extracted example:

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
