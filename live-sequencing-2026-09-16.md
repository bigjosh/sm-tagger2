# Live SmarterMail sequencing observations — 2026-09-16

**Historical scope:** this report records the rc.4 experiment and its then-current policy. On 2026-09-17, the approved rc.7 contract added a positive `Written` gate: `Writing` and unexpected/incomplete statuses defer, while `Failed` retains its established error path. See [the current contract and evidence note](written-readiness-2026-09-17.md). The observations, original test counts, and limitations below remain unchanged; they are not fresh rc.7 live tests.

The captured behavior supports returning a completed pair to the normal spool **EML first, final HDR last**. Incoming final EML presence is only a candidate signal: under the policy tested here, the matching HDR had to exist, be readable without an active writer, and not say `Failed`. The tested direct API request with `sendImmediately: true` and the tested SMTP submissions publish files differently. Ordinary browser Send and delayed-send publication were not traced in this test set.

This report records bounded observations, not a vendor guarantee or approval of every deployment gate. The published rc.4 sorter completed the scoped live integration checks below without a production-code change.

## Environment and scope

- SmarterMail: `100.0.9742.26305+420ad0abfc12ec298959057b3a7aae179d13f0e1`.
- MailService executable SHA-256: `4D1983ABC5ECAEF9F5982287338DAD71E53709A25DC50B298598B4E2562501DA`.
- OS: Windows 11 Pro x64, `10.0.26200`, build `26200`. **Windows Server was not tested.**
- Input: `C:\SmarterMail\Spool\proc`; output: normal `C:\SmarterMail\Spool`, not `Spool\Drop`.
- Separately synthesized mail used reserved domain `sequence.test`, routed explicitly to a controlled SMTP sink at `127.0.0.2:25`, with local delivery disabled for that test domain. No global DNS settings were changed.
- The passive producer captures and three completed output-order trials did not run the sorter or tagger. Output pairs were moved by the test harness, without modifying their message bytes.

[Environment record](artifacts/sequencing/environment.json). Raw artifacts are intentionally ignored local evidence, unavailable from a fresh public checkout. This report contains no credentials or private sample messages.

## What the vendor documentation establishes

SmarterTools' current [Antispam Options](https://help.smartertools.com/SmarterMail/Current/Topics/SystemAdmin/Settings/Antispam/Options) documents third-party processing through `Spool\Proc`, but supplies no precise file-creation, close or return-order contract.

The vendor's [2018 SMCustomHeaders source](https://github.com/SmarterTools/SMCustomHeaders/blob/3c74049d6544f2ee96f965e36dec28123b05ae61/SM%20Custom%20Headers/SMCustomHeaders.cs) watches renames, considers final `.eml` files, and moves EML before HDR. Its destination is **Drop**, so it is not a normal-spool sequencing guarantee. In a [2025 employee answer](https://portal.smartertools.com/community/a96780/using-spool-proc-and-changing-destination-recipient.aspx), Kyle Kerst explains that normal subspools use HDR envelope addressing, whereas Drop does not process HDRs.

Matt Petty's [2018 employee resolution](https://portal.smartertools.com/community/a90382/custom-headers-service-issue-with-orphaned-hdr-files.aspx) explains that `Failed` on HDR line 1 indicates an SMTP session failure, including premature disconnect. This supports rejecting that status independently of file existence. No reviewed primary source guarantees the detailed ordering for every current or future route/build.

## Capture method and limitations

Microsoft-signed [Process Monitor](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon) 4.11 captured native PML files. Completed native CSV exports were parsed in full and their row counts retained. The initial capture reported process exit code 1; its first export attempt failed, and a later native export successfully read the original PML. Exit code 1 itself is not treated as proof of successful flushing. Original failure evidence remains retained.

Default CSV includes operation timestamps, paths, results and access/share details, but omits Duration, Thread ID and Completion Time. `ExportRowOrdinal` is the one-based native CSV data-row position, **not** Procmon's native Sequence Number. A read-only parser, based on the [original author's reverse-engineered PML-v9 layout](https://github.com/eronnen/procmon-parser/tree/96080961e51745ddc6e83490c24e540553c44c71), derives thread IDs and start-plus-duration completion times. Every selected record matches the native CSV's exact seven-decimal timestamp and PID; successful result codes and total event counts also match. These checks anchor event identity but do not independently validate the reverse-engineered duration interpretation.

The findings use operation records, not LastWriteTime as proof of sequencing. Directory enumeration is distinguished from opening or reading an individual message. Absence of later writes means none occurred in the captured interval, not that future writes are impossible.

[Tool verification](artifacts/sequencing/procmon-setup-verification.json), [initial recovered export](artifacts/sequencing/captures/input_auth_slow/complete.json), and [analysis notes](artifacts/sequencing/trace-analysis-notes.md).

## Incoming files: 104 SMTP lifecycles and one immediate API submission

Two passive captures cover a slow authenticated SMTP submission, slow inbound SMTP, an aborted authenticated submission, a large message, 100 concurrent SMTP submissions, and one direct API submission with `sendImmediately: true`. The sorter was stopped throughout.

**SMTP:** all 104 observed lifecycles wrote an early HDR and temporary `.em_`, then renamed `.em_` to final `.eml`. Captured HDR and message writes precede final rename; derived completion times place the final HDR close before rename start, on the same derived thread for each message. No later target mutations by MailService were captured. In the first slow example, final HDR close completes at derived UTC `16:00:29.2241759`, then final EML rename starts at native UTC `16:00:29.2251466`.

The aborted SMTP submission also produced a final `.eml`, with `Failed ` on HDR line 1 in the post-capture inventory. Its final HDR close completes at derived UTC `16:08:40.4223006`; final rename starts at `16:08:40.4229514`. **Final EML existence does not imply a successful message.** Procmon does not record written content; the status finding comes from the separate final inventory.

Successful final HDRs in these passive captures said `Written `. The policy at the time did not require literal `Written` and treated other non-`Failed` values, including `Ready`, as opaque status text; that policy was superseded by the approved rc.7 gate. A final `Ready` message was not separately observed in this capture set, and neither status text alone nor early HDR existence establishes readiness.

**Immediate API submission:** the harness called `POST /api/v1/mail/message-put` with `sendImmediately: true`; it did not use browser Send. For basename `422405313117`, the native trace directly records creation of `C:\SmarterMail\Spool\proc\422405313117.eml`, followed by its write and close, and only then creation/write/close of the matching HDR in that same `proc` directory. Searching the full native CSV for this basename finds no `delay` path or rename, including rename target details. All target operations were on one derived thread:

| Native UTC time | Operation |
| --- | --- |
| `16:08:45.4826922` | Create final EML, Generic Write, Share Read |
| `16:08:45.4828549` | Write EML |
| `16:08:45.4829444` | Close EML writer; derived completion `.4829897` |
| `16:08:45.4831242` | Create HDR, Generic Write, Share Read |
| `16:08:45.4832770` | Write HDR |
| `16:08:45.4833359` | Close HDR writer; derived completion `.4833771` |

The saved API response reports `undoInProgress: false` and `isScheduledMessage: false`. This immediate API case does not establish the order used by ordinary browser Send. The user subsequently reported observing HDR publication before an EML rename from `delay` into `proc` in a separate manual Procmon test. That report is consistent with a different publication path; its trace has not yet been independently analyzed here. Earlier wording that generalized this API finding to all webmail was too broad.

Consequently, final EML existence is not a universal publication boundary for a complete pair across the tested routes. In watch mode, a missing or busy HDR must defer processing. Once readable, a `Failed` HDR remains a separate rejection condition.

[Slow authenticated analysis](artifacts/sequencing/captures/input_auth_slow/sequence-analysis.json), [passive route analysis](artifacts/sequencing/captures/passive_routes/sequence-analysis.json), [final inventory](artifacts/sequencing/passive-final-inventory.json), [immediate API native operations](artifacts/sequencing/captures/passive_routes/message-422405313117.csv), [derived operation headers](artifacts/sequencing/captures/passive_routes/message-422405313117-pml-headers.json), and [API response](artifacts/sequencing/webmail-traced.json).

## Windows sharing is part of HDR readiness

The immediate API route's writer requested Generic Write with Share Read. That does **not** allow every read-only open to succeed. Windows checks sharing in both directions: a new handle's share mode must permit the existing handle's access, as well as the existing handle permitting the new access. [Microsoft's CreateFile documentation](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew) explicitly documents both checks.

The sorter's `File.ReadAllBytes` HDR read uses read access and `FileShare.Read`, as shown in the [.NET 10 implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/File.cs). Because that share mode excludes writes, Windows rejects the read while an incompatible writer remains open. The sorter handles the sharing violation as `HDR_BUSY` and retries in watch mode. This is a Windows-specific compatibility argument; it is not asserted as an equivalent cross-platform lock guarantee.

Keep this sharing behavior when changing the HDR reader: allowing `FileShare.Write` would remove the observed writer exclusion. A successful read alone also cannot prove that a producer will never reopen the file later; that limitation remains a version-sensitive SmarterMail assumption. [Current reader](src/SmSorter/SorterProcessor.cs), [readiness tests](tests/SmTagger.Tests/SorterReadinessTests.cs).

## Returning pairs to the normal spool

Each completed trial withheld one part of a primary pair while publishing a complete control pair. The control was delivered during the gap, proving the consumer was active. Directory queries in the full native CSV repeatedly listed the incomplete primary state.

| Trial | Observed incomplete interval | MailService activity on primary |
| --- | --- | --- |
| EML first, HDR last | `11.948062` seconds | EML listed; no primary file opens, reads or attribute probes until final HDR appeared. |
| HDR first, EML last | `11.4187967` seconds | 17 successful HDR reads and 17 attempts to open the absent EML, returning NAME NOT FOUND. No primary writes/deletes in the gap. |
| EML plus `.hdr.pending`, final HDR rename last | `14.9821921` seconds | Both listed; no primary file opens, reads or attribute probes until rename to final `.hdr`. |

For EML-first, the final HDR move completed at application UTC `16:11:43.2722723`; the first primary HDR read-open was native UTC `16:11:45.8589384`. For HDR-first, the first HDR read was `16:13:04.0760816`, followed by a missing EML Generic Read open at `16:13:04.0762453`; later attempts included attribute probes. Final EML publication completed at application UTC `16:13:13.4675617`. For the suffix trial, final HDR rename completed at application UTC `16:14:53.8682185`; first HDR read-open was native UTC `16:14:55.4789719`.

Each of the three primaries and three controls reached the sink once within its observation window, with the correct SMTP envelope and no remaining pair at trial end. The visible To address deliberately differed from the envelope recipient. HDR-first was tolerated in this interval; **the test did not demonstrate HDR-first data loss**. It demonstrated early processing attempts on an incomplete pair. EML-first/HDR-last avoided those attempts on this build.

[EML-first trace analysis](artifacts/sequencing/captures/output_eml_first_02/consumer-sequence-analysis.json), [HDR-first trace analysis](artifacts/sequencing/captures/output_hdr_first_01/consumer-sequence-analysis.json), [suffix trace analysis](artifacts/sequencing/captures/output_hdr_rename_last_01/consumer-sequence-analysis.json). Adjacent focused CSVs preserve the native rows; each trial's application events and receipts are under `artifacts/sequencing/output-trials/`.

## Body bytes and excluded attempts

The strict body-equality check in the original output trial JSON remains **false**. In the examined EML-first primary and manual pass-through control, all 2048 original body bytes were preserved as a prefix, followed by exactly one extra CRLF (`0D0A`). Generated and Proc body hashes match; sink body hashes differ. The sorter and tagger were not involved. Sink code inspection supports SmarterMail outbound SMTP framing as the source, but no independent wire capture establishes that attribution. No general newline normalization or silent byte-identical pass was applied. [Exact comparison](artifacts/sequencing/body-framing-comparison.json).

`output_eml_first_01` failed staged-hash preflight before any move. Its retained records are excluded from behavioral trial counts. [Preflight failure](artifacts/sequencing/output-trials/output_eml_first_01/trial.json).

The first live-sorter harness attempt, `backlog-rc4`, opened active EML files using a sharing mode that can obstruct their movement. It encountered a sharing violation during an EML move; the harness then stopped its watcher while another message was owned. This confounded run is excluded from the integration conclusion, rather than attributed to a production defect. Its failed result, logs, queue inventories, and retained partial states remain intact. The corrected monitor uses filename/attribute inventories while mail is active. [Excluded harness result](artifacts/sequencing/sorter-trials/backlog-rc4/result.json).

## Live sorter integration

The tested standalone `sm-sorter.exe` was the unchanged rc.4 release, SHA-256 `6C2E1822C55E502A7B2A18200240B71860AF80B3B776088C901597561B7B3E5F`. Its data root had an empty `senders` directory and no `profiles` directory. These tests therefore exercise pass-through and upstream failure retention, not enrollment or rewriting.

| Clean check | Observed result |
| --- | --- |
| Startup backlog | 35 unique PASS records, no ERROR, no successful-message residuals. All 34 SMTP tokens and the separately identified immediate API message arrived once with the correct envelope. |
| Live SMTP watcher | 100 new accepted messages, 100 unique PASS records and deliveries, no successful-message residuals. One deliberately aborted SMTP pair was retained with exactly one `UPSTREAM_FAILED` ERROR, diagnostic and stderr report; it was never delivered. |
| Live immediate API watcher | 100 successful API submissions with `sendImmediately: true` in approximately 0.8 seconds, 100 unique PASS records and deliveries, correct envelopes, empty stderr and no final message residuals. All 200 decoded plain-text/HTML alternatives contained the complete submitted token exactly once. |

The 134 SMTP bodies and the separately traced backlog API body preserve their original body bytes followed by the explicitly measured extra CRLF. Exact sender-body equality is **not** claimed. The 100 API messages are validated as SmarterMail-generated MIME messages, not as byte-identical copies of an input MIME file.

The original API test verifier assumed a single `text/plain` body and reported failure. SmarterMail actually generated `multipart/alternative` containing both plain text and HTML. Its original result remains unchanged. A separate standard-library MIME audit of all saved requests and sink copies confirmed 100 unique correct deliveries, both complete alternatives per message, and no MIME defects or truncations. No resend was needed, and the correction does not alter the sorter or its results.

[Clean backlog result](artifacts/sequencing/sorter-trials/backlog-rc4-clean/result.json), [clean SMTP watcher result](artifacts/sequencing/sorter-trials/watch-rc4-clean/result.json), [independent integration audit](artifacts/sequencing/independent-integration-audit.json), and [API MIME/delivery audit](artifacts/sequencing/webmail-trials/webmail-load-rc4/serialization-aware-audit.json) retain the detailed counts and hashes. Artifact names containing `webmail` are retained for continuity; these submissions used the immediate API route described above. The held failure's original HDR/EML hashes independently match its [retained copies](artifacts/sequencing/retained-failed-clean/disposition.json).

Two deterministic Windows regressions now exercise an HDR writer that permits readers: a permissive reader can see its unfinished bytes, but the sorter's own `FileShare.Read` access must defer until the writer finishes and closes. The complete suite passed **398 tests, zero failures or skips**, with a clean Release build and formatting check. [Full test evidence](artifacts/test-results/sequencing/sequencing-full.trx).

The loopback SMTP helper's session limit was increased from 16 to 64 so it can accept this build's 50 outbound workers without creating capture-side connection rejection. Its five loopback DNS/SMTP self-tests passed. This helper is outside the production applications and release assets.

## State after testing

The live spool was empty at teardown. The experiment restored `useSpoolProcFolder` to its original `false` value, stopped its sorter, capture sink and elevated trace helper, and left SmarterMail running. Global DNS and spool settings matched the saved baseline. Synthetic evidence and inert pairs remain under the lab directories, and the reserved `sequence.test` domain/account remains configured for inspection. No retained message was automatically replayed. [Teardown record](artifacts/sequencing/teardown.json).

Windows Server, production deployment, tagger edits, signing, catch-all delivery and client compatibility remain outside these observations. Recheck publication patterns and writer-sharing assumptions when changing SmarterMail builds, relevant .NET file APIs, operating systems or message entry routes.
