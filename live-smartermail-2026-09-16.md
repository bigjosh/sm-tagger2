# Live SmarterMail compatibility suite — 2026-09-16

**Historical rc.4 results:** the BOM correction has since been implemented in the local rc.5 candidate. See the [BOM compatibility follow-up](bom-compatibility-2026-09-16.md) for the exact contract, regression tests, current live retest status, and future diagnosis procedure. The observations and held failure evidence below describe the unchanged rc.4 executables used in this original suite.

This is a follow-up to the [native file-sequencing investigation](live-sequencing-2026-09-16.md). It exercises the actual local SmarterMail service with synthetic SMTP and API messages, the unchanged rc.4 executables, independent receiving SMTP/DNS services, and saved-file audits. The live experiments used synthetic messages; private `samples/` originals remain unchanged and excluded from shared artifacts. The separate automated suite includes its existing private local sample checks.

**An immediate-API tagging compatibility blocker was found in rc.4.** SmarterMail's `sendImmediately:true` route can prepend a UTF-8 BOM to the EML in Proc. That tagger rejects the input at `parse-eml` as `Invalid EML field name`. Sorter-only pass-through works; successful sequencing tests therefore did not establish tagger compatibility. That artifact must not serve this route for an enrolled account. See the follow-up above for the corrected candidate's evidence. Delayed and scheduled API inputs in this suite had no BOM and were tagged and delivered successfully.

The other completed tests below exercised the expected routing, rewriting, retention, and delivery behavior. They do not approve untested clients, Windows Server, external providers, or arbitrary SmarterMail versions. No production parsing or processing code was changed during this suite.

## Exact environment and evidence boundaries

| Item | Tested scope |
|---|---|
| OS | Windows 11 Pro, 10.0.26200; **not Windows Server** |
| SmarterMail | `100.0.9742.26305+420ad0abfc12ec298959057b3a7aae179d13f0e1` |
| MailService executable SHA-256 | `4D1983ABC5ECAEF9F5982287338DAD71E53709A25DC50B298598B4E2562501DA` |
| Sorter executable SHA-256 | `6C2E1822C55E502A7B2A18200240B71860AF80B3B776088C901597561B7B3E5F` |
| Tagger executable SHA-256 | `331BA692DE9E6BCC3EE459A1233ED90B154B1149A91E9629D49421D10F4BF35A` |
| Storage | Actual SM spool and test datadir on the same local C: NTFS volume |
| Submission | Loopback SMTP and installed HTTP API; authenticated synthetic domain administrator mailbox |
| Outgoing receiver | Loopback SMTP sink, independent message/envelope capture; controlled DNS answering only test zones |
| Domain arrangement | One primary `sequence.test` domain and a `reply.sequence.test` domain alias; a second primary domain was refused by the installed license |
| Signing | Real SM DKIM, normally verified DNS key, 2048-bit RSA, relaxed/relaxed; signing was not forced |
| SMTP policy | Existing authentication and sender-match settings retained |

Machine-specific fixtures, credentials, tokens, captures, transcripts, queue copies, and audit tools remain under ignored `artifacts/live-suite-20260916/` and `lab/`. Reports in this document describe synthetic identities only. The user's two original manual Proc pairs were hashed and moved to a separate local preservation directory before the tests. No automatic replay of held mail was performed.

The user's visible Procmon session was left untouched. This follow-up uses SMTP/API responses, SM logs, controlled file observations, saved inputs/outputs, and receiver evidence. It does **not** claim a new native trace of every producer operation. The earlier native sequencing report remains the source for its specific file-handle observations.

## Findings that affect deployment

### 1. Immediate API BOM: confirmed tagger blocker

The immediate alias-send EML began `EF BB BF` followed by `From:`. The sorter diverted it correctly, but `EmlDocument.Parse` treated those bytes as part of the first field name and rejected it. The original pair remained at the documented `.start` boundary with its diagnostic; no output was published. Delayed and scheduled sends using the same selected alias did not have this prefix and succeeded. Undo canceled a delayed send, produced no delivery, and returned the exact marked message to Drafts.

The initial saved-file review found the BOM in three identified immediate/API spool copies and not in the sampled delayed/scheduled inputs. The later failure-isolation phase reproduced the BOM hold with another fresh message. Absence from received wire messages does not prove absence from the tagger's input: SmarterMail's output serialization removes or normalizes material after the queue boundary.

Recommended correction: explicitly recognize one exact UTF-8 BOM at offset zero as a preserved preamble, keep all edit offsets absolute, and ensure it is not copied into a synthesized Reply-To or removed accidentally with a first Return-Path field. Add regression cases for these boundaries and rerun immediate API tagging. An **unapplied** patch proposal and test design were prepared locally; applicability was checked, but the proposal was neither compiled nor included in the executables. This requires a documented parser-contract change, not a silent expansion of the current specification.

Evidence: `api-bom-review.md`, `api-bom-evidence-final.json`, `api-runs/`, `proposed-api-bom.patch` under the suite evidence root.

### 2. An immediate SMTP disconnect can produce Failed after DATA 250

Six preliminary accepted SMTP probes and one isolated immediate-close control produced a final EML with a `Failed` HDR. SM's own SMTP logs record `250 OK`, then a disconnected-client rejection, then HDR writing and EML publication. Closing immediately after the DATA response reproduces it. Sending QUIT immediately, or waiting before closing TCP without QUIT, produced `Written` in the controls.

This is an SM submission behavior, not evidence that the sorter should ignore `Failed`. The sorter correctly held the preliminary pairs. A client's DATA success is not sufficient evidence of a usable Proc message on this build. Preserve and investigate Failed inputs; do not automatically replay them.

Four paused/NOOP/RSET controls had final EMLs and readable Written HDRs before their close action. No readable Failed-to-Written transition was observed. The polling checkpoints start too late to establish every earlier file-handle transition, and their read-sharing probes can perturb timing. These observations do not justify replacing the current final-EML plus successful-HDR-read contract with a new timing heuristic.

Evidence: `smtp-close-runs/`, `smtp-close-immediate-runs/`, and `independent-smtp-phase-audit.json`.

A later [Failed-spool consumer experiment](failed-spool-2026-09-16.md) returned two fresh, preserved Failed pairs to normal spool: one after client-received DATA 250 and one after an incomplete DATA transfer. Build 9742 explicitly logged removal of both as Failed, cleared their queue files, and produced no receiver capture during either 30-second observation. A fresh Written control delivered normally. Returning a Failed pair to spool is therefore a disposal path on this tested build, not a preservation or recovery mechanism; older held evidence was not replayed.

### 3. Client identity and display-name configuration still matter

With this server's existing `requireSendAddressAuthMatch:true`, SMTP MAIL FROM using the private alias was rejected even though the alias permitted sending. Authenticated SMTP with the account envelope and private EML From succeeded and exercised the documented partial-match behavior. The API's selected alias successfully supplied the private envelope on its delayed/scheduled routes. Do not infer that an approved alias works through every client/submission combination, and do not weaken global SMTP policy merely to hide a failed bring-up gate.

The alias API also defaulted the From display name to the private alias's local part. The tagger intentionally preserves display names. Set a suitable human-readable display name and inspect actual received messages; rewriting the addr-spec does not make an identifying display name secret. This is within the already documented secrecy boundary, but is an important onboarding check.

## Completed processing and transport checks

| Experiment | Observed result and qualification |
|---|---|
| Original 19-case SMTP matrix | 14 cases rejected upstream because of envelope/auth policy; the five admitted cases exercised tagged delivery, unchanged pass-through, retired identity, and MDN holds. Rejections are not counted as exercised tagger behavior. |
| Adapted 19-case SMTP matrix | 18 admitted using the authenticated account envelope: 10 tagged parents plus two unchanged parents produced 16 deliveries; six invalid/retired/MDN cases were retained. The missing-From case was rejected by SM before reaching the tagger. |
| Sender fields and fan-out | Exact input-to-output edits, single-recipient child envelopes, stable individual/group mappings, deliberate Reply-To, matching Reply-To, removal of multiple Return-Path fields, and unchanged opaque body bytes checked. SM deduplicated one duplicate-recipient fixture before the tagger; that live case does not establish the tagger's own deduplication branch. |
| Wrong From counts | Repeated From and multi-mailbox From were retained with the specified `.hdr.err` / `.eml.err` contract-error evidence and no delivery. |
| MDNs and retired identities | Both standard MDN content-type forms and retired private identities were held. These synthetic cases do not approve actual client-generated MDNs or enabling `allow-mdn`. |
| Fresh nine-case SMTP probes | Seven accepted; inbound spoof passed unchanged, DSN fan-out produced two deliveries, MIME and three size cases produced one each, SMTPUTF8 recipient was retained. Null reverse path and an over-advertised SIZE declaration were rejected upstream. |
| Forged EML authentication | Unauthenticated remote mail containing forged authentication-like EML fields did not acquire trusted HDR auth and did not enter the tagger. |
| DSN envelope metadata | Each child retained its own notify instruction while preserving ENVID, return type, and other opaque HDR bytes. A real SM-generated success DSN additionally reported the expected recipient, original ENVID/message, relayed action, and 2.0.0 status. The sink did not advertise DSN, so downstream relay of SMTP DSN options remains untested. |
| MIME and binary attachment | All three decoded parts matched fixture, Proc, tagger output, and received wire content, including the 1,078-byte binary attachment. Tagger output body bytes were exact. |
| Size samples | 32 KiB, 128 KiB, and 2 MiB messages passed tagging and real delivery; body preservation checked. These are samples, not a capacity benchmark. |
| Real SM output serialization | SM converted some 8bit parts to quoted-printable, changed MIME parameter quoting, and added trailing CRLF on wire output. Audits reconstructed these exact observed changes separately from byte-exact tagger edits. |
| Concurrent watchers | Four concurrent SMTP submitters, 45 accepted messages, 40 tagged plus five unchanged, exactly 75 deliveries. Mapping and enrollment identity files remained byte-identical; no live input backlog or duplicate output remained. |
| Failure isolation | With only the sorter running, an enrolled message waited in `process` while unauthenticated inbound mail delivered. Starting the tagger delivered the waiting message; a new immediate-API BOM input was then held, and the following SMTP message still delivered. Four inputs, three deliveries, one intentional hold. |
| Quiet immediate API, empty enrollment | One message passed the real sorter watcher and reached the sink with only `senders/` present in its datadir. This specimen did not require a readiness deferral; it therefore does not independently measure rescan recovery latency. |
| Real logging failures | Directory blockers at the sorter log, tagger trace, and existing mapping logs produced stderr errors; both programs exited zero and the message delivered. All 15 pre-existing log files were restored byte-for-byte. |
| Delayed/scheduled API | Both tagged and delivered. Scheduled Send was observed later than the first harness deadline; the same accepted pair was then processed, with no resubmission. Scheduling is not an exact-second promise. |
| Undo | Delayed send canceled; no matching delivery and exactly one marked Drafts message confirmed through read-only mailbox/raw retrieval. |

The watcher helpers were terminated only after their expected work was accounted for. That does not establish graceful Ctrl+C/Ctrl+Break handling by a service wrapper. The existing automated suite covers its separate shutdown contract.

## Admission boundaries

The saved domain and server settings have distinct limits; do not treat API, domain, SMTP advertisement, and DATA limits as interchangeable.

| Probe | Outcome |
|---|---|
| Immediate API plaintext body of 10,485,761 bytes | HTTP 400, `DOMAIN_SIZE_LIMIT_EXCEEDED\|10240000`; no queue files during the 30-second observation. |
| SMTP DATA of 13,981,014 bytes, no MAIL SIZE declaration | DATA 552 naming a 10,485,760-byte maximum; a truncated final EML and Failed HDR remained and were preserved without delivery. EHLO advertised SIZE 13,981,013. |
| Immediate API with 501 recipients | HTTP 400, `Too many recipients.`; no queue files during the observation. The domain setting was 200, so this does not establish a 500-recipient API threshold. |
| SMTP with 501 RCPT commands | First 200 RCPT replies 250, remaining 301 replies 452; RSET 250 and QUIT 221. No DATA was sent and no queue files appeared during the ten-second observation. This does not claim acceptance of a 200-recipient DATA message. |

These probes establish rejection at the exercised points, not exact just-under/just-over acceptance for every representation or route. Delayed/scheduled API size boundaries, attachments near limits, production configuration, and resource capacity remain separate checks. Evidence: `admission/`.

## Final reconciliation, catch-all, and restoration

The frozen outgoing inventory contains **109 captures**, all accounted for: **107 controlled deliveries** (105 authenticated outputs, each with exactly one independently verified DKIM signature aligned to its visible From domain, plus two correctly unsigned unauthenticated inbound passes), one validly signed SM-generated success DSN, and one validly signed pre-suite queued message. No expected controlled delivery was missing, duplicated, or delivered to an unexpected recipient group. The pre-suite message's retry/delivery was linked by queue ID and connection port in SM's logs and excluded from controlled-suite counts; it went only to the local sink. Admission and close-timing probes deliberately preserved without publication are separate experiments, not expected receiver deliveries.

Signature verification used the public key actually configured in SM and served by the lab DNS. It checked both the body hash and RSA header signature, with the expected signing domain derived independently from each received From field. These are real post-rewrite signing checks, including previously unprovisioned tag addresses and 2 MiB mail, but not an external SPF/DMARC or deliverability test. Evidence: `dkim-audit/batch-final-after-isolation-snapshot.json`, `final-after-isolation-case-ledger.json`, and the separate per-message verification files. A canceled or retained input is outside the delivery/signature denominator.

The inbound catch-all phase temporarily selected local delivery and a catch-all targeting the test mailbox. Three unauthenticated messages addressed to an allocated individual tag, an allocated group tag, and a never-allocated address all passed the sorter and arrived exactly once in Junk E-Mail. Their visible To fields deliberately named an unrelated address; the original SMTP recipient was absent from every submitted EML. SM preserved each original recipient in an added `X-Rcpt-To` header. The official mailbox-export ZIP contained all three original EMLs with literal address brackets and byte-identical submitted bodies. The two allocated mapping records remained unchanged; the fabricated address still had no mapping, and the mapping count remained 14. No per-tag account or alias was created and no forwarding loop was observed.

Two observation pitfalls mattered. The first mailbox search returned no matches for its 60-second window even though delivery logs recorded successful local delivery; a later unfiltered listing and the same original query found the messages. The internal visibility/indexing mechanism was not established, and no message was resent. Also, `/mail/message/raw` returned HTML-escaped address brackets, whereas `/mail/messages-export` preserved the original EML representation. Use the export when asserting byte-level mailbox evidence. These are harness/API considerations, not sorter/tagger corruption. Evidence: `mailbox-audit/catchall/mailbox-results-independent-reinspection.json`, `mailbox-export-independent.json`, `mailbox-export.zip`, and the narrow delivery-log excerpt. The initial failed observer result is preserved separately.

These three local mailbox deliveries are additional to the 109 SMTP sink captures; they are not included in its signature denominator. This establishes a local catch-all path on the domain-alias arrangement used here. External SMTP timing/bounce comparisons and a separate dedicated tag-domain deployment remain unapproved.

Operational settings were restored and read back: SM's configured DNS fields are empty again, DNS caching remains off, the primary domain uses its original external route, catch-all assignment is empty and disabled, and DKIM **signing is disabled**. The generated key remains stored: `domainKeysSettings.isActive` describes that key's state, independently of the signing-enabled flag. The API, shipped frontend, and persisted `enable_dkim_signing:false` agree that signing is off. This is not an exact rollback of newly created key material or synthetic aliases.

Proc remains **enabled**, matching the user's starting setting. All coordinator-owned sorter/tagger watchers and DNS/SMTP helpers are stopped; MailService and the user's visible Procmon remain running and were not restarted. Final inspection found no live messages in SM's spool, Proc, Delay, or Schedule queues and no plain inputs in the private process queue. Intentional holds, logs, 14 mappings, exported/captured messages, the synthetic private/catch-all/domain aliases, and generated key are retained as lab evidence. The original four manual-test files remain hash-identical at `C:\SmarterMail\FullSuite20260916\manual-test-originals`; they were not reinjected. Other controlled Proc artifacts are preserved outside the live queue.

The original external route still points to the now-stopped loopback sink. It is a retained lab configuration, not an operational Internet delivery route. With Proc enabled and processors stopped, new Proc mail waits for the operator. Evidence: `settings-inspect.json`, `dkim-audit/disabled-state-independent.json`, `sink-stopped.json`, and `final-queue-and-preservation.json`.

## Automated regression check

The unchanged implementation also passed `dotnet build SmTagger.slnx -c Release` with zero warnings/errors, all **398 tests** with zero failures/skips, and `dotnet format SmTagger.slnx --verify-no-changes --no-restore` during this suite. These tests do not cover the newly discovered API BOM compatibility requirement; that defect remains open.

## Remaining deployment gates

Repeat applicable checks on the actual Windows Server build, SM configuration, process identity, filesystem, and hosting arrangement. External SPF/DMARC assessment, real Gmail/Yahoo/provider delivery, supported mail clients and protected/signed/encrypted modes, actual generated MDNs, untested submission routes, downstream DSN support, and external catch-all allocation-oracle behavior are not approved by this loopback suite. No disk-loss or power-loss testing was added.
