# sm-sorter / sm-tagger — Behavioral Specification

This document defines **what** version 1 must do and **why** those behaviors exist. It incorporates [storage-reference.md](storage-reference.md) as the normative directory and file-format contract; those definitions live there rather than being duplicated here. It intentionally does not prescribe classes, APIs, operating-system calls, data structures, or test seams. [`implementation.md`](implementation.md) defines **how** the current implementation satisfies this contract. If the implementation guide conflicts with this specification or its incorporated storage reference, the guide must be corrected.

All addresses, hostnames, and IP addresses in the examples are sanitised: `example.com` is the sender's own domain, while `example.net` and `example.org` stand in for correspondents.

External client, SmarterMail, provider, and deployment behaviors on which this design depends are tracked in `assumptions.md`. A client/version/path is supported for enrolled accounts only after its applicable empirical assumptions have current evidence or an explicit accepted-risk disposition. Implementation choices cannot silently weaken the behavior, security boundary, or failure outcomes defined here.

---

## 1. What this system does

A two-process outbound-mail rewriter for SmarterMail. `sm-sorter.exe` is the small, failure-isolated classifier at the SmarterMail queue boundary: it reads only enough of each HDR to prove that mail is unrelated and pass it through unchanged, or diverts mail from an enrolled authenticated address into the tagger queue. `sm-tagger.exe` consumes only that private queue and performs the full parsing, mapping, fan-out, rewriting, and publication work. For a qualifying message, it replaces every supported sender-identity occurrence of that account's **private-address** with a **tag-address** that is unique to the recipient and remembers the mapping.

This split deliberately limits failure propagation. If the tagger is stopped, crashed, or unable to validate its configuration, enrolled mail accumulates in `<mailroot>\process\` while positively classified no-auth and non-enrolled mail continues from SmarterMail `proc\` to `spool\`. The sorter remains a critical singleton: if the sorter itself or its watcher stops, all mail through the selected `proc\` stops. Shared-volume exhaustion and other host-wide failures can still affect both paths, so this is logical failure isolation rather than complete resource isolation.

Alice receives mail using `joe-a3f92@example.com`; Bob receives mail using `joe-c17b4@example.com`. If mail later arrives at the allocated `joe-a3f92@example.com`, its exact tag record associates that address with Alice's recipient-id. That is useful attribution evidence that the tag escaped its intended path, not proof that Alice personally disclosed it (§1.4).

Three properties make it work:

- **One tag-address per person, forever.** The same recipient always gets the same tag, so it is stable enough to live in their address book.
- **Only the address changes.** The display name in `From:` is passed through byte-for-byte, so the user can set or change it freely in their mail client and this program neither knows nor cares.
- **The private-address is removed from the defined sender-identity surface.** On every successfully tagged output, no parsed addr-spec equal to the current private-address remains in the supported sender fields listed in §8. Content outside that explicit surface is preserved and is not claimed to be secret-free.

Version 1 independently enforces one sorter for the selected spool `proc\` and one tagger for both its selected mail queue and its datadir/mapping set (§11). The tagger holds a data lock and a separate mail-queue lock. The sorter never waits on either tagger lock or configuration validation. A one-shot invocation cannot run beside a corresponding watcher sharing either protected resource, but the sorter and tagger are expected to run concurrently. `<mailroot>` means the fixed workspace under Proc defined in the storage reference.

### 1.1 Messages with multiple recipients

These messages require special handling because each recipient should receive the tag-address associated with their recipient address.

To ensure this, messages with several recipients are **split into one message per recipient** before tagging. Every tagged message must have exactly one EML `From:` field containing exactly one mailbox, regardless of recipient count or `Reply-To:` handling.

When the message has no `Reply-To:`, we generate an additional stable tag-address for the complete recipient set and add it as `Reply-To:`. When an existing valid `Reply-To:` contains the private-address, its matching mailboxes become that same group tag while deliberate non-matching mailboxes are preserved. An existing `Reply-To:` with no private-address is preserved byte-for-byte and suppresses group-tag creation.

This is a best-effort reduction in accidental disclosure by ordinary reply-all clients, not a guarantee about every client, manual reply, forward, quotation, or downstream transformation. A group tag is evidence for its complete recipient set only (§1.4); it cannot identify one member as the source of a disclosure.

### 1.2 Authenticated enrollment and the private-address

An account opts in through an auth-index directory pointing to a sender-id record (§5), identified permanently by an opaque **sender-id**. Several authenticated accounts may point to the same record and therefore share **the private-address that their clients use as a sender identity**, template, MDN policy, and permanent recipient tags. The private-address is deliberately *not* an everyday address. It is a non-public, randomly generated string like `k7p2qx@example.com`, configured in the clients. Generate it from a cryptographically secure random source and record that fact during onboarding; version 1 validates its addr-spec and uniqueness but cannot infer how it was originally generated and imposes no numeric entropy minimum.

The `auth:` value selects the sender-id record through its published index but is never rewritten. An auth address may also be the private-address of that same sender-id; address ownership must still be unique across different sender IDs. If the configured private-address occurs in at least one supported sender-identity field, tagging is activated and every exact parsed sender-identity occurrence of that private-address is replaced. Authentication alone never activates tagging. Legitimate non-matching sender identities are preserved. If the private-address occurs in no supported sender-identity field, the message passes through unchanged and no tag is created. Former private addresses have no special runtime meaning: they are ordinary non-matches after replacement (§7.3).

Consequences worth understanding before building:

- **`auth:` is the trusted outbound classifier and sender-id record lookup key.** The sorter reads it but never modifies it. Inbound mail normally has no `auth:` and therefore cannot enter the tagger queue merely by spoofing the private-address.
- **The sender-id, not an email address, is the persistent identity.** It is an opaque operator-chosen name under the [storage reference's identifier rules](storage-reference.md#identifiers-and-addresses), with no UUID requirement. Recipient tag mappings are keyed by its exact value, so changing the auth-address or rotating the private-address does not change existing tags. Keep established sender IDs unchanged.
- **Address identity is ASCII case-insensitive throughout version 1.** Complete addr-specs are canonicalized to lowercase for comparison and mapping (§7.1.1), while original message spelling is preserved except at explicit rewrite sites.
- **The auth index is the enrollment authority.** Every published auth directory points to exactly one existing sender-id record. Several distinct canonical auth directories may point to the same record; no primary account or reverse auth list exists. A sender-id record may have zero auth indexes, preserving existing mappings without enrolling an account. Directory and pointer formats are defined in the storage reference.
- **An authenticated account may still send other identities.** A configured `auth:` with no private-address occurrence in a supported sender-identity field is an ordinary pass-through message.
- **A partial match is allowed.** For example, the EML `From:` may use the private-address while the envelope sender uses a different address. Replace the matching `From:` and preserve the non-matching envelope sender.
- **The trigger must be a sendable identity on every enrolled account sharing the sender-id** — normally an alias, and for a provider "send as" relay it needs that provider's verification. Confirm this before choosing the string.
- **Mail authentication is a bring-up gate, not a local parser guarantee.** The private-address and generated tag-address may use different domains, but every final tag domain and submission route must pass the DKIM/SPF/DMARC checks in §8 before the sender is enabled. Repeat them after any relevant client, SmarterMail, DNS, relay, signing, size-limit, or routing change.
- **Version 1 does not provision one alias per tag.** Each sender route must prove during bring-up that SmarterMail and every downstream relay accept freshly generated, never-provisioned individual and group tags in every role where the algorithm or an approved client path can place them. A route that requires per-local-part alias or send-as verification is unsupported.
- **Accounts and private-addresses can change without changing identity.** Keep the sender-id and its mappings unchanged. With affected submissions paused and the tagger stopped, publish or remove auth indexes deliberately, update the private-address if needed, restart the tagger, and only then use the new client identity. No former-auth or former-private list is loaded. An old private address may consequently pass through unchanged; the operator must coordinate every affected client and queued message.
- **Tagger sender configuration takes effect only at startup in version 1.** The tagger does not watch or reread sender-id records while running. The sorter checks the canonical auth-address directory on each classified message, so atomically publishing that directory immediately enables diversion, not successful tagging. Stop the tagger before changing a sender-id record, publish every required auth index entry only after its pointer file is complete, restart the tagger, and only then let the client use the new identity. A queue item not represented consistently in the configuration effective for that invocation fails closed; it is never passed merely because the tagger does not recognize it.
- **Every published auth index is active.** Removing an index makes later sorter lookups of that auth treat it as unenrolled and pass safely classified mail unchanged. This is not a fail-closed account disable: disable submissions upstream if that is required. A pair already in the tagger queue whose auth is absent from the next startup snapshot fails closed. Stop affected submissions and reconcile queued mail before removing an index; do not delete its sender-id record or permanent mappings.
- **Outbound MDN responses are disabled per sender-id by default policy.** Set that sender-id record's `allow-mdn.txt` to `true` only after the empirical MDN checks in `testing-plan.md` are recorded in `assumptions.md`. Because the flag is shared, every account and every client/version/platform/submission path capable of using any auth index for that sender-id must be approved, or every unapproved path must be disabled or prevented from generating MDNs. Adding an account requires extending that evidence before using an already-enabled policy. The flag is enforced for recognized MDNs, but its value takes effect only at startup: changing it requires a restart in version 1.

### 1.3 Version 1 inbound scope and catch-all prerequisite

Version 1 performs **no inbound tag lookup, recipient transformation, automatic attribution, blocking, or rerouting**. The sorter sends a safely classified no-auth message directly from `proc\` to `spool\` byte-for-byte even when an envelope recipient is a known tag-address. Automatic inbound processing is deferred to FUT-002 in `future.md`.

Instead, every sender uses a dedicated tag-address domain or subdomain, such as `reply.example.com`, configured in SmarterMail with a catch-all that accepts every generated tag-address and delivers it to that user's intended collection mailbox. The domain is reserved for that user's tags so generated addresses cannot collide with ordinary mailboxes or aliases. This catch-all is an external deployment prerequisite, not something `sm-tagger` creates or manages.

Before enabling a sender-id record, perform an end-to-end delivery check proving that a representative generated-style address at its tag domain:

1. is accepted by SmarterMail;
2. reaches the intended collection mailbox;
3. retains the original SMTP envelope recipient in data the operator can inspect, even when the visible EML `To:` header is absent or different; and
4. does not enter a forwarding or processing loop.

Replies and DSNs to tag-addresses depend on this catch-all behavior. Version 1 attribution is manual: the operator obtains the original envelope recipient and looks up the exact `tag-addresses\<tag-address>\recipient-id.txt` record. The accepted tradeoff is that catch-all domains also accept fabricated or unallocated local parts and therefore receive additional spam; version 1 does not distinguish or reject them automatically.

### 1.4 Secrecy and attribution boundary

The version 1 private-address secrecy guarantee is deliberately scoped and testable: after successful tagging, the current private-address does not remain as a parsed addr-spec in any supported sender-identity field listed in §8. The program does not claim that the private-address is absent from message bodies, arbitrary or unknown headers, trace fields, embedded messages, encoded content, or other regions outside that rewrite surface. Those bytes are preserved rather than searched, decoded, rewritten, or rejected.

Version 1 does not parse or rewrite MIME bodies, including the human-readable, `message/disposition-notification`, and returned-message parts of a read receipt/MDN. When `allow-mdn.txt` is `false`, it classifies standard MDNs from the top-level EML `Content-Type:` (§6.4) and a recognized outbound MDN fails closed before tag lookup or publication—even if no private-address appears in a supported header. When it is `true`, Content-Type classification is unnecessary and the message follows the ordinary header-only trigger and rewrite rules; the flag does not extend the secrecy surface or make body content safe. Enabling it is an operator assertion that current evidence for every MDN-capable path sharing that sender-id record found no configured or known former private-address outside the supported header surface, with any unapproved path disabled. Read receipts must also remain disabled in the client/server until that evidence exists, because nonstandard or no-auth MDNs may not be attributable to a sender-id record at runtime. Full MIME-aware MDN rewriting is deferred to FUT-003 in `future.md`.

Tag attribution has three levels:

- **Allocated individual tag:** the tag record is evidence that the address was assigned to the stored individual recipient-id. Receipt of mail at the tag shows that the address became available somewhere beyond its intended use, but does not prove who disclosed it. Forwarding, account or provider compromise, shared systems, logs, and guessing remain possible alternative explanations.
- **Allocated group tag:** the record associates the address with the complete stored recipient set only. It cannot attribute disclosure to one member of that set.
- **Unallocated or fabricated catch-all address:** no tag record exists, so the message has no tag-based attribution value.

The program does not authenticate the human source of inbound mail and does not make a finding of culpability. Version 1 deliberately retains the five-character, 25-bit token in §7.1.4 for the expected 100–5,000 live mappings per sender. This accepts a small but nonzero chance that blind mail to a random catch-all address lands on an allocated tag and creates misleading attribution evidence. The decision relies on the deployed catch-all giving the sender no observable allocated-versus-unallocated address-validity signal. Re-open the entropy decision before a sender materially exceeds 5,000 live mappings or if any SMTP, bounce, timing, or other feedback reveals whether a probed address was allocated.

---

## 2. Scope and system boundaries

Version 1 runs on Windows beside SmarterMail as two console executables, `sm-sorter.exe` and `sm-tagger.exe`. The language, runtime, libraries, and platform operations are implementation choices documented in `implementation.md`.

Spool messages use CRLF line endings. Every byte outside an explicitly defined edit must be preserved exactly.

### 2.1 Resource/admission boundary

SmarterMail is the version 1 admission-policy boundary for message size, recipient count, and related submission limits before a pair appears in the configured `proc\` queue. This must be established for every enrolled submission path under §3.2 and `assumptions.md`.

**VERSION-SENSITIVE-007:** Submission-path limits and signing/relay size behavior must be rechecked after relevant SmarterMail, client, or relay updates. Trusted template-generated address lengths are recorded separately as `ASSUMPTION-TEMPLATE-LENGTH` in `assumptions.md`.

Neither executable adds an independent message-size, recipient-count, backlog, fan-out, free-space, or per-message time limit. Resource exhaustion is discovered when required work fails. Such a failure must not expose incomplete output as live mail and follows the retained-state rules in §9.

This trust in upstream limits does not permit arithmetic overflow, invalid memory access, incomplete writes, or unbounded call-stack growth. If admitted input cannot be represented or processed safely, the applicable startup or message operation fails. Capacity monitoring remains outside version 1 and is deferred to FUT-005.

### 2.2 Testability outcome

Parsing, classification, mapping, rewriting, ordering, and state decisions must be deterministically testable. The real Windows filesystem, discovery behavior, and release executables must also be testable as integrated boundaries. Production operation must expose no route by which message data, configuration, or command-line input can select fake time, randomness, faults, or storage. The internal organization and test seams used to achieve those outcomes belong in `implementation.md`.

---

## 3. Invocation

**SmarterMail invokes neither executable.** Version 1 uses two console executables run by hand or hosted as independent long-running processes by Task Scheduler or an external service wrapper. Neither is a native Windows Service Control Manager service; that integration is deferred to FUT-004 in `future.md`. Each file watcher is inside its executable rather than a launcher for per-message processes.

```
sm-sorter.exe <datadir> [-l <logfile>] [-v] [-c] <spooldir> [<basename>]
sm-tagger.exe <datadir> [-l <logfile>] [-v] [-c] [-keep] <spooldir> [<basename>]
```

| arg | meaning |
|---|---|
| `<datadir>` | root of the auth index, stable sender-id records, tag database, and mapping staging (§5); mail working state is selected by spooldir |
| `<spooldir>` | the SmarterMail spool folder. The sorter input is `<spooldir>\proc\`; both executables publish final mail into `<spooldir>\` |
| sorter `<basename>` | **optional.** Given, sort that one plain pair from `proc\` and exit. Omitted, watch `proc\` |
| tagger `<basename>` | **optional.** Given, handle that one plain pair from `<mailroot>\process\` and exit. Omitted, watch `process\` |
| `-l <logfile>` | append best-effort logging to the selected file: sorter terminal message results, or the tagger detailed execution trace (§5.1) |
| `-v` | write verbose debugging to standard output: sorter lifecycle/routing events, or tagger execution-trace events (§5.1) |
| `-c` | write concise startup information and one standard-output line after each complete output-pair move (§5.1); does not change file logging |
| `-keep` | tagger only: retain copies of original and resultant files in `process\` as `.in` and `.out` |

Each program's options must appear directly after `<datadir>`, before `<spooldir>`, to avoid confusion with basenames that may start with a dash. For either program, `-l <logfile>`, `-v`, and `-c` are independent; the tagger also accepts `-keep`. Supplying both `-v` and `-c` emits both outputs, without precedence or suppression. Options may appear in any order in that interval and each at most once. The log path is required after `-l`; a relative path resolves against the process working directory. The former tagger `-log` option is rejected with usage hints; upgrade launch commands to name a file explicitly. The sorter has its own small diagnostic writer and no dependency on the tagger's execution trace, full EML parser, tag mapping, or sender-id record loader. Its retained HDR suffix, best-effort parent `.sort.err`, and standard-error diagnostics remain available without any sorter output option.

An invalid argument count or other argument-validation error prints a concise error, one usage block, argument descriptions, and quoted-path examples to standard error, then exits with status 1 before acquiring a singleton, opening a requested log, or touching queue files. The hints explain that omitting `<basename>` starts watch mode and show the applicable option order.

A command-line `<basename>` is one nonempty literal Windows filename component, not a path; leading hyphens are permitted. A value containing a separator, rooted/drive syntax, a reserved or otherwise unusable component spelling, or `.`/`..` is an invocation failure before any queue message is touched. Watch mode obtains basenames only from direct-directory enumeration.

`-keep` accumulates files in `process\` that must be manually cleared.

For either executable, the presence of `<basename>` selects one-shot mode; omission selects watch mode. One-shot mode follows that executable's normal input and state rules and is not a repair/retry command. It never resumes a suffixed sorter or tagger state.

Without `-l`, neither program creates or opens an execution/email log; the tagger no longer has an implicit `<datadir>\log.txt`. Without `-v`, neither program emits debug output; without `-c`, neither emits concise output. Standard output is quiet when neither console flag is supplied. Per-mapping tag logs remain independent of all three flags. The operator selects dedicated log files separate from each other, mail queues, configuration, and mapping records, and creates their parent directories. Prefer absolute paths. The sorter has no implicit dependency on the tagger trace; failure of one output cannot stop the other program.

A sorter singleton/watcher failure returns nonzero and stops all flow through the selected `proc\`. A failure confined to one sorter-owned message leaves that message inert and does not stop the sorter from handling later EML candidates. Tagger lock/configuration startup failure or fatal watcher failure returns nonzero but does not affect the independent sorter; enrolled mail remains in `process\` while unrelated mail continues. Logging failures print to standard error and do not stop either process, hold the current message, or change its exit status. One-shot mode returns nonzero when its requested message is not ready or does not complete successfully for a mail-processing or contract reason.

### 3.1 Watch mode

Entered by omitting that executable's `<basename>`. The sorter watches `proc\` for regular files whose final extension is exactly `.eml`; the tagger watches `<mailroot>\process\` for regular files whose final extension is exactly `.hdr`, both compared ASCII case-insensitively. Further-suffixed files are excluded from discovery. A sorter-selected EML still needs its matching plain HDR before ownership (§4).

The persistent lock files defined in the [storage reference](storage-reference.md#locks) are not messages. The sorter holds its Proc lock; the tagger holds both its data and mail-queue locks for its complete invocation (§11).

Sorter watch mode discovers final plain EML files in `proc\`; tagger watch mode discovers plain HDR files in `process\`. Both discover eligible backlog at startup and remain correct when filesystem notifications are missed, coalesced, reordered, duplicated, or overflowed. While otherwise idle, an eligible input must be reconsidered within 30 seconds even if no useful notification arrives. The notification and rescan mechanism that provides these outcomes belongs in `implementation.md`.

Each scan sorts current basenames by ordinal comparison and processes them sequentially. A sorter-selected EML or matching plain HDR that disappears before ownership is stale and skipped; the tagger likewise skips a vanished plain HDR before its claim. Once ownership succeeds, a missing EML is a message failure rather than a reason to wait. Enumeration or monitoring failure that prevents reliable continued discovery is fatal to that executable; a message-local failure is not.

A plain HDR in SmarterMail's `proc\` is not a sorter trigger: it can appear before the EML and may exist for an attempt that never produces one. The sorter selects final `.eml` files, checks the EML before any HDR access, then reads the matching same-basename HDR without sharing write access. Only exact `Written` permits classification and routing; `Failed` takes the existing retention path, `Writing` defers, and every other or incomplete status defers with an unexpected-status diagnostic (§7.3.1). Final EML visibility alone does not establish readiness. At the tagger boundary, the sorter's EML-first/HDR-last handoff establishes completeness for a plain HDR in `process\`.

Ctrl+C or Ctrl+Break requests orderly shutdown. If idle, the program stops without claiming another message. If a message is already owned, it completes that message's normal success or message-local failure path, claims no next basename, releases its singleton, and returns 0. A fatal failure encountered while completing or shutting down still takes precedence and returns nonzero. Forced termination follows the crash rules in §9 and does not stop the other executable. The notification and cancellation mechanics that provide this behavior are defined in `implementation.md`.

### 3.2 SmarterMail deployment contract

Version 1 supports an operator-approved SmarterMail deployment whose relevant version, routes, queue behavior, and storage assumptions have passed the evidence gates in `assumptions.md` and `testing-plan.md`. It does not infer compatibility with an untested deployment.

The target is the most recent generally available production release of SmarterMail and Windows Server at bring-up, with current servicing updates. Record the exact tested builds, .NET runtime/SDK, clients, and relay routes in `assumptions.md`. `VERSION-SENSITIVE-*` markers identify dependencies likely to change with product or platform updates; recheck the affected assumptions and tests before enabling an updated deployment. Targeting current releases does not automatically establish compatibility with every later release.

**VERSION-SENSITIVE-001, VERSION-SENSITIVE-002, VERSION-SENSITIVE-003:** The queue trigger, readiness, authenticated-route coverage, and original/derived basename namespace are deployment contracts, not stable proprietary APIs.

One sorter watches exactly that one nonrecursive `proc\`, publishes safe pass-through mail into its parent `spooldir`, and diverts enrolled mail into the one process queue. One tagger watches exactly that nonrecursive process queue and publishes unchanged or transformed outputs into the same `spooldir`. Neither discovers alternate queues or subspools. A second sorter for that `proc\`, a second tagger for that datadir, or an enrolled route that bypasses the selected `proc\` is unsupported.

Neither executable discovers SmarterMail routes, alternate queues, signing configuration, or catch-all behavior. The operator must establish that every intended authenticated path enters the selected `proc\`, enrolled auth reaches `process\`, and sorter/tagger publication into the common spool works as assumed.

Approval includes the applicable tests for queue triggers, EML/HDR readiness, basename uniqueness, same-volume moves, admitted HDR/EML grammar, limits, post-tagger signing, catch-all routing, and final delivery. Record evidence for the actual environment.

A change that can affect one of those assumptions requires the affected evidence to be repeated before support is reasserted. If route coverage or the one-queue topology cannot be established, the deployment is unsupported; version 1 intentionally has no runtime mechanism that can prove them.

---

## 4. Spool layout and the suffix rule

The normative [runtime trees](storage-reference.md#runtime-directory-trees), [readers/writers](storage-reference.md#readers-writers-and-ownership), and [message state suffixes](storage-reference.md#message-state-suffixes) are defined in the storage reference.

The sorter discovers final EML files; the tagger discovers plain HDR files. Ownership removes the plain HDR first; publication moves EML first and exposes the plain HDR last. Procedures remain in §7, and failure/partial-state outcomes remain in §9. The same-volume NTFS boundary and `VERSION-SENSITIVE-004` apply to every transition.

---

## 5. Data directory layout

Use the [runtime directory trees](storage-reference.md#runtime-directory-trees) and [required directories and creation](storage-reference.md#required-directories-and-creation) as the sole layout definition. Existing-installation migration is covered by §5.3.

### 5.1 File formats

The normative formats are consolidated in the storage reference:

- [Sender configuration](storage-reference.md#sender-configuration), including authoritative auth pointers, sender records, template syntax, and shared MDN policy.
- [Identifiers and addresses](storage-reference.md#identifiers-and-addresses) and [persistent tag mappings](storage-reference.md#persistent-tag-mappings).
- [Logs and diagnostics](storage-reference.md#logs-and-diagnostics), including sorter outcomes, tagger trace events, publication attempts, error files, escaping, and accepted corruption.
- [Locks](storage-reference.md#locks).

Logging remains best effort: report failure to stderr and continue the same mail operation; diagnostic failure never holds mail or changes its outcome. The reference defines each log's disable/retry policy and interpretation.

With `-c`, announce the application name/version and selected mode, then the resolved data and spool directories, after all required locks are held. The tagger announces before loading configuration and reports its sender-record and tag-mapping counts only after configuration and mapping load both succeed, before queue processing. Counts come from already loaded collections, include individual and group mappings, exclude inert staging, and remain a startup snapshot. The sorter does not load or count tagger data. The welcome says `starting` and does not establish readiness; a subsequent startup failure retains normal stderr reporting without claiming a successful load.

For mail processing, attempt one concise console line only after both message files complete their transfer. The sorter reports each original pair with `PASS` for a move to `spool`, `TAKE` for a move to `process`, or `FAIL` for retention in `failed`, replacing `from` and omitting the trailing destination clause. Complete upstream-failure retention remains an error with its existing stderr report and exit status. The tagger reports each published child separately, or one unchanged original pair for pass-through. A tagged child's line identifies its individual tag as newly `created` by this message's lookup or already available and `used`; group-tag details remain in the existing detailed outputs. Report the original HDR positional envelope sender and the transferred pair's envelope recipients, not visible EML header addresses. Best-effort display extraction must not introduce new mail validation, EML reads by the sorter, or reporting state shared between messages. Unavailable display fields do not affect processing.

Do not emit concise mail summaries for incomplete/failed transfers, deferred or stale candidates, internal state renames, mapping allocation, or pending children. Report a published child immediately after its completed HDR move; a later sibling or parent-cleanup failure does not retract that success or add another concise line. These summaries establish completed pair transfer only, not final delivery. Console formatting, writing, or flushing failure remains diagnostic only. Exact format, local timestamps, and one-physical-line escaping are defined in the [concise console reference](storage-reference.md#concise-console-output--c); file formats and UTC timestamps are unchanged.

### 5.2 Data coherency

The [cross-file consistency and publication rules](storage-reference.md#cross-file-consistency-and-publication) define required relationships between auth indexes, sender records, and permanent mappings. Tagger startup validates the complete configuration; the sorter deliberately checks only auth-directory existence. Sender configuration is fixed for the invocation. Staging leftovers are inert evidence, never live records or automatic recovery inputs.

### 5.3 Backup, restore, layout changes, and retention

Version 1 has no schema/version marker and performs no automatic migration or fallback to earlier paths or configuration meanings. Before upgrading an installation predating rc.10, follow the [offline queue migration and rollback procedure](queue-layout-2026-09-17.md), including the earlier sender-configuration conversions if applicable. Stop both programs and pause upstream processing/administrative changes, back up configuration and mail state together, and coordinate both matching binaries. Preserve every queue suffix, retained byte, stable sender ID, permanent mapping, log, and staging artifact. Do not start on a partially migrated queue or treat an empty new queue as proof that the old one was drained. An offline rollback may restore prior binaries and complete prior checkpoints only before any new post-cutover state exists; otherwise reconcile that state before restoring or resuming.

Neither executable creates backups. A consistent checkpoint requires both programs, SmarterMail processing, and administrative updates to be stopped while the complete datadir and complete mailroot are backed up as coordinated units, with the remaining direct Proc and normal-spool state inventoried and preserved separately. A datadir-only backup now omits queued mail, retained originals, `-keep` copies, and per-message diagnostics. Include operator-selected trace/email logs separately when they are outside those roots. If data and mail use different volumes, maintain the same stopped checkpoint across both; there is no cross-volume transaction or automatic recovery.

Restoring an older backup can reintroduce uncertain queue work and omit tag mappings or history created after the checkpoint. Restore and reconciliation must occur offline. Before the restored datadir may be used, the operator must account for every post-checkpoint mapping, tag-log append, sender-id record change, and retained state using surviving current data and external records. If every emitted post-checkpoint mapping cannot be recovered or otherwise accounted for, do not treat the restored tag namespace as complete and do not resume normal tag minting or attribution from it. Otherwise the same recipient could silently receive a second permanent tag. Version 1 cannot infer or repair that loss.

Neither program automatically ages, retries, rotates, truncates, or deletes auth indexes, permanent mappings, tag logs, sender-id records, staging leftovers, failure diagnostics, `-keep` artifacts, or partial-message evidence except during the documented normal-success transitions. Operator disposition occurs only while the owning program is stopped and is outside version 1's automatic guarantees. Removing an auth index has the enrollment effect in §1.2; it never removes its sender's mapping history.

---

## 6. Input file formats

### 6.1 The `.HDR` envelope

See the normative [HDR format](storage-reference.md#the-hdr-envelope), including positional fields, repeated metadata, status, `auth:`, `from:`, and `notify:` meanings. Readiness and routing are defined in §7.3.1. Inbound mail without auth still passes through this queue under §1.3.

### 6.2 The `.EML` message

See the normative [EML format](storage-reference.md#the-eml-message) and [input grammar](storage-reference.md#input-grammar), including the preserved leading UTF-8 BOM exception. The body remains byte-for-byte unchanged; the only permitted header edits are defined in §8.

### 6.3 `from-template.txt`

See the normative [template file format](storage-reference.md#from-templatetxt). Dedicated catch-all provisioning remains a prerequisite under §1.3; `ASSUMPTION-TEMPLATE-LENGTH` remains a trusted operator constraint. The separate edited-header line-length gate remains in §8.1.

### 6.4 Deterministic message parsing

The normative [input grammar](storage-reference.md#input-grammar) defines HDR classification, full HDR/EML framing, supported mailboxes, null paths, MDN recognition, and byte preservation. `VERSION-SENSITIVE-005`, `VERSION-SENSITIVE-006`, and `VERSION-SENSITIVE-012` require corresponding fixtures and client-path evidence. Parser organization belongs in `implementation.md`; processing and failure decisions remain in §§7–9.

---

## 7. Processing behavior

If `-l` or `-v` is enabled, trace consequential evaluations, branches, and persistent state transitions under §5.1 so an operator can reconstruct the path taken.

### 7.1 Identities and mappings

#### 7.1.1 Canonicalize an addr-spec

Use the storage reference's [address identity](storage-reference.md#address-identity) for all comparisons and keys. Preserve original addr-spec bytes for edits and recipient delivery; canonical identity deliberately collapses ASCII case differences, including local-part case.

#### 7.1.2 Parse recipients and create a recipient-id

Use the [recipient identity](storage-reference.md#recipient-identity) grammar, canonical ordering, deduplication, and reversible encoding. Parse recipients only at the processing stage required below; no-match pass-through does not require a valid recipient list.

#### 7.1.3 The sender-id and recipient-id to tag-address mapping

One permanent mapping associates each `(sender-id, recipient-id)` pair with exactly one tag-address. The tagger loads those mappings before processing and makes each newly allocated mapping available to later messages in the same invocation. We expect fewer than 100,000 mappings in total and normally 100–5,000 for one sender. The five-character entropy decision is scoped to that per-sender range; materially exceeding it requires design review.

#### 7.1.4 Obtain a tag-address

For a `(sender-id, recipient-id)` pair:

1. If a mapping exists, return its tag-address. Lookup and allocation do not add a tag-log entry; only publication does.
2. Otherwise create one complete, inert staging identity record containing the sender-id and recipient-id. Attempt to create its empty tag log, but a logging failure only prints to standard error and does not prevent publication of the complete identity record. Incomplete identity files are never a live mapping.
3. Generate a five-character token from a cryptographically secure random source using the [tag-token alphabet](storage-reference.md#identifiers-and-addresses). The five characters are independent and uniformly distributed, so all `32^5` tokens are equiprobable and provide the stated 25 bits. There is no weaker fallback.
4. Substitute that token for the template's `%` placeholder. Template validation at startup guarantees that any token from this alphabet produces one canonical, usable tag-address; the generated value is trusted thereafter.
5. Publish the complete staging record atomically under `tag-addresses\<tag-address>\`, without replacing or adopting an existing mapping. This directory transition is the mapping's single live-publication point.
6. If that tag-address already exists, generate another token and try another final address, up to sixteen token proposals. Another publication failure fails the message and leaves the staging record inert.
7. Once the mapping is live, make it available to subsequent messages in the running tagger. If the process cannot do so, it must stop; restart will load the complete published record and preserve mapping uniqueness.

An in-scope crash before mapping publication leaves only inert staging. A crash afterward leaves a complete live record that startup will load. This is process-crash visibility under §9.6, not a power-loss guarantee. UUID naming, randomness APIs, directory creation, and in-memory structures are defined in `implementation.md`.

### 7.2 Program startup

Each executable owns and validates only its own resource boundary.

**Sorter startup:** Before scanning `proc\` or touching a message, acquire only the sorter singleton under §11. The sorter does not open the tagger trace, preload auth indexes, read `sender-id.txt`, inspect sender-id records or mappings, or wait for the tagger. It consults `senders\auth-addresses\` only for a message with one valid enrollable auth. If that lookup is unavailable, no-auth and structurally unenrollable messages can still pass while affected auth messages are held. Version 1 performs no volume, free-space, or destination-existence preflight.

**Tagger startup:** Acquire the data singleton first, create the fixed mailroot if needed, then acquire its queue singleton. Hold both before requested tracing, configuration loading, scanning, or message processing. Failure to acquire either stops startup and releases any already-acquired handle without touching queued mail. If `-l` or `-v` is enabled, initialize the requested trace outputs before the remaining startup work. With `-l`, attempt to open the selected file; a file failure prints to standard error and startup continues without that file while requested verbose output remains independent. Then establish and validate the complete sender configuration that will govern this invocation:

- every usable sender-id record and its private-address, validated template, and MDN policy;
- every published auth index and its unambiguous pointer to an existing sender-id record, allowing several indexes per record or none.

Separately validate every complete live `(sender-id, recipient-id)` mapping, with no duplicate key or unresolved sender. A mapping published during this invocation must be reused by every later message rather than allocated again. The in-memory representations that provide these behaviors belong in `implementation.md`.

The `senders\auth-addresses\` and `senders\sender-ids\` roots must exist and be accessible and enumerable; otherwise the tagger cannot prove that its configuration view is complete and startup fails. Missing required files, malformed identity data, or a conflict that could change sender-id record selection or mapping uniqueness likewise fails startup before any message is processed. Extra unrelated files are ignored. Inert entries under either staging directory and suffixed message leftovers are named in a startup warning but are neither parsed as live data nor repaired, retried, or deleted. An absent `tag-addresses\` directory means the mapping set is empty.

Sender configuration takes effect only at tagger startup and remains unchanged for the life of the process. Concurrent administrative editing is unsupported. Stop the tagger, complete and publish the configuration update, and restart it before the client uses the new identity. The sorter independently observes a newly published auth folder on a later lookup. Live configuration reload is deferred to `future.md`.

### 7.3 Trigger

#### 7.3.1 Sorter classification and handoff

For each `proc\<basename>.eml`, either selected by the basename on the sorter command line or found while watching `proc\`:

1. Before reading or probing the matching HDR, establish that the final `.eml` exists as a file, without opening or reading its contents. A missing EML is a stale selection in watch mode; one-shot mode defers with `EML_NOT_AVAILABLE`. Neither inspects an HDR-only attempt. A directory in its place or another failure to determine its attributes becomes a hold decision with `INPUT_READINESS_FAILED`.
2. Read the matching same-basename plain HDR in place using read-only access with `FileShare.Read`, excluding active writers. Close the read handle before any ownership rename. If the HDR vanished before the read, it is stale and skipped in watch mode; in one-shot mode the requested message fails. A Windows sharing/lock violation defers the candidate untouched with `HDR_BUSY`. Any other HDR read failure becomes a hold decision so the sorter can attempt to make the ambiguous trigger inert.
3. Compare the first CRLF-terminated status line case-sensitively after removing trailing ASCII SP/HTAB only. Exact `Written` permits the fast HDR scan in §6.4 and the existing enrollment/classification rules. Exact `Failed` becomes a hold decision with `UPSTREAM_FAILED` and takes the unchanged retention path without auth lookup. Exact `Writing` returns `Deferred` with `HDR_WRITING`. Every other status, including `Quarantined`, `Ready`, wrong case, empty status, or a missing first CRLF, returns `Deferred` with `HDR_STATUS_UNEXPECTED` and reports unexpected status to stderr on every encounter, including watch mode and regardless of flags. Neither status deferral performs auth lookup or EML-content access. A valid `Written` line followed by malformed remaining HDR framing still holds with `UNSAFE_HDR`.
4. Choose one routing decision before claiming the pair:
   - `NO_AUTH` → direct pass to `spool\`.
   - `UNSAFE` → hold.
   - `VALID_AUTH` that cannot be represented by the literal auth-folder layout → direct pass as structurally unenrollable, without constructing that path.
   - Any other `VALID_AUTH` → divert when its exact auth directory exists, pass when that child is definitively absent beneath an accessible `senders\auth-addresses\` root, and hold when the lookup is unavailable or uncertain.
5. Claim the message by making the plain HDR inert as `.hdr.sort`, without replacing an existing artifact. If the plain HDR disappeared first, skip it without a diagnostic in watch mode; in one-shot mode the requested message fails. Any other ownership failure is fatal to that sorter invocation because it could not make the selected live trigger inert. Once claimed, the routing decision is fixed.
6. For `UPSTREAM_FAILED` only, after claiming the HDR, create `<mailroot>\failed\` if absent, then move the plain EML there, followed by the owned HDR retaining its `.hdr.sort` suffix. Neither move may replace an existing file. Stop this retention sequence at its first failure and preserve every completed operation. For every other hold, leave both files at their current locations in `proc\`. Attempt `<basename>.sort.err` beside the current owned HDR, report the error to standard error, and continue with later EML candidates.
7. For direct pass, move the plain EML to `spool\<basename>.eml`, then publish `.hdr.sort` there as the plain HDR. For diversion, move the EML to `process\<basename>.eml`, then publish the HDR there. The HDR transition is the destination's readiness event and no destination is overwritten.

A deferral creates neither `.hdr.sort` nor `.sort.err`, writes no terminal email-log record, and leaves all input bytes and names untouched. With `-v`, emit the normal `DEFER` debugging event and reason. Watch mode continues with other EML candidates and reconsiders them on a later notification or the ordinary rescan within 30 seconds while idle. `Writing` is quiet on watch-mode stderr; unexpected or incomplete status is reported to stderr on every encounter, without deduplication state. One-shot mode does not wait: it prints `NOT READY` to standard error and returns 1. An unexpected status may remain deferred indefinitely and requires manual investigation; never automatically promote it or change status bytes. There is no per-message sleep, retry loop, or readiness timeout.

A `Failed` HDR is a real terminal rejection even when retention in `failed\` completes: the outcome remains `Failed`, one-shot returns 1, and watch mode continues. Attempt exactly one terminal `ERROR` record with reason `UPSTREAM_FAILED`, actual current HDR/EML paths, and the attempted retention target. Directory creation or either retention move failing is message-local and keeps that reason; report the I/O error and partial locations. If creation or the first move fails, no relocation has completed and the last-known source paths remain in `proc\`; if the second move fails, the EML remains in `failed\` and the HDR remains in `proc\`. The diagnostic follows the current owned HDR. Always attempt stderr reporting. Diagnostic and email-log failures never prevent retention or change its outcome. Do not read EML contents, change status bytes, release the pair to SmarterMail, delete, roll back, or automatically replay either file.

**VERSION-SENSITIVE-001:** For a `Written` candidate, discovery relies on SmarterMail publishing final EML and final HDR metadata without later producer modification after a successful HDR read excluding writers. EML existence, status bytes, and that read are necessary checks, not proof of every producer guarantee; the read handle closes before ownership. Establish the contract on each deployed build and route. Saved successful SMTP and immediate/delayed/scheduled REST inputs said `Written `; failures said `Failed `. These bounded observations are not an all-route guarantee. The tagger's input contract remains the sorter's controlled EML-first/HDR-last publication. A shared basename associates the two files; no additional numeric-only basename restriction is introduced.

The sorter does not parse any other HDR address, inspect `sender-id.txt`, load sender-id records, or verify tagger readiness. Folder existence is deliberately only a conservative routing decision. Every published index is active in the tagger snapshot once validated at startup. Adding or removing an auth directory affects the first sorter lookup that observes the change; absent means unenrolled, not rejected.

If the EML disappears after the readiness check, a destination transition fails, or the diagnostic cannot be written, retain the exact state created by completed operations, report the available information, and continue with later EML candidates. Never roll a moved EML back. Existing `.hdr.sort` or `.sort.err` artifacts are not completed by this readiness gate. A remaining plain EML whose only HDR is `.hdr.sort` may be discovered, but its matching plain HDR is absent, so watch mode skips it as stale without replay. If a new matching plain pair appears alongside that residual, the conflicting ownership destination remains a fatal collision requiring manual disposition.

#### 7.3.2 Tagger trigger and activation

For each `process\<basename>.hdr`, either specified on the tagger command line or found while watching `process\`:

1. Claim the pair by renaming the HDR to `.hdr.start`, then the EML to `.eml.start`, both without replacement. If the plain HDR disappeared before the first rename, skip it as stale in watch mode; in one-shot mode the requested message fails. Any other first-rename failure is fatal to that tagger invocation because no inert ownership state exists; an EML rename failure after the HDR claim is message-local. After both claims succeed, make `.in` copies of the two `.start` files if `-keep` is enabled. (`-keep` copy operations are not traced.)
2. Parse the complete HDR framing and the metadata/sender values needed for trigger classification once, but leave recipient line 3 uninterpreted at this stage. Require exactly one auth-address resolving through the startup auth-index snapshot and select that sender-id record. Missing, ambiguous, malformed, or unknown auth is a message failure. A pair already routed into `process\` never falls back to the sorter's unconfigured-auth pass rule.
3. Parse every occurrence of the EML sender fields before tag lookup or generation. Each present occurrence of a supported sender-identity field listed in §8 must individually conform to the version 1 grammar; syntactic ambiguity within an occurrence is a failure even if no current private-address match has yet been found. Record the `From:` field and mailbox counts and the `Reply-To:` field count. The activated-message count requirements below apply only after a current-private match; individually valid occurrences do not by themselves prevent a no-match pass-through. When `allow-mdn` is `false`, also perform the top-level MDN classification in §6.4.
4. If that classification recognizes an MDN, fail closed before private-address pass-through classification, recipient parsing, or tag lookup. When `allow-mdn` is `true`, skip this classification and follow the ordinary sender-field rules; the flag does not force tagging.
5. Compare canonical-addresses under §7.1.1. If none matches the configured private-address, pass the unchanged `.start` pair to `spool\`: when `-keep` is enabled, first copy the pair to `.out`, then move the EML and then the HDR without replacement. Do not parse recipients or create or look up a tag. Former private addresses are ordinary non-matches. If at least one configured-private match exists, tagging is activated.

6. For every activated message, require exactly one EML `From:` field containing exactly one mailbox, and at most one `Reply-To:` field. A missing or repeated `From:`, or a `From:` with the wrong mailbox count, is a runtime contract error and follows the error-retention procedure below. Repeated `Reply-To:` fields are likewise a message-local contract failure but retain their current `.start` state. Enforce these requirements before fan-out, recipient parsing, or any tag lookup or allocation. They apply regardless of which supported sender field activated tagging and whether the message needs group-tag handling. The one `From:` mailbox need not equal the private-address; preserve a non-matching address under the ordinary rewrite rule.

**`From:` contract-error retention:** Starting with the owned `.start` pair, rename `<basename>.hdr.start` to `<basename>.hdr.err`, then `<basename>.eml.start` to `<basename>.eml.err`, without replacement. These are retention transitions only; do not allocate a tag, construct a child, or publish any mail. If either rename fails, stop the retention moves and keep the exact state left by completed operations; do not overwrite a colliding artifact or roll back. Attempt `<basename>.err` containing the contract reason, observed field/mailbox counts, and actual retained paths, attempt an error event in the requested trace outputs when available, and print the error to standard error regardless of `-l` or `-v`. Include any retention or diagnostic failure in the available error report. Logging failures do not change this contract-error outcome. Watch mode continues with later fresh messages; one-shot mode returns nonzero. The error pair is never resumed automatically.

### 7.4 Fan-out

1. Rename `<basename>.hdr.start` → `.hdr.break`, then `<basename>.eml.start` → `.eml.break`.

2. Parse line 3 of `process\<basename>.HDR.break` into the canonical ordered and deduplicated recipient records and complete-list recipient-id defined by §7.1.2. Recipient parsing begins only after tagging has activated; tagger pass-through messages do not need it.

3. If there is more than one unique canonical recipient record, this is a multi-recipient message:
    a. Use the already parsed optional `Reply-To:` field and the single-mailbox `From:` established by §7.3.2.
    b. If one valid `Reply-To:` exists and contains no addr-spec canonically equal to the private-address, preserve it byte-for-byte. Do not create or look up a group tag.
    c. Otherwise, group-tag handling is required. Generate or look up the stable group-tag-address using the complete-list recipient-id from step 2 (§7.1.4).
    d. If `Reply-To:` was absent, synthesize exactly one by starting with the validated single-mailbox `From:` field, changing the field name to `Reply-To:`, and replacing its mailbox addr-spec with the group-tag-address while retaining its display-name formatting.
    e. If one valid `Reply-To:` existed and contained the private-address, replace every matching addr-spec with the group-tag-address. Preserve every non-matching mailbox and every byte outside the replaced addr-specs.


4. For each recipient *n*, numbered from 1 by position in the sorted and deduplicated canonical recipient list, construct a new child using the literal `c` and invariant decimal [naming contract](storage-reference.md#message-state-suffixes), without overwriting any existing artifact:
    1. Create `process\<basename>c<n>.eml.process` from the parent EML with these changes:
        a. If multi-recipient group-tag handling was required, apply the prepared `Reply-To:` addition or replacement before ordinary child sender rewriting. If the original non-private `Reply-To:` suppressed group-tag creation, leave it byte-for-byte.
        b. Encode the current record's single canonical-address as a one-address recipient-id under §7.1.2, then generate or look up its child tag-address (§7.1.4).
        c. In the EML headers, replace every parsed addr-spec in a supported sender-identity field whose canonical-address matches the private-address canonical identity with the child tag-address. Preserve non-matching sender identities and every byte outside the replaced addr-spec. Do not apply sender rewriting to recipient fields.
        d. Create `process\<basename>c<n>.hdr.process` with line 3 reduced to **exactly one** recipient—the retained first original-address for the recipient currently being processed. For `notify:` filtering, skip optional leading SP/HTAB in the value, parse one bare addr-spec, and require the next byte to be the `=` delimiter; treat everything after that delimiter as opaque. Keep every well-formed `notify:` line whose address has canonical-address equality with the child recipient, preserving each complete kept line byte-for-byte and in original order. Drop all nonmatching or unparseable `notify:` lines. Zero or multiple matches are valid and never fail the message. In HDR line 2 and every HDR `from:` field, replace an addr-spec if and only if its canonical-address equals the private-address canonical identity. Preserve every null path and non-matching sender addr-spec byte-for-byte. Keep `auth:` unchanged. Keep every other line byte-for-byte, including the trailing blank line.
        e. Only after both child files are completely written and the resulting edited/synthesized EML header lines pass §8.1, make the EML `.pend` and then the HDR `.pend`. A child is ready only when both `.pend` files exist; the HDR transition is the last readiness marker.


### 7.5 Publish

Do not begin publication until every intended child is a complete `.pend` pair that has passed §8.1.

1. If `-keep` is specified, create **all** child `.out` pairs before publishing any child. A copy failure stops the operation while every child remains unpublished.

2. For each child `.pend` pair in the defined child order:
    a. Build the tag-log entry for output basename `<id>` under §5.1 and attempt each distinct referenced log in ordinal tag-address order. Report failures to standard error and continue. These are the last mapping-log attempts before publication; best-effort execution-trace result and next-intent events and standard-error diagnostics may intervene.
    b. Move `process\<id>.eml.pend` → `spool\<id>.eml` without replacing an existing destination.
    c. Move `process\<id>.hdr.pend` → `spool\<id>.hdr` without replacing an existing destination. **That child is live from this instant.**

   Complete both moves for one child before attempting any move for the next child. Do not move all EMLs as a batch and do not pre-publish later children. This deliberately favors prompt successful delivery to earlier children over all-or-none fan-out.

   - A tag-log or execution-trace failure is diagnostic only: print it to standard error and continue this child's publication and later children. Do not create a parent `.err` or retain a successfully processed parent solely because logging failed. Leave any partially appended bytes in place.
   - If a child's EML move fails, stop immediately. Any pre-move tag-log bytes remain, that child and every later child remain complete `.pend` pairs, and earlier children whose HDR moves succeeded may already have been sent.
   - If a child's EML move succeeds but its HDR move fails, stop immediately. Leave that child's plain EML in `spool\`, its HDR as `.hdr.pend` in `process\`, and every later child as a complete `.pend` pair. Do not delete or move the staged EML as an attempted rollback.
   - On a mail-publication error, do not attempt a later child and do not delete the parent `.break` pair. Attempt the parent `.err` diagnostic with every child id and one of: `PUBLISHED` (HDR move succeeded), `EML_MOVE_FAILED`, `HDR_MOVE_FAILED_EML_IN_SPOOL`, or `UNATTEMPTED`. `PUBLISHED` means the child became live and must be treated as possibly sent; it does not prove final delivery. Logging errors may appear as additional diagnostic details but never replace the child's actual publication state. An intact tag-log entry by itself means only `PUBLICATION_ATTEMPTED`; reconcile it with these retained states.

3. Only after every child HDR has been published successfully, remove the immutable parent `.eml.break` and `.hdr.break`. Cleanup is defined by the postcondition that both files are absent; discovering one already absent is not itself an error. Without `-keep`, no files from a successful operation remain in `process\`. With `-keep`, only the parent `.in` and child `.out` copies remain there.

4. An actual failure while removing a remaining parent file after all children are live is a cleanup failure. Preserve whatever parent state remains, add the parent `.err` diagnostic, and perform no further automatic transition. Manual disposition is outside version 1.

There is no automatic recovery from partial publication. The program never replays the parent, republishes a child marked `PUBLISHED`, or promotes an EML/HDR remainder. Version 1 defines no manual recovery procedure or duplicate-avoidance rule for operator intervention. Operators may use the documented tree, transition order, diagnostics, tag logs, and SmarterMail records to handle a case ad hoc, accepting that manual publication, renaming, deletion, or requeueing can duplicate or lose mail and is outside the program's guarantees. Automatic alerting is deferred to FUT-005.

Version 1 relies on SmarterMail basename uniqueness, deterministic child-name continuity, the enforced singletons, and the approved deployment's support for concurrent sorter/tagger publication. It does not add a separate duplicate-delivery or derived-name reconciliation policy for violations of those assumptions. No existing destination is overwritten; an actual move failure follows the retained-state rules above.


---

## 8. Sender rewriting

For a configured `auth:` sender-id record, tagging is activated when at least one supported sender-identity field contains a parsed addr-spec whose canonical-address equals the sender-id record's canonical private-address (§7.1.1). Every activated message must satisfy §7.3.2: exactly one EML `From:` field containing exactly one mailbox, and at most one `Reply-To:` field. Replace **every** such complete addr-spec match with the child tag-address. Legitimate non-matching sender identities are preserved.

**Only the matching addr-spec is replaced. Everything else in the logical header field is preserved byte-for-byte** — display name, quoting, spacing, folding, and angle brackets. The user owns their display name; this program does not normalise, unfold, decode, or re-encode it.

```
From: "Joe Q. Public" <k7p2qx@example.com>
From: "Joe Q. Public" <joe-a3f92@example.com>      <- only the addr-spec changed
```

Use the supported grammar in §6.4. Shapes confirmed in real mail that must be accepted include bare and bracketed `Reply-To:`, single-mailbox `From:` with quoted or unquoted display names, comma mailbox lists in fields that permit them, and folds between syntactic elements. Rewrite every matching addr-spec in every permitted occurrence of a supported sender-identity field, not merely the first. Do not apply the rule to recipient identities, message identifiers, trace syntax, unsupported fields, or the body. The byte-edit technique belongs in `implementation.md`.

| Location | Action |
|---|---|
| `.HDR` line 2 (positional envelope sender) | if its parsed addr-spec equals the private-address, replace it with the child tag-address; preserve a non-matching addr-spec or null path unchanged |
| `.HDR` `from:` key | in every occurrence, replace the parsed addr-spec if it equals the private-address; preserve a non-matching addr-spec or null path unchanged |
| `.HDR` `auth:` | **leave untouched** |
| `.HDR` recipient line 3 / `notify:` | reduce line 3 to this child and filter `notify:` by the lenient canonical-address rule in §7.4 |
| `From:` | require exactly one field containing exactly one mailbox; replace its addr-spec if it equals the private-address, preserving a non-matching address and all other bytes |
| `Return-Path:` | **strip every complete logical-field occurrence**, including continuations — the receiving MTA regenerates it from the envelope, and a stale one is worse than none |
| `Reply-To:` | parse every occurrence before trigger classification; if tagging activates, require at most one valid field. For a single-recipient tagged message, replace private-address matches with the child tag. For a multi-recipient tagged message, follow §7.4: preserve a wholly non-private field, or replace private-address matches with the stable group tag; synthesize a group field when absent |
| `Sender:` | replace every parsed addr-spec equal to the private-address; preserve non-matching addr-specs |
| `Disposition-Notification-To:`, `Return-Receipt-To:`, `X-Confirm-Reading-To:` | replace every parsed addr-spec equal to the private-address; preserve non-matching addr-specs |
| `Resent-From:`, `Resent-Sender:` | replace every parsed addr-spec equal to the private-address; preserve non-matching addr-specs |
| `To:`, `Cc:`, `Bcc:`, `Resent-To:`, `Resent-Cc:`, `Resent-Bcc:` | recipient identities; **never sender-rewrite**, even if an addr-spec equals the private-address |
| `Message-ID:` | **leave alone** — verified to carry only the domain, not the local part |

Across current real samples only `From:`, `Reply-To:`, and `Return-Path:` carried the sender. The larger supported surface exists because those other standardized sender fields can carry the same identity and therefore belong inside the stated secrecy guarantee. Do not scan or rewrite unsupported headers, trace fields, nested content, or the body; they remain explicitly outside that guarantee (§1.4).

**On body content and MDNs.** Bodies are copied byte-for-byte even if their raw or encoded content includes the private-address. Operators must ensure that the private-address is not deliberately placed in signatures, templates, or body content. Ordinary replies should quote the tagged address, but that expectation is an operational assumption rather than part of the program's secrecy guarantee. Recognized MDNs are blocked for a sender whose `allow-mdn` is `false`; setting it to `true` merely permits the message to follow these same header-only rules and is valid only under the evidence gate in §1.4.

**Post-rewrite authentication gate.** `sm-tagger` does not generate DKIM, SPF, DMARC, or ARC results. The approved SmarterMail path must add a valid DKIM signature after rewriting whose `d=` domain exactly equals the final RFC5322 `From:` domain, cover the final `From:`, preserve that validity through downstream relays, and produce `dkim=pass` and `dmarc=pass` at an independent receiver for every emitted tag domain and supported message size. Exact domain equality is the deployment requirement even where relaxed DMARC alignment would accept a parent or sibling organizational domain. When the envelope sender is changed to a tag domain, that domain must also authorize the sending host for SPF. A preserved unrelated or null reverse-path may not align, so this exact-domain DKIM result is mandatory.

**VERSION-SENSITIVE-008, VERSION-SENSITIVE-009, VERSION-SENSITIVE-010, VERSION-SENSITIVE-011:** Signing order, relay acceptance of new tags, catch-all delivery, and client-generated identity bytes outside the rewrite surface require evidence for each changed product/version/path. Captured message samples alone do not establish those properties for a deployment.

**Arbitrary tag-address acceptance.** Version 1 creates no SmarterMail user, alias, or provider send-as registration per tag. The approved route must accept previously unseen individual tags in every sender role the algorithm uses, preserve a group tag as `Reply-To:`, and accept replies through the dedicated catch-all. A route that requires per-address provisioning or verification is unsupported.

These are environment support conditions, not conclusions from local file order. Establish them with the bring-up matrix in `testing-plan.md`, record the evidence in `assumptions.md`, and repeat affected checks after relevant client, SmarterMail, DNS, signing, relay, size-limit, or routing changes.

**Pre-existing authentication/signature fields.** Version 1 does not parse, validate, remove, repair, or otherwise special-case an input `DKIM-Signature:`, `ARC-Seal:`, `ARC-Message-Signature:`, `ARC-Authentication-Results:`, `Authentication-Results:`, or `Received-SPF:` field. They are unsupported opaque headers and remain byte-for-byte except for their relative displacement by an explicitly inserted/removed field elsewhere. The deployment assumption is that enrolled authenticated clients submit directly to SmarterMail without DKIM/ARC signing and that SmarterMail supplies the authoritative aligned DKIM signature after `sm-tagger` publishes the child. If an unexpected earlier signature covered a rewritten field, it may fail validation; version 1 accepts that residual result as long as the mandatory external gate proves that the new aligned SmarterMail signature and final DMARC result pass. S/MIME, PGP/MIME, and protected headers remain client-compatibility features that must be approved under `assumptions.md`; this rule does not claim their signed or encrypted content is safe to rewrite.

### 8.1 Edited and synthesized header-line length

Every resulting physical EML header line whose bytes are changed by a sender-address replacement, and every physical line of a synthesized `Reply-To:` field, must contain at most **998 bytes excluding its terminating CRLF**. Count the entire resulting physical line, including the field name and colon when present, display-name bytes, address bytes, punctuation, and whitespace; continuation indentation counts too. Exactly 998 bytes is valid. This implements the physical-line limit in [RFC 5322 §2.1.1](https://www.rfc-editor.org/rfc/rfc5322.html#section-2.1.1) for the supported byte-oriented header grammar. It is separate from the trusted template-generated address-length assumption.

When a preserved EML preamble precedes the first surviving original field, include its three bytes if that field's first physical line is checked. Determine the first surviving field after removing every `Return-Path:`; do not charge the prefix to later fields or continuation lines. A synthesized `Reply-To:` has no preamble and receives no extra three-byte charge. This is conservative raw-byte accounting for the compatibility preamble, not a claim that RFC 5322 defines such a prefix; the existing scope of which lines require checking is unchanged.

Check the resulting lengths after the actual individual/group tag addresses and all sender edits for the child are known, before either child file becomes `.pend`. A synthesized `Reply-To:` must be checked with its new field name; replacing `From:` with `Reply-To:` adds four bytes even when the address length stays equal. Count each physical line separately, not the total unfolded field length. Removed fields have no resulting line to check. Unchanged physical lines, unsupported opaque fields, bodies, HDR files, and no-match pass-through are outside this check. Do not refold, truncate, shorten, or otherwise change preserved bytes to make an output fit.

An overlong result is a **message-local runtime contract error**. Stop further child construction and publish no child for that parent. Retain the immutable `.break` parent and any constructed children in their exact inert states (`.process`, `.pend`, or no child files yet), following the normal pre-publication fan-out failure rules. Any mapping already published remains permanent and reusable. Attempt the parent `<basename>.err` diagnostic, an error event in the requested trace when available, and standard-error output regardless of `-l` or `-v`. Include the child, field, physical-line position, measured byte length, 998-byte limit, and retained paths in the diagnostic. Failures to write diagnostics do not release rejected mail. Watch mode continues with later fresh messages; one-shot mode returns nonzero. This uses the fan-out retention states, not the early `From:`-count `.start`-to-`.err` rename procedure in §7.3.2.

---

## 9. Failure model

The governing safety rule is simple:

- The sorter passes a message only when it can establish that auth is absent, structurally unenrollable, or definitively not enrolled. Ambiguity is held rather than passed.
- Once a message has been diverted to `process\`, the tagger fails closed unless it can prove that unchanged pass-through or the complete specified transformation is safe.
- No incomplete output becomes live: a destination's plain HDR appears only after its EML and all preceding preparation are complete.

### 9.1 Failure scope

A **startup/fatal failure** stops only the affected executable, releases its held singleton resources, reports to standard error, and returns nonzero. These failures include:

- inability to acquire any required singleton;
- tagger configuration or live-mapping ambiguity;
- inability to maintain reliable queue discovery;
- inability to make a selected live HDR inert after it was chosen for ownership;
- inability to make an already-published live mapping available to later messages in the same invocation.

A **message-local failure** stops normal processing for the owned message, performs any specifically required retention transition (§7.3.1 for upstream `Failed`, §7.3.2 for a `From:` contract error), preserves the resulting files, attempts the applicable `.sort.err` or parent `.err`, and lets watch mode continue with later fresh messages. These include malformed or unsupported enrolled mail, a missing EML after ownership, pre-publication identity-record allocation or random-source failure, child construction failure, an overlong edited or synthesized EML header line (§8.1), and handoff/publication/cleanup failure after ownership. Report runtime contract errors to standard error and attempt an execution-trace error event when `-l` or `-v` is active. The post-publication mapping-availability failure listed above is fatal because continuing without reusing the live mapping could mint a second tag for the same identity. Log and diagnostic-file failures themselves are not mail-processing failures.

A selected sorter EML or matching plain HDR that disappears before ownership is stale in watch mode and needs no error diagnostic. The tagger likewise skips a vanished plain HDR before its claim. One-shot distinctions are specified in §7.3.1. Once ownership succeeds, do not convert a failure into pass-through, retry, rollback, or lower-fidelity output.

Sorter readiness deferral is a separate pre-ownership outcome: the producer's files remain untouched and no retained-error state is created. Watch mode can reconsider that plain candidate normally; one-shot mode prints `NOT READY` and returns 1 without waiting.

One-shot mode returns nonzero if its requested message is not ready or does not complete for a mail-processing or contract reason. Log and diagnostic-file failures print to standard error but do not hold mail or change the exit status. If an underlying message error already requires retention, failure to record that error does not release the retained message.

### 9.2 Retained evidence and partial publication

Suffix states and actual file locations are authoritative. Diagnostics and logs explain those states but do not override them.

Before publication, every parent and child state is inert because no destination plain HDR exists. Children are published sequentially. On the first publication failure:

- stop immediately and do not attempt a later child;
- never roll back an EML already moved to `spool\`;
- treat every child whose plain HDR reached `spool\` as possibly sent;
- leave current and later children in their exact residual states; and
- retain the parent `.break` files that still exist.

An intact tag-log entry means only `PUBLICATION_ATTEMPTED`. It does not prove that the EML move, HDR move, SmarterMail acceptance, or final delivery succeeded. Corrupted log contents may not preserve identifiable entries; use actual filesystem states as the authority.

After all child HDRs are live, parent cleanup cannot cause another message publication. An already absent parent file satisfies cleanup's desired postcondition. A real deletion failure preserves whatever remains and produces a cleanup diagnostic.

### 9.3 No automatic recovery

Leftover `.sort`, `.start`, `.break`, `.process`, `.pend`, `.hdr.err`, and `.eml.err` files and their diagnostics are never resumed, replayed, retried, renamed back, or automatically deleted. Staging leftovers are likewise never adopted or published. They remain for manual disposition while the sorter considers final EML candidates and the tagger considers fresh plain HDRs.

At startup, each watcher reports the leftovers in its own input queue and then continues with fresh work. Neither watcher scans `<mailroot>\failed\`, and older retained files in `proc\` are not relocated on restart. Retained `.in` and `.out` files are expected debugging evidence rather than failed work.

Manual intervention is outside version 1's guarantees. Publishing, requeueing, renaming, or deleting residual files can duplicate or lose mail. Version 1 deliberately defines no duplicate-avoidance or automatic repair policy.

### 9.4 Logging and operator visibility

All logging is best effort. Creating, opening, encoding, writing, flushing, or closing a log can fail without holding the current message, stopping the watcher, or changing the mail outcome or exit status. Report the failure to standard error and continue under §5.1. Existing log corruption is acceptable, and complete diagnostic history is not guaranteed. Runtime message-contract errors still retain the message and produce best-effort file, trace, and standard-error diagnostics. Out-of-band delivery of captured standard error is deferred to FUT-005.

After watch mode starts successfully, ordinary message failures do not terminate it. Orderly shutdown finishes an already owned message and returns 0. Startup/fatal failures return nonzero.

A failed or stopped tagger queues enrolled mail in `process\` while a healthy sorter continues unrelated mail. A failed sorter stops all new flow through its selected `proc\`. Version 1 has no built-in monitor or out-of-band alert; operators must observe process status, standard error, queue age, and retained filesystem evidence. Monitoring is deferred to FUT-005.

### 9.5 Crash states

If a process terminates between completed transitions, the following residuals are expected:

| crash point | result |
|---|---|
| sorter before HDR ownership | the plain input pair remains eligible for a later scan |
| sorter after `.hdr.sort` but before the EML move | inert `.hdr.sort` plus plain EML in `proc\` |
| sorter after its EML move but before its HDR move | EML at the destination and inert `.hdr.sort` in `proc\`; no destination message is live |
| sorter after upstream-failure retention completes | EML and `.hdr.sort` remain in the inert mailroot `failed\` directory, with any completed diagnostic; manual disposition only |
| tagger after HDR ownership but before EML ownership | inert `.hdr.start` plus plain EML in `process\` |
| tagger during classification | inert `.start` state |
| during `From:` contract-error retention | `.start`/`.err` mixture if interrupted between renames, or complete `.hdr.err`/`.eml.err` pair; no child or live output |
| during fan-out | parent `.break` plus incomplete `.process` and/or complete `.pend` children; nothing published yet |
| between a child's readiness transitions | one `.pend` and one `.process`; the child is not ready |
| after tag-log attempts but before EML publication | complete `.pend` pair with any successfully appended log bytes; the child is not live |
| between a child's EML and HDR publication | plain EML in `spool\`, HDR still `.pend`; the child is not live |
| after one or more child HDRs publish | earlier children are possibly sent; parent and remaining work stay inert |
| after all child HDRs publish but before cleanup | all children are live and any remaining parent `.break` files are inert |

If termination interrupts a state-changing operation, inspect the actual retained state rather than assuming success or failure. Any pair accepted externally must be treated as possibly sent.
### 9.6 Durability boundary

Version 1's state-machine guarantees cover a **process crash** while Windows, the mounted filesystem, storage hardware, and SmarterMail remain operating normally. Completed filesystem operations may be used to determine the state left by the terminated process, and the operating system releases its singleton resources.

A machine, kernel, controller, storage-device, or power loss is outside this guarantee. Version 1 does not force every data and directory change to stable media. After such an event, apparently completed mapping, log, message, or directory changes may be missing, partial, or reordered, and SmarterMail may have accepted mail that surviving local evidence cannot prove.

Normal startup still validates the configuration and mapping relationships it needs and reports inert leftovers. It cannot detect a complete record that vanished, reconstruct missing tag-log history, recall mail SmarterMail consumed, or prove final delivery.

After a known or suspected out-of-scope durability event, the operator must treat affected mapping, queue, log, and delivery history since the last trusted checkpoint as uncertain. Restore or reconciliation is an offline operational decision under §5.3. Version 1 has no automatic repair, clean-shutdown marker, boot interlock, or recovery runbook. The buffering and flush choices that implement this boundary are documented in `implementation.md`.
## 10. Security and trust boundaries

### 10.1 Trusted administration and filesystem

The operator-provided datadir and SmarterMail spooldir—their locations, permissions, topology, filesystem, and required within-tree volume placement—are trusted installation inputs. Mail working paths remain on the spool volume without junctions; mapping staging and publication remain together on the data volume, which may differ from the spool volume. The owner-confirmed assumption that SmarterMail does not read the Proc subtree isolates the nested mailroot; it is version-sensitive and accepted for this change, not established by a new live test. The program does not audit ACLs, ownership, reparse points, volume identity, or filesystem type and does not attempt to repair them. An administrator or service identity able to alter those trees while a program runs is outside the threat model.

This trust is intentional: version 1 is a small local mail utility, not a defense against its own administrator. Actual access, read, write, or move failures still follow §9.

Configuration changes are supported only while the tagger is stopped. Program logs and persistent records have one program writer. Once the applicable singleton is acquired, races with another copy of that program are outside the version 1 operating model.

### 10.2 Data boundaries

HDR and EML content remains syntactically untrusted. Ambiguous auth or supported sender syntax must not cause a private-address to pass through after enrollment. Configuration and persisted mappings are validated when the tagger establishes its startup view because accidental corruption could select a wrong sender or create a second mapping.

Values generated by the program or deliberately provisioned by the trusted administrator remain trusted after their boundary validation.

Any value used as one literal child name must be one usable Windows path component so it cannot escape its trusted root. This is a simple containment and layout requirement, not a general hostile-filesystem subsystem. An otherwise valid auth-address that cannot be represented by the literal-folder layout is structurally unenrollable and takes the sorter's direct-pass branch without constructing that path. Actual path-length or filesystem limitations are discovered through the required operation.

### 10.3 Entropy and diagnostics

Tag tokens use the cryptographically secure, uniform five-character generation defined in §7.1.4. Their 25-bit size is an accepted attribution limitation, not an authentication mechanism. Private-addresses are also created from a cryptographically secure source during onboarding and kept non-public, but only authenticated enrollment—not possession of that string alone—selects a sender-id record.

The sorter email log, both programs' console debugging and concise summaries, tagger execution trace, parent diagnostics, `-keep` artifacts, and standard error may contain any identity or message metadata useful for diagnosis. They are intentionally unredacted and belong inside the same protected operational boundary as the datadir and spool. File-record and scalar metadata controls remain escaped. Console exception chains and stack traces deliberately use real line breaks for readability; they are multiline operator text, not a sanitized single-line record. See the [diagnostic rendering rules](storage-reference.md#logs-and-diagnostics).
## 11. Concurrency and atomicity

Version 1 has independent sorter and tagger ownership. The [storage reference](storage-reference.md#locks) defines the persistent files:

- one sorter owns the direct entries of one SmarterMail Proc queue;
- one tagger owns one datadir and its permanent mapping set through the data lock; and
- that tagger also owns the selected spool's process queue through the mailroot lock, preventing another datadir from consuming the same queue.

Acquire the tagger data lock first, create the mailroot if needed, then acquire its queue lock, before requested tracing, configuration, scanning, or message work. Hold both throughout the invocation, including completion of owned work on shutdown. If workroot creation or queue-lock acquisition fails, release the data lock through normal cleanup and fail startup without touching queue messages. The sorter acquires only its own lock. Contention is a startup failure: report it and return nonzero, without waiting for the other owner. Persistent lock files may remain after exit; their existence alone does not mean the resources are owned. Sharing a datadir across two spools or a spool across two datadirs therefore conflicts for taggers.

The sorter and tagger do not acquire or wait for one another's locks and are expected to run concurrently. A sorter one-shot conflicts with its sorter watcher, and a tagger one-shot conflicts with its tagger watcher.

Within each singleton:

- process messages sequentially;
- do not launch per-message workers or process two basenames concurrently;
- let only the tagger allocate mappings, make newly published mappings available to later messages, and append tag logs;
- require sender-id record/auth-index administration to occur while the tagger is stopped; and
- retain ownership through completion of the current message during orderly shutdown.

These stipulations eliminate same-program races from the version 1 design; behavior under a second same-role process that somehow bypasses singleton enforcement is not supported.

The atomic visibility requirements for our transitions are the observable state rules in §§4, 7, and 9: ownership removes the plain HDR first, handoff/publication exposes the plain HDR last, a live mapping appears only as one complete directory, and existing destinations are never replaced. SmarterMail's incoming candidates additionally require the readiness gate in §7.3.1. The supported same-volume NTFS installation makes individual moves atomic. Locking and move choices are specified in `implementation.md`.

## 12. Future scope

Deferred features are tracked in `future.md`. They do not weaken the version 1 behavior above and require their own design decision before implementation.
