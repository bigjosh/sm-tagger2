# Deployment assumptions and evidence

This register supports [spec.md](spec.md) and [testing-plan.md](testing-plan.md). It records external conditions the pure C# implementation relies on; it does not add runtime checks or change the specification's failure rules.

The current local rc.10 [storage layout](storage-reference.md#runtime-directory-trees) separates the fixed Proc mail workspace from sender configuration, permanent mappings, staging, and the trace in the datadir. The data volume may differ from the mail volume; each class of atomic move remains within its required local NTFS volume. The rc.9 active-index configuration and shared sender-id policies remain unchanged. Existing installations require [offline queue migration and coordinated rollback planning](queue-layout-2026-09-17.md), including earlier configuration conversion where needed. There is no runtime fallback, migration, or automatic detection. Historical tests retain the versions and paths actually exercised; they do not establish the new mailroot's live compatibility.

The version 1 implementation and local automated validation cover the scope recorded in [validation-report.md](validation-report.md). Synthetic fixtures, private local sample checks, and published-executable tests provide local evidence. [Live sequencing tests on SmarterMail build 9742 and Windows 11](live-sequencing-2026-09-16.md) record scoped producer/consumer observations; the [broader compatibility suite](live-smartermail-2026-09-16.md) adds real tagging, watcher, signing, and admission evidence. Windows Server and complete deployment/client/route gates remain **PENDING**; the local results do not approve an untested deployment. The immediate API BOM correction and successful scoped local retest are recorded under `VERSION-SENSITIVE-005` and in the [compatibility follow-up](bom-compatibility-2026-09-16.md). `VERSION-SENSITIVE-001` records both the earlier plain-HDR readiness failure and the narrower readiness contract supported by the live tests.

## Target and evidence policy

At bring-up, target the most recent stable SmarterMail release and the most recent stable Windows Server release. Record the exact installed releases and servicing builds then; “latest” alone is not an evidence identifier. The managed target is defined in [implementation.md](implementation.md), currently `net10.0`; also record the pinned SDK, runtime patch, architecture, and publication mode used for the tested release executables.

| Deployment field | Recorded value |
|---|---|
| SmarterMail version and complete build | TBD at bring-up |
| Windows Server release, edition, OS build, and update level | TBD at bring-up |
| .NET SDK/runtime, architecture, publication mode | Local release: see [validation-report.md](validation-report.md) and [artifacts/release/manifest.json](artifacts/release/manifest.json); exact deployed release and servicing level TBD at bring-up |
| Commit and hashes of `sm-sorter.exe` / `sm-tagger.exe` | Local artifact hashes: [artifacts/release/manifest.json](artifacts/release/manifest.json); deployed revision and artifact selection TBD at bring-up |
| Actual spool/Proc/mailroot, datadir, mail-volume and data-volume identities, and absence of queue junctions | TBD |
| Process identity, Task Scheduler/service wrapper and arguments | TBD |
| Sender-id, client/version/platform, submission route, relay/provider | TBD for every supported path |
| Tag domains, collection mailboxes, DNS/signing configuration | TBD for every sender |

`PENDING` means evidence has not been recorded. `VERIFIED` requires a dated result and retained evidence for the exact deployment scope. `ACCEPTED-RISK` requires an explicit owner disposition naming the unproved condition, affected paths, and consequences; it is never inferred from an old result or an example. `UNSUPPORTED` records a path that fails the contract or is disabled. Such dispositions cannot silently weaken a required behavior in the specification, including the MDN approval gate.

Keep the `VERSION-SENSITIVE-*` identifiers stable. They identify dependencies likely to change with SmarterMail, Windows/.NET, client, relay, DNS, storage, or host updates. Inline markers in the design and source code point to these entries. When an applicable component or configuration changes, mark the affected result `PENDING`, repeat its checks, and record the new scope before reasserting support. Preserve earlier evidence as history.

## Version-sensitive registry

### VERSION-SENSITIVE-001

**Queue readiness and live triggers — deployment PENDING; scoped local evidence recorded.** A plain HDR in the selected `proc` can precede the EML and can exist for a message attempt that never produces one. The sorter discovers final `.eml` files, checks the EML before accessing the matching HDR, and reads HDR without sharing write access; its read handle closes before ownership. The positive-status contract introduced in rc.7 requires exact `Written` before normal classification and retains `Failed` under the existing policy. `Writing` defers quietly while watching; every other or incomplete status defers with stderr on every encounter, without auth lookup or message mutation. Comparison trims trailing ASCII SP/HTAB only and is case-sensitive. Establish on every deployed route that a successfully read `Written` HDR with final EML means both files are complete and neither receives later producer edits. Missing/busy HDRs must remain unclaimed while watching. Final EML visibility alone is insufficient: the observed direct API route with `sendImmediately: true` creates EML before HDR. Ordinary browser Send and delayed/scheduled publication were not covered by the earlier native trace; later functional delivery checks do not establish every producer file-handle transition. Separately establish that suffixed HDRs are inert and that SmarterMail accepts externally published complete pairs in the common spool when EML arrives first and plain HDR last, including simultaneous sorter/tagger activity. Recheck after SmarterMail queue/processing changes. Test: `LAB-001`.

Observed failure, reviewed 2026-09-16: the earlier retained HDR had status `Writing`, while the later plain HDR had status `Written` and different envelope/metadata content. The sorter attempted its EML move before that file was available, then collided with its retained HDR when the later plain HDR appeared. The private captures stay outside source control and releases. These observations invalidate plain-HDR visibility as evidence of completion.

Local retest, 2026-09-16: on SmarterMail `100.0.9742.26305+420ad0abfc12ec298959057b3a7aae179d13f0e1`, all 104 traced SMTP lifecycles closed the final HDR before renaming `.em_` to `.eml`; an aborted transfer nevertheless produced final EML with `Failed` status. The traced direct API route with `sendImmediately: true` instead closed EML before creating/writing HDR, whose open writer is excluded by the sorter's existing read-sharing mode. Consumer trials repeatedly enumerated EML-only and `.hdr.pending` pairs without opening them, while active controls delivered. HDR-first caused repeated reads and missing-EML attempts, although the delayed pair eventually delivered. The resulting recommendation remains EML first, final HDR last. Full evidence and limitations are in [the sequencing report](live-sequencing-2026-09-16.md). The [broader suite](live-smartermail-2026-09-16.md) adds successful concurrent sorter/tagger publication without a new native trace of every producer operation. Windows Server and untested producer paths remain deployment gates.

A [SmarterTools developer explanation](https://portal.smartertools.com/community/a96224/archive-and-spool-overview.aspx) identifies the first HDR line as status and describes `Writing`, `Failed`, and `Quarantined` in addition to `Written`; this does not establish that `Quarantined` occurs in Proc. The vendor's [older custom-header sample](https://github.com/SmarterTools/SMCustomHeaders/blob/3c74049d6544f2ee96f965e36dec28123b05ae61/SM%20Custom%20Headers/SMCustomHeaders.cs) watches filename-renaming events and selects EML files. The 2026-09-16 policy rejected `Failed` and left other statuses opaque. On 2026-09-17 the owner approved replacing that rule with the positive `Written` gate while preserving final-EML discovery and EML-first/HDR-last publication. Saved successful SMTP and immediate/delayed/scheduled REST originals contain `Written `; failures contain `Failed `. `Ready` was not observed as a successful final status. The [contract note](written-readiness-2026-09-17.md) records the bounded evidence and required checks. Unknown statuses can remain deferred indefinitely and need manual investigation; these observations do not guarantee every route or future build.

The [Failed-spool consumer experiment](failed-spool-2026-09-16.md) separately observed build 9742 deleting `Failed` pairs returned to normal spool, without delivery in the recorded windows. The rc.6 sorter therefore retains only confirmed `UPSTREAM_FAILED` in `<datadir>\failed`, outside the entire spool tree, after claiming the HDR. This is manual retention, not a new queue. Folder creation and either move can fail locally; keep and report actual partial locations without releasing, replaying, or deleting the pair. The [retention follow-up](failed-retention-2026-09-16.md) passed three fresh local scenarios using the application-folder sorter and empty senders without profiles: post-250 disconnect and aborted DATA pairs were retained byte-for-byte, with zero captures in each 30-second window, and the subsequent normal-QUIT control delivered once. Earlier in-Proc failures remain historical evidence and are not automatically migrated. Windows Server and broader route gates remain pending.

**Owner disposition, 2026-09-16 — resolved for version 1/project scope.** The owner accepts rc.6 retention in `<datadir>\failed` as the complete project resolution for upstream `Failed` messages, including the observed post-250 disconnect case. No further SmarterMail disconnect-workaround experiment or vendor fix is required for this implementation. This disposition does not claim that SmarterMail's behavior changed or verify the wider queue/deployment gates. Historical evidence and manual disposition of retained mail remain as documented.

**Owner-confirmed isolation, 2026-09-17 — accepted for rc.10.** The owner confirms SmarterMail does not read mail inside the selected Proc tree. On that accepted basis, rc.10 places process work and Failed retention in the fixed nested mailroot defined by the storage reference; the sorter remains nonrecursive and excludes that subtree from discovery. This supersedes the earlier outside-spool placement requirement without reopening the resolved disconnect case. No fresh live isolation trial is claimed or required for this approved relocation. Revisit this version-sensitive assumption if SmarterMail's Proc handling changes; broader producer/consumer and Windows Server gates remain pending.

### VERSION-SENSITIVE-002

**Route coverage and trusted auth — PENDING.** Every enrolled submission path for every account sharing a sender-id traverses the one selected nonrecursive `proc`. SmarterMail supplies the actual authenticated account in HDR `auth:`; spoofed EML identities cannot manufacture it. Inbound/no-auth mail, unenrolled auth, and every active index follow their specified routes. Removing an index permits future sorter pass-through and is not an upstream account block; an unknown auth already in the tagger queue still fails closed. Auth metadata remains internal and is retained where downstream delivery needs it. Recheck after account, authentication, connector, relay, or SmarterMail routing changes. Test: `LAB-002`.

Local evidence: the [2026-09-16 suite](live-smartermail-2026-09-16.md) exercised authenticated SMTP tagging, unchanged inbound spoof handling, and delayed/scheduled selected-alias API delivery. The configured SMTP policy rejected a private-alias envelope while accepting the account envelope with private EML From. Immediate API diversion succeeded but tagging failed as described under `VERSION-SENSITIVE-005`. These route distinctions remain part of onboarding; actual browser clients and untested routes are not approved.

### VERSION-SENSITIVE-003

**Basename and derived-name namespace — PENDING.** SmarterMail's basename allocation does not collide with retained original names or generated `<basename>-<n>` children across `proc`, `process`, and spool during their required retention window. Concurrent publication does not reuse names or cause duplicate consumption. This includes collisions between one message's original basename and another's derived child name. Recheck after SmarterMail naming/queue changes. Test: `LAB-003`.

### VERSION-SENSITIVE-004

**Local NTFS moves, sharing, and singletons — PENDING.** Proc, the fixed mailroot and its process/failed children, and spool output share the spool's ordinary local NTFS volume without junctions. The datadir may use another ordinary local NTFS volume, with mapping staging/final mappings and auth staging/index publication each together. Managed non-replacing moves provide the required atomic visibility. The sorter holds its Proc lock; the tagger holds data then mailroot queue locks, preventing conflicts through either datadir or spool. Verify both lock boundaries, acquisition-failure cleanup, intended permissions, lazy failed-directory creation, and EML-first/HDR-last retention. Trusted installation supplies topology; production adds no volume/ACL/location/reparse-point preflight. Failed retention receives no automatic scan or cleanup. Recheck after Windows/.NET, storage/topology, identity, antivirus, or backup-agent changes. Test: `LAB-004`.

### VERSION-SENSITIVE-005

**Admitted HDR/EML grammar — deployment PENDING; rc.5 automated and scoped local live checks passed.** Captures from every supported path must conform to the [input grammar](storage-reference.md#input-grammar): verify CRLF and final HDR blank line, positional envelope fields, auth syntax, repeated metadata, supported sender fields, folding, null-path spelling, and any [permitted EML preamble](storage-reference.md#message-files). Verify ASCII A-label domains where internationalized domains are supported. Recheck after SmarterMail/client/submission-path changes. Test: `LAB-005`.

Historical failure, 2026-09-16: SmarterMail build 9742's immediate API (`sendImmediately: true`) route produced `EF BB BF` before its first EML field. The rc.4 tagger reported `Invalid EML field name` and retained the pair without publication; delayed and scheduled examples had no BOM and succeeded. The [original suite](live-smartermail-2026-09-16.md) remains failure evidence. The local 1.0.0-rc.5 implementation recognizes one exact BOM only at absolute EML byte zero and preserves it outside field ranges; HDR, body, and the other header rules remain unchanged. Its full automated suite passed 439 tests, including 41 added BOM checks. Do not infer compatibility for the existing rc.4 binaries or automatically replay held inputs.

Scoped retest, 2026-09-16: five fresh natural API/SMTP cases produced six deliveries; one separately prepared private-queue consumer fixture produced two more. All eight retained child files had the exact specified edits, unchanged bodies, and exactly one preserved prefix when present. All eight wire captures had the expected tagged From, no BOM, and one independently valid aligned DKIM signature; each wire body was the retained body plus one CRLF added by SmarterMail. The natural immediate API supplied a matching Reply-To even when the request left it empty. Leading Return-Path removal and synthesized Reply-To were therefore exercised by the prepared fixture, which does not establish producer or sorter behavior. See [the follow-up](bom-compatibility-2026-09-16.md) for exact artifacts, observation limits, and restoration evidence. This validates the tested local build and cases, not future producer/consumer releases.

Upgrade diagnosis: if `Invalid EML field name`, first-header loss, or unexpected final signing reappears, preserve the failed pair and inspect its initial bytes offline after the producer/processor is quiescent. Record the exact SmarterMail route/build and executable hashes. Check for a changed, repeated, partial, displaced, or other-encoding prefix rather than stripping it or retrying with looser parsing. Compare fresh retained input, every tagged output, and final wire headers/body; verify that the output preamble remains exactly once, supported sender fields are correctly rewritten, and final From/DKIM are valid. Recheck leading Return-Path removal, synthesized Reply-To, and 998/999-byte line boundaries. Prior sorter-only evidence showed this build consuming the prefix before SMTP delivery, but that observation does not guarantee future consumers or releases will do so.

### VERSION-SENSITIVE-006

**`notify:` and delivery notifications — PENDING.** Address-based retention of matching `notify:` lines and removal of others yields correct per-child delivery-notification behavior. Opaque values need no interpretation, and unknown retained metadata does not reintroduce recipients or alter child routing. The DSN hypothesis is not treated as established. Recheck after SmarterMail/relay notification changes. Test: `LAB-006`.

### VERSION-SENSITIVE-007

**Admission limits and capacity — PENDING.** Every enrolled route applies the intended message-size, recipient-count, and submission limits before accepting mail for delivery. Rejection need not prevent queue-file creation: a rejected SMTP DATA probe left a final `Failed` pair. Actual hardware/storage can process the admitted workload and fan-out, with operational observation of backlog and free space outside either executable. Recheck after route/limit, hardware/storage, Windows/.NET, or SmarterMail changes. Test: `LAB-007`.

The [local admission probes](live-smartermail-2026-09-16.md#admission-boundaries) recorded different domain, global, advertised SMTP, and actual rejection limits. The domain configuration was 10,240,000 bytes and 200 recipients; SMTP rejected RCPT 201 and an oversized DATA transaction, while the immediate API rejected a 10,485,761-byte plaintext body and 501 recipients. These rejections do not prove accepted DATA/delivery at the exact boundaries or production capacity.

### VERSION-SENSITIVE-008

**Final signing and authentication — PENDING.** SmarterMail signs after rewriting; a valid signature covers final `From:` with `d=` exactly equal to its domain. Independent receivers report `dkim=pass` and `dmarc=pass` for every approved final domain, route, and supported size. Changed envelope domains authorize sending hosts for SPF. Relays preserve the needed signature; unexpected earlier opaque signatures do not invalidate the required final result. Recheck after signing, DNS, relay, SmarterMail, client, or size-policy changes. Test: `LAB-008`.

The [local compatibility suite](live-smartermail-2026-09-16.md) includes cryptographic verification of real SM DKIM with the final tag domain on captured loopback deliveries. This is stronger than counting signature headers but does not establish public DNS, external SPF/DMARC results, provider acceptance, or all supported size/route combinations.

### VERSION-SENSITIVE-009

**Unprovisioned tag acceptance — PENDING.** SmarterMail and each relay/provider accept newly generated individual tags in every sender role the algorithm uses and preserve group tags in `Reply-To:`. No per-tag alias or send-as registration is needed. The private identity itself is authorized for the account's submission path. Recheck after provider, send-as, authentication, or routing-policy changes. Test: `LAB-009`.

### VERSION-SENSITIVE-010

**Catch-all routing and envelope preservation — PENDING.** Each sender has the specified reserved tag domain and catch-all collection destination. Individual tags, group tags, and replies/DSNs reach that mailbox without loops. The original SMTP envelope recipient remains operator-inspectable even when EML `To:` is absent or different. Existing tag domains remain usable for permanent mappings. Recheck after DNS, alias/catch-all, forwarding, mailbox, relay, or SmarterMail changes. Test: `LAB-010`.

Local evidence, 2026-09-16: the [compatibility suite](live-smartermail-2026-09-16.md#final-reconciliation-catch-all-and-restoration) delivered one message each to an allocated individual tag, an allocated group tag, and a fabricated address through a domain-alias catch-all. The official mailbox-export ZIP contained all three exactly once, with byte-identical submitted bodies and the original recipient in `X-Rcpt-To` despite an unrelated visible To. The allocated records stayed unchanged, the fabricated address remained unmapped, and the total remained 14 mappings. The initial mailbox search missed the deliveries for its 60-second window; later observations found the same messages without resending. These three mailbox deliveries are separate from the suite's outgoing SMTP/signature counts. A dedicated tag-domain deployment, external feedback, and complete reply/DSN/client coverage remain pending.

### VERSION-SENSITIVE-011

**Client content and protected-message compatibility — PENDING.** Approved clients preserve the intended identity behavior in compose, reply, reply-all, and forwarding. Evidence records whether configured/former private identities appear in bodies, signatures/templates, `Message-ID:`, trace/unknown headers, or embedded content outside the rewrite surface. S/MIME, PGP/MIME, and protected-header modes require their own compatible path or remain disabled/unsupported. No finding extends the program's header-only secrecy guarantee. Recheck after client/plugin/template/signature, server, or relay changes. Test: `LAB-011`.

### VERSION-SENSITIVE-012

**MDN-capable paths — PENDING; `allow-mdn=false` remains the default.** Before enabling `allow-mdn=true`, every account and every client/version/platform/submission path sharing that sender-id must have evidence that its MDNs contain no configured or known former private identity outside supported sender headers; every unapproved MDN path must be disabled or prevented from generating receipts. Adding an account to an enabled record requires extending that evidence before use. Include automatic, nonstandard, and no-auth receipts because runtime classification cannot cover every path. Former-identity inspection is an operator check; no retired-address list is loaded. Recheck after any capable client/server/provider or receipt-policy change and restart the tagger after changing the flag. Test: `LAB-012`.

### VERSION-SENSITIVE-013

**Watcher and console hosting — PENDING.** The selected Windows/.NET build and console host support reliable directory rescans, the independent watcher singletons, captured standard error, and the specified Ctrl+C/Ctrl+Break shutdown. For standalone EXEs, the actual process identity can start without companion DLLs or an installed runtime. The locally tested .NET 10.0.11 build needed no runtime-file extraction; if the selected runtime build requires extraction, its location must be writable by that identity and protected from other accounts. See [deployment.md](deployment.md#release-files). A wrapper that cannot deliver a supported console event performs a forced stop, with crash semantics. These remain console applications rather than native Windows services. Recheck after Windows/.NET, packaging, scheduler/wrapper, identity, extraction-location, or process-control changes. Test: `LAB-013`.

The [local concurrent-watcher trial](live-smartermail-2026-09-16.md) processed 45 authenticated inputs into 40 tagged and five unchanged results, with exactly 75 deliveries and unchanged permanent mapping/enrollment identities. Its owned watcher processes were stopped after accounting for their work; this does not establish service-wrapper console-event behavior or Windows Server compatibility.

The local rc.6 standalone sorter was initially blocked before startup by Windows Application Control, including a representative retry. That full suite recorded 441 passed and 14 failed, with zero skips; all failures were pre-start launch blocks. After the user disabled Smart App Control, the same unchanged binaries passed all 455 tests with zero failures/skips, including standalone launch cases. Readback confirmed Smart App Control was off; no rebuild, code, certificate, or security-policy change was made by the testing agent. **The local launch blocker is closed as of 2026-09-16.** Support with Smart App Control enabled is outside the verified scope, not an active blocker for this host. Application-folder tests, including watcher retention/restart, and the earlier three live scenarios passed; no additional SM live trial accompanied the policy retest. The [retention follow-up](failed-retention-2026-09-16.md) preserves both runs. Recheck actual launch permission for the selected artifact, host policy, and process identity; this local success does not approve an untested deployment.

### VERSION-SENSITIVE-014

**Catch-all allocation feedback — PENDING.** External SMTP responses, bounces, timing, and other available feedback do not expose whether a probed tag is allocated. The accepted five-character/25-bit attribution tradeoff depends on this condition and the expected 100–5,000 mappings per sender. Reopen that decision if feedback reveals allocation or the mapping population materially exceeds the range. Recheck after catch-all, filtering, relay, SmarterMail, or provider changes. Test: `LAB-014`.

## Operator assumptions

### ASSUMPTION-PRIVATE-PROVISIONING

**PENDING per sender-id and every enrolled account.** Record that the shared private-address was generated using a cryptographically secure source, is non-public, and is an authorized sending identity on each account. Record the method/date and sender-id without spreading the private identity outside protected operational records. The program validates syntax and uniqueness, not the original generation method or a numeric entropy minimum. Repeat onboarding for added accounts and identity changes, preserving stable sender IDs and mappings. Explicitly coordinate index removal and private-address replacement with all affected clients and queued mail: neither operation creates a retired-identity rejection rule.

### ASSUMPTION-TEMPLATE-LENGTH

**ACCEPTED — explicit owner decision, 2026-09-05.** The operator will provision templates that never generate an overlong address. Address length is a trusted configuration assumption; do not add a runtime address-length gate or a per-message length recheck for it. Existing template syntax and usable-filename validation still apply. Record the approved template/domain during onboarding and reconsider this assumption when they change. The distinct runtime check on edited and synthesized physical EML header lines is defined in [spec.md §8.1](spec.md#81-edited-and-synthesized-header-line-length); it does not change this address-length assumption.

The specification also expressly accepts case-insensitive address identity, catch-all spam and limited attribution, sequential partial publication, no automatic recovery, unredacted protected diagnostics, best-effort logging, and process-crash-only durability. These are design boundaries, not claims of empirical verification. Backup/reconciliation and residual-file disposition remain offline operator responsibilities. Deferred behavior stays in [future.md](future.md).

## Result record

Add one record per tested deployment/path and assumption, retaining failures and superseded results:

```text
Assumption ID(s):
Status: PENDING | VERIFIED | ACCEPTED-RISK | UNSUPPORTED
Date / operator / approver:
Sender-id and complete client/submission/relay scope:
SmarterMail / Windows Server / .NET / executable hashes:
Relevant paths, host configuration, DNS/signing/catch-all snapshot:
Test case(s), expected outcome, actual outcome:
Protected evidence location (raw HDR/EML, capture, logs, hashes):
Limitations or explicit risk disposition:
Changes that invalidate this result:
```

Production deployment evidence locations and completed records are **TBD**. The linked local reports record their narrower tested scopes. Keep original byte fixtures and unsanitized evidence inside the protected operator boundary; any shareable examples are separate sanitized copies.
