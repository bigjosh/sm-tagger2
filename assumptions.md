# Deployment assumptions and evidence

This register supports [spec.md](spec.md) and [testing-plan.md](testing-plan.md). It records external conditions the pure C# implementation relies on; it does not add runtime checks or change the specification's failure rules.

The version 1 implementation and local automated validation are complete for the scope recorded in [validation-report.md](validation-report.md). Synthetic fixtures, private local sample checks, and published-executable tests provide local evidence. All external deployment checks below remain **PENDING**; no SmarterMail/Windows Server installation, client, or route is claimed to have passed its bring-up gates.

## Target and evidence policy

At bring-up, target the most recent stable SmarterMail release and the most recent stable Windows Server release. Record the exact installed releases and servicing builds then; “latest” alone is not an evidence identifier. The managed target is defined in [implementation.md](implementation.md), currently `net10.0`; also record the pinned SDK, runtime patch, architecture, and publication mode used for the tested release executables.

| Deployment field | Recorded value |
|---|---|
| SmarterMail version and complete build | TBD at bring-up |
| Windows Server release, edition, OS build, and update level | TBD at bring-up |
| .NET SDK/runtime, architecture, publication mode | Local release: see [validation-report.md](validation-report.md) and [artifacts/release/manifest.json](artifacts/release/manifest.json); exact deployed release and servicing level TBD at bring-up |
| Commit and hashes of `sm-sorter.exe` / `sm-tagger.exe` | Local artifact hashes: [artifacts/release/manifest.json](artifacts/release/manifest.json); deployed revision and artifact selection TBD at bring-up |
| Actual `spooldir`, `proc`, datadir, `process`, NTFS volume | TBD |
| Process identity, Task Scheduler/service wrapper and arguments | TBD |
| Sender-id, client/version/platform, submission route, relay/provider | TBD for every supported path |
| Tag domains, collection mailboxes, DNS/signing configuration | TBD for every sender |

`PENDING` means evidence has not been recorded. `VERIFIED` requires a dated result and retained evidence for the exact deployment scope. `ACCEPTED-RISK` requires an explicit owner disposition naming the unproved condition, affected paths, and consequences; it is never inferred from an old result or an example. `UNSUPPORTED` records a path that fails the contract or is disabled. Such dispositions cannot silently weaken a required behavior in the specification, including the MDN approval gate.

Keep the `VERSION-SENSITIVE-*` identifiers stable. They identify dependencies likely to change with SmarterMail, Windows/.NET, client, relay, DNS, storage, or host updates. Inline markers in the design and source code point to these entries. When an applicable component or configuration changes, mark the affected result `PENDING`, repeat its checks, and record the new scope before reasserting support. Preserve earlier evidence as history.

## Version-sensitive registry

### VERSION-SENSITIVE-001

**Queue readiness and live triggers — PENDING.** A plain HDR in the selected `proc` is complete and its EML is already complete; a suffixed HDR is inert. SmarterMail accepts externally published complete pairs in the common spool when EML arrives first and plain HDR last, including simultaneous sorter/tagger activity. Recheck after SmarterMail queue/processing changes. Test: `LAB-001`.

### VERSION-SENSITIVE-002

**Route coverage and trusted auth — PENDING.** Every enrolled submission path traverses the one selected nonrecursive `proc`. SmarterMail supplies the actual authenticated account in HDR `auth:`; spoofed EML identities cannot manufacture it. Inbound/no-auth mail, non-enrolled auth, current enrollment, and retired enrollment follow their specified routes. Auth metadata remains internal and is retained where downstream delivery needs it. Recheck after account, authentication, connector, relay, or SmarterMail routing changes. Test: `LAB-002`.

### VERSION-SENSITIVE-003

**Basename and derived-name namespace — PENDING.** SmarterMail's basename allocation does not collide with retained original names or generated `<basename>-<n>` children across `proc`, `process`, and spool during their required retention window. Concurrent publication does not reuse names or cause duplicate consumption. This includes collisions between one message's original basename and another's derived child name. Recheck after SmarterMail naming/queue changes. Test: `LAB-003`.

### VERSION-SENSITIVE-004

**Local NTFS moves, sharing, and singletons — PENDING.** The actual roots share one ordinary local NTFS volume. Managed non-replacing file/directory moves provide the required atomic visibility there; held lock streams exclude a second same-role process. The process identity and other software accessing the spool permit the required operations. Trusted installation supplies this condition; production adds no volume/ACL/reparse-point preflight. Recheck after Windows/.NET, storage/topology, identity, antivirus, or backup-agent changes. Test: `LAB-004`.

### VERSION-SENSITIVE-005

**Admitted HDR/EML grammar — PENDING.** Captures from every supported path conform to the bounded parser contract: CRLF and final HDR blank line, positional envelope fields, auth syntax, repeated metadata, supported sender fields, folding, and null-path spelling. Internationalized domains arrive as ASCII A-labels where supported. Recheck after SmarterMail/client/submission-path changes. Test: `LAB-005`.

### VERSION-SENSITIVE-006

**`notify:` and delivery notifications — PENDING.** Address-based retention of matching `notify:` lines and removal of others yields correct per-child delivery-notification behavior. Opaque values need no interpretation, and unknown retained metadata does not reintroduce recipients or alter child routing. The DSN hypothesis is not treated as established. Recheck after SmarterMail/relay notification changes. Test: `LAB-006`.

### VERSION-SENSITIVE-007

**Admission limits and capacity — PENDING.** Every enrolled route applies the intended message-size, recipient-count, and submission limits before queue admission. Actual hardware/storage can process the admitted workload and fan-out, with operational observation of backlog and free space outside either executable. Recheck after route/limit, hardware/storage, Windows/.NET, or SmarterMail changes. Test: `LAB-007`.

### VERSION-SENSITIVE-008

**Final signing and authentication — PENDING.** SmarterMail signs after rewriting; a valid signature covers final `From:` with `d=` exactly equal to its domain. Independent receivers report `dkim=pass` and `dmarc=pass` for every approved final domain, route, and supported size. Changed envelope domains authorize sending hosts for SPF. Relays preserve the needed signature; unexpected earlier opaque signatures do not invalidate the required final result. Recheck after signing, DNS, relay, SmarterMail, client, or size-policy changes. Test: `LAB-008`.

### VERSION-SENSITIVE-009

**Unprovisioned tag acceptance — PENDING.** SmarterMail and each relay/provider accept newly generated individual tags in every sender role the algorithm uses and preserve group tags in `Reply-To:`. No per-tag alias or send-as registration is needed. The private identity itself is authorized for the account's submission path. Recheck after provider, send-as, authentication, or routing-policy changes. Test: `LAB-009`.

### VERSION-SENSITIVE-010

**Catch-all routing and envelope preservation — PENDING.** Each sender has the specified reserved tag domain and catch-all collection destination. Individual tags, group tags, and replies/DSNs reach that mailbox without loops. The original SMTP envelope recipient remains operator-inspectable even when EML `To:` is absent or different. Existing tag domains remain usable for permanent mappings. Recheck after DNS, alias/catch-all, forwarding, mailbox, relay, or SmarterMail changes. Test: `LAB-010`.

### VERSION-SENSITIVE-011

**Client content and protected-message compatibility — PENDING.** Approved clients preserve the intended identity behavior in compose, reply, reply-all, and forwarding. Evidence records whether current/retired private identities appear in bodies, signatures/templates, `Message-ID:`, trace/unknown headers, or embedded content outside the rewrite surface. S/MIME, PGP/MIME, and protected-header modes require their own compatible path or remain disabled/unsupported. No finding extends the program's header-only secrecy guarantee. Recheck after client/plugin/template/signature, server, or relay changes. Test: `LAB-011`.

### VERSION-SENSITIVE-012

**MDN-capable paths — PENDING; `allow-mdn=false` remains the default.** Before enabling `allow-mdn=true`, every client/version/platform/submission path using that sender-id must have evidence that its MDNs contain no current or retired private identity outside supported sender headers; every unapproved MDN path must be disabled or prevented from generating receipts. Include automatic, nonstandard, and no-auth receipts because runtime classification cannot cover every path. Recheck after any capable client/server/provider or receipt-policy change and restart the tagger after changing the flag. Test: `LAB-012`.

### VERSION-SENSITIVE-013

**Watcher and console hosting — PENDING.** The selected Windows/.NET build and console host support reliable directory rescans, the independent watcher singletons, captured standard error, and the specified Ctrl+C/Ctrl+Break shutdown. For standalone EXEs, the actual process identity can start without companion DLLs or an installed runtime. The locally tested .NET 10.0.11 build needed no runtime-file extraction; if the selected runtime build requires extraction, its location must be writable by that identity and protected from other accounts. See [deployment.md](deployment.md#release-files). A wrapper that cannot deliver a supported console event performs a forced stop, with crash semantics. These remain console applications rather than native Windows services. Recheck after Windows/.NET, packaging, scheduler/wrapper, identity, extraction-location, or process-control changes. Test: `LAB-013`.

### VERSION-SENSITIVE-014

**Catch-all allocation feedback — PENDING.** External SMTP responses, bounces, timing, and other available feedback do not expose whether a probed tag is allocated. The accepted five-character/25-bit attribution tradeoff depends on this condition and the expected 100–5,000 mappings per sender. Reopen that decision if feedback reveals allocation or the mapping population materially exceeds the range. Recheck after catch-all, filtering, relay, SmarterMail, or provider changes. Test: `LAB-014`.

## Operator assumptions

### ASSUMPTION-PRIVATE-PROVISIONING

**PENDING per sender.** Record that the private-address was generated using a cryptographically secure source, is non-public, and is an authorized sending identity on the account. Record the method/date and sender-id without spreading the private identity outside protected operational records. The program validates syntax and uniqueness, not the original generation method or a numeric entropy minimum. Repeat onboarding records for rotations and preserve permanent auth indexes and retired identities as specified.

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

Evidence locations and completed records are **TBD**. Keep original byte fixtures and unsanitized evidence inside the protected operator boundary; any shareable examples are separate sanitized copies.
