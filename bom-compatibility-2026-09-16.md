# Leading EML BOM compatibility — 2026-09-16

The local **1.0.0-rc.5** tagger accepts and preserves one exact UTF-8 byte-order mark (`EF BB BF`) at the absolute beginning of an EML. This corrects the immediate-API failure found in the [rc.4 live suite](live-smartermail-2026-09-16.md). It is a narrow, version-sensitive SmarterMail spool compatibility rule, not general Unicode-header support or a change to the sorter.

The Release build and all **439 automated tests** passed, with zero warnings, errors, failures, or skipped tests. Formatting and release-package verification passed. **Six fresh live scenarios produced exactly eight deliveries**, each with one independently verified DKIM signature aligned to its final From domain. The candidate is built locally; this document does not announce a GitHub release.

## Why the exception exists

On SmarterMail `100.0.9742.26305+420ad0abfc12ec298959057b3a7aae179d13f0e1`, `POST /api/v1/mail/message-put` with `sendImmediately:true` produced a Proc EML beginning `EF BB BF 46 72 6F 6D 3A` — a three-byte marker followed by `From:`. The rc.4 parser treated the marker as part of the field name and stopped at `parse-eml` with `Invalid EML field name.` The sorter had correctly diverted the enrolled message; the original files stayed at the documented `.start` boundary.

The delayed/scheduled API examples and SMTP controls in that earlier suite had no leading marker. Successful sorter pass-through did not establish tagging compatibility: the sorter does not parse EML headers, and SmarterMail serializes the file again before delivery. The earlier failure evidence remains preserved; it was not replayed as part of the correction.

This is observed behavior of the tested build. Do not assume every immediate send has a BOM, or that every other route will always omit it. Inputs with either no marker or the one permitted marker follow the same remaining validation rules.

## Exact contract and implementation

[spec.md](spec.md), [implementation.md](implementation.md), and `VERSION-SENSITIVE-005` in [assumptions.md](assumptions.md) define the maintained contract. The implementation is in [EmlDocument.cs](src/SmTagger.Mail/EmlDocument.cs).

- Recognize **one complete `EF BB BF` sequence at byte offset zero only**. Keep the original byte array intact. Scan the header after those three bytes, then restore every physical-line position to an absolute source offset before parsing addresses or creating edits.
- Treat the marker as a preserved file preamble, outside the first field. A first `From:` is therefore recognized normally. Removing a leading `Return-Path:` removes that field, not the preamble; removing several such fields still leaves exactly the original prefix before the first surviving field.
- Synthesized `Reply-To:` copies only the original From field bytes, then applies the ordinary field-name and address changes. It must never copy the preamble. Existing Reply-To, folding, display names, opaque headers, and body bytes retain the existing preservation rules.
- If an edited physical line is the first surviving original line, include the preserved three bytes in its 998-byte limit. This also applies after removing leading Return-Path fields. A synthesized Reply-To has no prefix to count. The existing scope remains edited original lines and every synthesized line; this change does not start validating untouched opaque lines.
- Keep the CRLF framing, ASCII address grammar, exactly-one-From contract, MDN policy, and failure retention unchanged. HDR parsing and sorter readiness/publication order are unchanged.

A partial prefix, repeated prefix, UTF-16/UTF-32 prefix, or marker inserted before a later field name receives ordinary grammar rejection; there is no search-and-strip operation. Marker-like bytes inside an opaque header value or the body remain opaque bytes. The tagger neither decodes nor re-encodes the whole message. In particular, this does not enable SMTPUTF8 addresses.

## Automated and packaged verification

| Layer | New cases | What the cases establish |
|---|---:|---|
| [EmlBomTests.cs](tests/SmTagger.Tests/EmlBomTests.cs) | 29 | Exact source positions and output bytes; first/folded/repeated Return-Path removal; existing and synthesized Reply-To; malformed-prefix rejection; opaque/body preservation; 998/999-byte boundaries with and without the prefix |
| [BomIntegrationTests.cs](tests/SmTagger.Tests/BomIntegrationTests.cs) | 10 | Activated single/group processing and retained copies; unchanged no-match pass; From-cardinality error retention; invalid-prefix holds; MDN holds with Content-Type first |
| [BomExecutableTests.cs](tests/SmTagger.Tests/BomExecutableTests.cs) | 2 | Real published application-folder and standalone EXEs produce two exact children, preserve original `.in` and child `.out` files, and leave binary body bytes unchanged |

The **41 new cases plus the 398-test baseline all passed**. The three private sample tests ran locally using working copies; their originals and contents remain excluded from source control and packages. Existing crash-boundary, logging-failure, sharing/readiness, and watcher regressions also passed in the full run.

Commands used from the repository root:

```powershell
dotnet build SmTagger.slnx -c Release
dotnet format SmTagger.slnx --verify-no-changes --no-restore
.\scripts\Publish-Release.ps1
dotnet test SmTagger.slnx -c Release --no-build --logger 'trx;LogFileName=bom-full.trx' --results-directory artifacts/test-results/bom
```

Local evidence: [full TRX](artifacts/test-results/bom/bom-full.trx), [preserved rc.5 release manifest](artifacts/failed-retention-20260916/prior-rc5/manifest.json), and [independent artifact verification](artifacts/bom-fix-20260916/artifact-verification.json). All 400 deployment files, 396 ZIP entries, and both package hashes matched. The sorter package contains no tagger mail/engine assemblies. These generated files are ignored by Git; the source tests and this report are shared.

## Fresh live verification

All cases ran against the actual local installation and a separate loopback SMTP/DNS receiver. Natural submissions completed their API request or SMTP QUIT before the harness obtained closed read access to HDR and EML. The real rc.5 standalone sorter and tagger processed the five natural cases as one-shots; the last fixture intentionally exercised only the tagger and SM consumer. No earlier failed message was replayed.

| Fresh scenario | Input observed / constructed | Deliveries and result |
|---|---|---|
| Immediate API, one recipient, empty request Reply-To | Leading BOM; SM supplied the selected private alias as Reply-To | 1; correct individual From and Reply-To |
| Immediate API, two recipients, empty request Reply-To | Leading BOM; SM supplied a matching Reply-To | 2; individual From per child, matching Reply-To replaced by the group tag |
| Immediate API, explicit matching Reply-To | Leading BOM and matching Reply-To | 1; supported identities rewritten correctly |
| Delayed API control | No BOM; matching Reply-To supplied by SM | 1; unchanged non-BOM behavior |
| Authenticated SMTP control | No BOM; no Reply-To | 1; unchanged non-BOM behavior |
| Prepared synthetic private-queue consumer fixture | One BOM, leading Return-Path, private From, two recipients, no Reply-To; saved natural HDR metadata copied unchanged | 2; Return-Path removed, prefix retained once, group Reply-To synthesized without copying the prefix |

The last row is **not** evidence that an API route naturally emits that header combination. It is an explicitly prepared synthetic input, placed directly in the private process queue with a fresh basename, to exercise the real tagger and downstream SmarterMail consumption/signing boundary. The natural API runs did not cover absent-Reply-To synthesis: `replyTo:""` still caused this installation to emit the selected alias as Reply-To. No undocumented omission trick was assumed.

For all eight children, the original `.in` copies were exact; retained `.out` bytes matched the independently specified header edits and unchanged bodies. Each input prefix remained once at absolute offset zero, and synthesized Reply-To contained none. The receiver obtained each expected child once, with the intended envelope recipient and rewritten From, and no extra captures or live queue leftovers. Each final message had exactly one cryptographically valid DKIM signature with `d=` equal to the final From domain. Signing used the existing normally DNS-verified key, without forced activation.

None of the eight wire messages contained a BOM. SmarterMail added one trailing CRLF to each body during output serialization; the tagger's retained body remained byte-identical to its input. This distinction is intentional: byte preservation is checked before SM consumes the files, while final SMTP content and signatures are checked separately. The results establish this consumer behavior only for the recorded build.

The first harness observation stopped before invoking either program because it required `Written` instead of the actual space-padded `Written ` status. Its initial no-Reply-To expectation was also corrected after inspecting the actual API output. The original attempt report remains intact. The same fresh, unprocessed pair was then explicitly processed once; it was not resubmitted, and no previously processed or failed production item was replayed. These were test-harness corrections, not changes to the sorter or HDR contract.

Local evidence: [final reconciliation](artifacts/bom-fix-20260916/live/final-audit.json), per-case inputs/outputs, stdout/stderr, envelopes, and independent DKIM results under `artifacts/bom-fix-20260916/live/cases/`, plus the eight received messages under its `sink/`. Duplicate checks cover the finite observed interval, not an unlimited guarantee. The ignored helper files reproduce this machine-specific experiment; the shared C# tests provide portable regression coverage.

## Lab restoration

Readback confirmed restoration of the pre-test DNS settings and disabled DKIM signing; the existing key and its active verification state were retained unchanged. The domain route and catch-all settings were unchanged, and Proc interception remained enabled as before the test. The owned receiver was stopped; no sorter or tagger worker remained running. MailService kept its existing process, and the user's visible Procmon session was left open.

Both live queues were empty. The four preserved manual-test files still matched their original hashes, the prior private error-file inventory was unchanged, and the mapping count remained 14. Earlier held failures and all new input/output evidence remain available for inspection. See [settings readback](artifacts/bom-fix-20260916/live/restored-settings.json) and [process readback](artifacts/bom-fix-20260916/live/restored-processes.json).

## Exact artifact and environment identity

| Item | Value |
|---|---|
| Candidate | `1.0.0-rc.5`, self-contained Windows x64, folder and standalone packages |
| SDK / bundled runtime | `10.0.400` / `10.0.11` |
| Host | Windows 11 Pro, `10.0.26200`; local NTFS |
| SmarterMail | `100.0.9742.26305+420ad0abfc12ec298959057b3a7aae179d13f0e1` |
| MailService executable SHA-256 | `4d1983abc5ecaef9f5982287338dad71e53709a25dc50b298598b4e2562501da` |
| Standalone sorter SHA-256 | `47299827036ac68fef54e34ee0d7cd16bc927d0b4a287fdc76fc7b492fc79c3d` |
| Standalone tagger SHA-256 | `83fb1a2c13351f9ab21f259285d4ff8e90bec34fb7531f4270d0580540a682cc` |

The previous rc.4 standalone EXEs and manifest were preserved under ignored `artifacts/bom-fix-20260916/prior-rc4/` before rebuilding. A version string alone does not identify a rebuilt executable: record its SHA-256 and matching manifest. The publicly released rc.4 does not contain this correction.

## If this breaks after an update

1. Record the exact SmarterMail build, Windows version, tagger version/hash, submission route, and `sendImmediately` value. Preserve the original pair and diagnostic before manual disposition. Do not automatically replay a held message or edit away its prefix to make it pass.
2. After the producer has finished and files are safely readable, inspect the EML's first bytes in hex. The expected variants are an ordinary first field or exactly `EF BB BF` followed by that field. A later/doubled marker, a different encoding, changed newline framing, or an incomplete HDR is a different problem. Keep the EML/HDR readiness investigation separate; the prefix fix is not evidence that the producer has finished writing.
3. For `parse-eml` / `Invalid EML field name`, distinguish an old deployed rc.4 binary from a new producer format. For missing/repeated From or MDN diagnostics, investigate the unchanged mail contract rather than broadening prefix acceptance. For a line-length failure, count actual bytes, including the prefix on the checked first surviving line.
4. Reproduce with fresh synthetic messages: immediate API with one recipient, immediate API with two recipients, immediate API with a matching existing Reply-To, delayed API, and authenticated SMTP. Confirm actual input bytes and Reply-To presence before claiming coverage. If SM supplies Reply-To automatically, use a separately labeled prepared queue fixture for absent-Reply-To synthesis; do not count that as a natural producer path. Include first Return-Path removal and near-limit lines in automated fixtures even if SM does not emit those forms in the live examples.
5. Compare the original EML to retained `.eml.out` copies from `-keep`. Require only the specified edits, an unchanged body, one preserved prefix if present, and no copied prefix in synthesized Reply-To. Confirm one complete delivery per intended child, exact tagged From, envelope recipients, and an independently valid DKIM signature aligned to the final From domain.
6. Inspect the **received wire bytes** as well as Proc and `.out`. The compatibility assumption includes SmarterMail consuming the retained prefix correctly. If a future build exposes it on the wire, misparses From, or signs different content, reopen `VERSION-SENSITIVE-005` and the signing/relay gate. Do not infer success from an exit code, a Sent-folder copy, or mere presence of a DKIM header.

Use a synthetic isolated lab or the documented supervised bring-up procedure. Configure sending display names independently: the API may default an alias display name to its private local part, and preserving that display name is intentional. This fix changes parsed identity addresses only.

These results do not approve Windows Server, other SmarterMail versions, public DNS/SPF/DMARC, external providers, arbitrary mail clients, or every possible readiness race. The complete deployment gates remain in [testing-plan.md](testing-plan.md) and [assumptions.md](assumptions.md).
