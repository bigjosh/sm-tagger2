# sm-sorter / sm-tagger — Behavioral Specification

This document defines **what** version 1 must do and **why** those behaviors exist. It intentionally does not prescribe classes, APIs, operating-system calls, data structures, or test seams. [`implementation.md`](implementation.md) defines **how** the current implementation satisfies this contract. If the two documents conflict, this specification wins and the implementation guide must be corrected.

All addresses, hostnames, and IP addresses in the examples are sanitised: `example.com` is the sender's own domain, while `example.net` and `example.org` stand in for correspondents.

External client, SmarterMail, provider, and deployment behaviors on which this design depends are tracked in `assumptions.md`. A client/version/path is supported for enrolled accounts only after its applicable empirical assumptions have current evidence or an explicit accepted-risk disposition. Implementation choices cannot silently weaken the behavior, security boundary, or failure outcomes defined here.

---

## 1. What this system does

A two-process outbound-mail rewriter for SmarterMail. `sm-sorter.exe` is the small, failure-isolated classifier at the SmarterMail queue boundary: it reads only enough of each HDR to prove that mail is unrelated and pass it through unchanged, or diverts mail from an enrolled authenticated address into the tagger queue. `sm-tagger.exe` consumes only that private queue and performs the full parsing, mapping, fan-out, rewriting, and publication work. For a qualifying message, it replaces every supported sender-identity occurrence of that account's **private-address** with a **tag-address** that is unique to the recipient and remembers the mapping.

This split deliberately limits failure propagation. If the tagger is stopped, crashed, or unable to validate its configuration, enrolled mail accumulates in `<datadir>\process\` while positively classified no-auth and non-enrolled mail continues from SmarterMail `proc\` to `spool\`. The sorter remains a critical singleton: if the sorter itself or its watcher stops, all mail through the selected `proc\` stops. Shared-volume exhaustion and other host-wide failures can still affect both paths, so this is logical failure isolation rather than complete resource isolation.

Alice receives mail using `joe-a3f92@example.com`; Bob receives mail using `joe-c17b4@example.com`. If mail later arrives at the allocated `joe-a3f92@example.com`, its exact tag record associates that address with Alice's recipient-id. That is useful attribution evidence that the tag escaped its intended path, not proof that Alice personally disclosed it (§1.4).

Three properties make it work:

- **One tag-address per person, forever.** The same recipient always gets the same tag, so it is stable enough to live in their address book.
- **Only the address changes.** The display name in `From:` is passed through byte-for-byte, so the user can set or change it freely in their mail client and this program neither knows nor cares.
- **The private-address is removed from the defined sender-identity surface.** On every successfully tagged output, no parsed addr-spec equal to the current private-address remains in the supported sender fields listed in §8. Content outside that explicit surface is preserved and is not claimed to be secret-free.

Version 1 independently enforces one sorter for the selected spool `proc\` and one tagger for the datadir/process queue and permanent mapping set (§11). The sorter never waits on the tagger's singleton or configuration validation. A one-shot invocation cannot run beside the corresponding production watcher, but the sorter and tagger are expected to run concurrently.

### 1.1 Messages with multiple recipients

These messages require special handling because each recipient should receive the tag-address associated with their recipient address.

To ensure this, messages with several recipients are **split into one message per recipient** before tagging. Every tagged message must have exactly one EML `From:` field containing exactly one mailbox, regardless of recipient count or `Reply-To:` handling.

When the message has no `Reply-To:`, we generate an additional stable tag-address for the complete recipient set and add it as `Reply-To:`. When an existing valid `Reply-To:` contains the private-address, its matching mailboxes become that same group tag while deliberate non-matching mailboxes are preserved. An existing `Reply-To:` with no private-address is preserved byte-for-byte and suppresses group-tag creation.

This is a best-effort reduction in accidental disclosure by ordinary reply-all clients, not a guarantee about every client, manual reply, forward, quotation, or downstream transformation. A group tag is evidence for its complete recipient set only (§1.4); it cannot identify one member as the source of a disclosure.

### 1.2 Authenticated enrollment and the private-address

An account opts in by having a sender profile (§5), identified permanently by an opaque **sender-id**. The profile associates its authenticated `auth:` address with **the private-address that its client uses as a sender identity**. The private-address is deliberately *not* the user's everyday address. It is a non-public, randomly generated string like `k7p2qx@example.com`, configured in the mail client. Generate it from a cryptographically secure random source and record that fact during onboarding; version 1 validates its addr-spec and uniqueness but cannot infer how it was originally generated and imposes no numeric entropy minimum.

The `auth:` value selects the account's sender profile but is never rewritten. If the current private-address occurs in at least one supported sender-identity field, tagging is activated and every exact parsed sender-identity occurrence of that private-address is replaced. Legitimate non-matching sender identities are preserved. If neither a current nor retired private-address occurs in any supported sender-identity field, the message passes through unchanged and no tag is created; a retired match fails closed (§7.3).

Consequences worth understanding before building:

- **`auth:` is the trusted outbound classifier and profile lookup key.** The sorter reads it but never modifies it. Inbound mail normally has no `auth:` and therefore cannot enter the tagger queue merely by spoofing the private-address.
- **The sender-id, not an email address, is the persistent identity.** Recipient tag mappings are keyed by sender-id, so changing the auth-address or rotating the private-address does not change existing tags.
- **Address identity is ASCII case-insensitive throughout version 1.** Complete addr-specs are canonicalized to lowercase for comparison and mapping (§7.1.1), while original message spelling is preserved except at explicit rewrite sites.
- **Version 1 supports one stable sender profile per authenticated account.** `<datadir>\senders\<canonical-auth-address>\` is a permanent routing-index entry containing `sender-id.txt`; the complete profile remains keyed by that stable sender-id under `profiles\`. Two profiles may not claim the same current or retired auth-address.
- **An authenticated account may still send other identities.** A configured `auth:` with no private-address occurrence in a supported sender-identity field is an ordinary pass-through message.
- **A partial match is allowed.** For example, the EML `From:` may use the private-address while the envelope sender uses a different address. Replace the matching `From:` and preserve the non-matching envelope sender.
- **The trigger must be a sendable identity on the account** — normally an alias, and for a provider "send as" relay it needs that provider's verification. Confirm this before choosing the string.
- **Mail authentication is a bring-up gate, not a local parser guarantee.** The private-address and generated tag-address may use different domains, but every final tag domain and submission route must pass the DKIM/SPF/DMARC checks in §8 before the sender is enabled. Repeat them after any relevant client, SmarterMail, DNS, relay, signing, size-limit, or routing change.
- **Version 1 does not provision one alias per tag.** Each sender route must prove during bring-up that SmarterMail and every downstream relay accept freshly generated, never-provisioned individual and group tags in every role where the algorithm or an approved client path can place them. A route that requires per-local-part alias or send-as verification is unsupported.
- **The private-address and auth-address are rotatable without changing identity.** Keep the sender-id unchanged, move each replaced value to the appropriate retired-address file, publish any new permanent auth index, update the current value, restart the tagger, and only then update/use the client identity. Existing recipient tags remain attached to the same sender-id.
- **Tagger sender configuration takes effect only at startup in version 1.** The tagger does not watch or reread profiles while running. The sorter checks the canonical auth-address directory on each classified message, so atomically publishing that directory immediately enables diversion, not successful tagging. Stop the tagger before changing a profile, publish every required auth index entry only after its pointer file is complete, restart the tagger, and only then let the client use the new identity. A queue item not represented consistently in the configuration effective for that invocation fails closed; it is never passed merely because the tagger does not recognize it.
- **Auth routing entries are permanent.** Never delete a current or retired auth-address directory: absence is the sorter's positive evidence that a valid auth is not enrolled. Auth rotation publishes a new directory pointing to the unchanged sender-id and leaves every former directory in place; the profile marks the old value retired so the tagger fails it closed.
- **Outbound MDN responses are disabled per sender by default policy.** Set that sender profile's `allow-mdn.txt` to `true` only after the empirical MDN checks in `testing-plan.md` are recorded in `assumptions.md`. Because the flag is keyed by sender-id rather than client, every client/version/platform/submission path capable of using that profile's auth-address must be approved, or every unapproved path must be disabled or prevented from generating MDNs. The flag is enforced for recognized MDNs, but its value takes effect only at startup: changing it requires a restart in version 1.

### 1.3 Version 1 inbound scope and catch-all prerequisite

Version 1 performs **no inbound tag lookup, recipient transformation, automatic attribution, blocking, or rerouting**. The sorter sends a safely classified no-auth message directly from `proc\` to `spool\` byte-for-byte even when an envelope recipient is a known tag-address. Automatic inbound processing is deferred to FUT-002 in `future.md`.

Instead, every sender uses a dedicated tag-address domain or subdomain, such as `reply.example.com`, configured in SmarterMail with a catch-all that accepts every generated tag-address and delivers it to that user's intended collection mailbox. The domain is reserved for that user's tags so generated addresses cannot collide with ordinary mailboxes or aliases. This catch-all is an external deployment prerequisite, not something `sm-tagger` creates or manages.

Before enabling a sender profile, perform an end-to-end delivery check proving that a representative generated-style address at its tag domain:

1. is accepted by SmarterMail;
2. reaches the intended collection mailbox;
3. retains the original SMTP envelope recipient in data the operator can inspect, even when the visible EML `To:` header is absent or different; and
4. does not enter a forwarding or processing loop.

Replies and DSNs to tag-addresses depend on this catch-all behavior. Version 1 attribution is manual: the operator obtains the original envelope recipient and looks up the exact `tag-addresses\<tag-address>\recipient-id.txt` record. The accepted tradeoff is that catch-all domains also accept fabricated or unallocated local parts and therefore receive additional spam; version 1 does not distinguish or reject them automatically.

### 1.4 Secrecy and attribution boundary

The version 1 private-address secrecy guarantee is deliberately scoped and testable: after successful tagging, the current private-address does not remain as a parsed addr-spec in any supported sender-identity field listed in §8. The program does not claim that the private-address is absent from message bodies, arbitrary or unknown headers, trace fields, embedded messages, encoded content, or other regions outside that rewrite surface. Those bytes are preserved rather than searched, decoded, rewritten, or rejected.

Version 1 does not parse or rewrite MIME bodies, including the human-readable, `message/disposition-notification`, and returned-message parts of a read receipt/MDN. When `allow-mdn.txt` is `false`, it classifies standard MDNs from the top-level EML `Content-Type:` (§6.4) and a recognized outbound MDN fails closed before tag lookup or publication—even if no private-address appears in a supported header. When it is `true`, Content-Type classification is unnecessary and the message follows the ordinary header-only trigger and rewrite rules; the flag does not extend the secrecy surface or make body content safe. Enabling it is an operator assertion that current evidence for every MDN-capable path sharing that sender profile found no current or retired private-address outside the supported header surface, with any unapproved path disabled. Read receipts must also remain disabled in the client/server until that evidence exists, because nonstandard or no-auth MDNs may not be attributable to a sender profile at runtime. Full MIME-aware MDN rewriting is deferred to FUT-003 in `future.md`.

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
sm-sorter.exe <datadir> [-l <logfile>] [-v] <spooldir> [<basename>]
sm-tagger.exe <datadir> [-log] [-keep] <spooldir> [<basename>]
```

| arg | meaning |
|---|---|
| `<datadir>` | root of the auth index, stable profiles, tag database, and `process\` queue (§5) |
| `<spooldir>` | the SmarterMail spool folder. The sorter input is `<spooldir>\proc\`; both executables publish final mail into `<spooldir>\` |
| sorter `<basename>` | **optional.** Given, sort that one plain pair from `proc\` and exit. Omitted, watch `proc\` |
| tagger `<basename>` | **optional.** Given, handle that one plain pair from `<datadir>\process\` and exit. Omitted, watch `process\` |
| `-l <logfile>` | sorter only: append one best-effort terminal result per processed message to the selected email log; readiness deferrals are excluded (§5.1) |
| `-v` | sorter only: write verbose lifecycle, scan, routing, and file-operation debugging to standard output (§5.1) |
| `-keep` | tagger only: retain copies of original and resultant files in `process\` as `.in` and `.out` |
| `-log` | tagger only: create/open `<datadir>\log.txt` and emit the execution trace in §5.1 |

Each program's options must appear directly after `<datadir>`, before `<spooldir>`, to avoid confusion with basenames that may start with a dash. Sorter `-l <logfile>` and `-v` are independent, may appear in either order, and may each appear at most once. The log path is required after `-l`; a relative path resolves against the process working directory. Tagger `-log` and `-keep` retain their separate meanings. The sorter has its own small diagnostic writer and no dependency on the tagger's execution trace, full EML parser, tag mapping, or sender-profile loader. Its retained HDR suffix, best-effort parent `.sort.err`, and standard-error diagnostics remain available without either sorter option.

An invalid argument count or other argument-validation error prints a concise error, one usage block, argument descriptions, and quoted-path examples to standard error, then exits with status 1 before acquiring a singleton, opening a requested log, or touching queue files. The hints explain that omitting `<basename>` starts watch mode and show the applicable option order.

A command-line `<basename>` is one nonempty literal Windows filename component, not a path; leading hyphens are permitted. A value containing a separator, rooted/drive syntax, a reserved or otherwise unusable component spelling, or `.`/`..` is an invocation failure before any queue message is touched. Watch mode obtains basenames only from direct-directory enumeration.

`-keep` accumulates files in `process\` that must be manually cleared.

For either executable, the presence of `<basename>` selects one-shot mode; omission selects watch mode. One-shot mode follows that executable's normal input and state rules and is not a repair/retry command. It never resumes a suffixed sorter or tagger state.

Without tagger `-log`, the tagger never creates, opens, inspects, appends, rotates, truncates, or deletes its `log.txt`. Without sorter `-l`, the sorter opens no email log; without sorter `-v`, it emits no debug output. Sorter operation has no implicit dependency on tagger `log.txt`; a tagger log failure cannot stop sorting. The operator selects a dedicated sorter log file separate from mail, configuration, mapping records, and the tagger's trace.

A sorter singleton/watcher failure returns nonzero and stops all flow through the selected `proc\`. A failure confined to one sorter-owned message leaves that message inert and does not stop the sorter from handling later EML candidates. Tagger lock/configuration startup failure or fatal watcher failure returns nonzero but does not affect the independent sorter; enrolled mail remains in `process\` while unrelated mail continues. Logging failures print to standard error and do not stop either process, hold the current message, or change its exit status. One-shot mode returns nonzero when its requested message is not ready or does not complete successfully for a mail-processing or contract reason.

### 3.1 Watch mode

Entered by omitting that executable's `<basename>`. The sorter watches `proc\` for regular files whose final extension is exactly `.eml`; the tagger watches `<datadir>\process\` for regular files whose final extension is exactly `.hdr`, both compared ASCII case-insensitively. Further-suffixed files are excluded from discovery. A sorter-selected EML still needs its matching plain HDR before ownership (§4).

The persistent `proc\sm-sorter.lock` and `<datadir>\sm-tagger.lock` files are not messages. Each program holds its corresponding singleton for its complete invocation (§11).

Sorter watch mode discovers final plain EML files in `proc\`; tagger watch mode discovers plain HDR files in `process\`. Both discover eligible backlog at startup and remain correct when filesystem notifications are missed, coalesced, reordered, duplicated, or overflowed. While otherwise idle, an eligible input must be reconsidered within 30 seconds even if no useful notification arrives. The notification and rescan mechanism that provides these outcomes belongs in `implementation.md`.

Each scan sorts current basenames by ordinal comparison and processes them sequentially. A sorter-selected EML or matching plain HDR that disappears before ownership is stale and skipped; the tagger likewise skips a vanished plain HDR before its claim. Once ownership succeeds, a missing EML is a message failure rather than a reason to wait. Enumeration or monitoring failure that prevents reliable continued discovery is fatal to that executable; a message-local failure is not.

A plain HDR in SmarterMail's `proc\` is not a sorter trigger: it is written first and may exist for an attempt that never produces an EML. The sorter selects final `.eml` files, checks the EML before any HDR access, then reads the matching same-basename HDR and rejects status `Failed` as specified in §7.3.1. Other status values are opaque, not a readiness whitelist. At the tagger boundary, the sorter's EML-first/HDR-last handoff establishes completeness for a plain HDR in `process\`.

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

SmarterMail deposits pairs in `<spooldir>\proc\`. The sorter sends a safe pass-through pair directly to `<spooldir>`, or sends a potentially enrolled pair to `<datadir>\process\`. The tagger sends its unchanged or transformed outputs from `process\` to `<spooldir>`. Both input directories are flat and all per-message state is represented by filename suffixes.

> ### Discovery and publication triggers
> **The sorter discovers final `.eml` files in `proc\`; the tagger discovers plain `.hdr` files in `process\`.** The sorter never treats an HDR-only arrival as ready. Files with further suffixes are not discovery candidates. A complete plain HDR published into `process\` or `spool\` is the receiving consumer's trigger.
>
> Our ownership and publication transitions follow this rule:
> - **Taking ownership** — rename the `.hdr` **first**, removing the plain header before moving its EML.
> - **Handoff/publication** — move the `.eml` **first** and the `.hdr` **last**, so a trigger appears only after its pair is complete.

Sorter states in `proc\`:

| files | meaning |
|---|---|
| `x.hdr` without `x.eml` | early or abandoned SmarterMail attempt; not a sorter candidate |
| `x.eml` + `x.hdr` | final EML discovery candidate; read the matching HDR and reject `Failed` before routing |
| `x.hdr.sort` + `x.eml` | sorter-owned pair before either destination move |
| `x.hdr.sort` with destination `x.eml` | first handoff/publication move succeeded; HDR move is incomplete/failed |
| `x.sort.err` | best-effort sorter diagnostic; the HDR/EML locations remain authoritative |

Tagger states in `<datadir>\process\`:

| files | meaning |
|---|---|
| `x.hdr` + `x.eml` | complete sorter handoff, not yet claimed by the tagger |
| `x.hdr.start` + `x.eml.start` | claimed original pair; classification has not yet committed to fan-out |
| `x.hdr.break` + `x.eml.break` | immutable claimed parent after tagging/fan-out begins |
| `x.hdr.in` + `x.eml.in` | files as originally accepted; source kept for debugging (only when `-keep` is specified) |
| `x.hdr.process` + `x.eml.process` | child output under construction; incomplete and never publishable |
| `x.hdr.pend` + `x.eml.pend` | complete, closed, validated child pair ready for publication |
| `x.hdr.out` + `x.eml.out` | retained copy of final output (only when `-keep` is specified) |
| `x.hdr.err` + `x.eml.err` | original pair retained after a `From:` contract error; inert and never automatically retried |
| `x.err` | tagger parent-level failure diagnostic; related files retain their exact state suffix |

Pass-through output keeps the SmarterMail-assigned input basename. Each tagged child keeps traceable continuity by using `<input-basename>-<n>`, where *n* is its canonical child ordinal (§7.4). Version 1 deliberately does not replace this with an unrelated random output id because the common basename is useful when correlating files with SmarterMail logs.

The sorter checks readiness and classifies the HDR while it is still plain, makes the HDR inert as `.hdr.sort`, and then sends the pair either to `spool\` or to `process\`. The EML reaches the destination before the plain HDR. A crash or error between those two transitions leaves an EML at the destination and the inert HDR in `proc\`; the sorter never rolls the EML back.

The tagger watches only plain HDRs in `process\`, so it cannot observe a sorter handoff before its EML is present. Once claimed, the tagger's `.start`/`.break`/`.process`/`.pend` states remain inside `process\` until a final pair is published. `spool\` receives only complete outputs whose HDR arrives last.

Every individual state transition must be atomic in the supported deployment and must not replace an existing destination. The configured `proc\`, `spool\`, datadir, and `process\` locations are therefore required to be on the same ordinary local NTFS volume. Correct placement and SmarterMail basename uniqueness are trusted installation conditions; version 1 does not preflight them. The platform operations used to implement these transitions are defined in `implementation.md`.

**VERSION-SENSITIVE-004:** Recheck move atomicity and sharing/singleton behavior after Windows Server, .NET, security-software, or storage-topology changes.

---

## 5. Data directory layout

```
<datadir>\
    sm-tagger.lock
    log.txt                         (only created/opened with -log)

    senders\
        .staging\
            <staging-id>.authtmp\
                sender-id.txt
        <canonical-auth-address>\
            sender-id.txt
    profiles\
        <sender-id>\
            auth-address.txt
            retired-auth-addresses.txt
            private-address.txt
            retired-private-addresses.txt
            from-template.txt
            allow-mdn.txt
    process\
        <sorter-handoff and tagger state files from §4>
    staging\
        <staging-id>.tagtmp\
            sender-id.txt
            recipient-id.txt
            tag-log.txt                 (best effort; may be absent or damaged)
    tag-addresses\
        <tag-address>\
            sender-id.txt
            recipient-id.txt
            tag-log.txt                 (best effort; may be absent or damaged)
```

- **`senders\<canonical-auth-address>\`** — the permanent sorter routing index. Folder existence diverts that canonical auth-address; its closed `sender-id.txt` points to the stable profile. Every current and retired auth-address retains one such directory forever.
- **`profiles\<sender-id>\`** — one complete tagger profile per stable sending identity (§1.2). The folder name is a canonical lowercase UUID in `8-4-4-4-12` form, generated once and never changed, reused, or derived from an email address.
- **`senders\.staging\<staging-id>.authtmp\`** — an unpublished auth-index entry. Populate and close its `sender-id.txt`, then rename the directory without replacement to the canonical auth-address path. The sorter never consults `.staging`; tagger startup reports but does not load leftovers.
- **`process\`** — the sorter-to-tagger queue and all tagger per-message working state. A plain HDR is a complete handoff; suffixed states are inert.
- **`staging\<staging-id>.tagtmp\`** — an inert, unpublished tag record being created. The staging-id is a fresh canonical lowercase UUID used only to avoid name collisions. Nothing under `staging\` is ever a live mapping.
- **`<tag-address>\`** — one folder for each tag-address we have ever generated.

Write auth-address index, sender-id, and tag-address folder names in canonical lowercase. Auth-address folder names additionally obey the safe-component rules in §10; an otherwise valid auth-address that cannot be a literal Windows component is not enrollable in version 1.

### 5.1 File formats

**Best-effort logs and accepted corruption.** Logging is diagnostic and must not control mail flow. A failure to create, open, encode, write, flush, or close a log prints to standard error and processing continues with the current message and later work. It never causes a message hold, process termination, failed mapping allocation, or nonzero exit by itself. The formats below define new entry bytes, not an integrity requirement on existing contents. Corrupted logs—including malformed UTF-8, incomplete lines, and entries joined by a later append to an unfinished line—are acceptable in version 1. This loss of diagnostic quality is preferable to the complexity of validation, repair, recovery separators, or tracking unusable mappings. Append at the existing end without reading or validating prior contents. Do not repair corruption or block work because a log is absent or unusable. Logs remain diagnostic evidence, never mapping or message-state authority.

**Sorter email log (`-l <logfile>`)** — an optional append-only record of terminal sorter outcomes. After acquiring the sorter singleton, attempt to open the selected file for the invocation. Do not create its parent directory. For each terminal message result, attempt one UTF-8 line without a BOM, terminated by CRLF, with this field order:

```text
<UTC timestamp> basename="..." result=PASS|DIVERT|ERROR auth="..." reason="..." operation="..." hdr="..." eml="..." destination="..." error="..."
```

The timestamp format is `yyyy-MM-ddTHH:mm:ss.fffZ`. Quote and escape string values using the rules below; use `"-"` for unavailable values. `auth` is the canonical authenticated address when known. `hdr` and `eml` name the last-known/current message paths; `destination` names the attempted destination when applicable. These fields describe known state and attempted operations, not a fresh existence check. `error` preserves the applicable error detail when available.

`PASS` is recorded only after unchanged publication into the spool completes; its reason is `NO_AUTH`, `UNENROLLED_AUTH`, or `UNENROLLABLE_AUTH`. `DIVERT` is recorded only after handoff into `process` completes, with reason `ENROLLED_AUTH`. `ERROR` records the failed terminal outcome, with reason `UPSTREAM_FAILED`, `UNSAFE_HDR`, `AUTH_LOOKUP_FAILED`, `HDR_READ_FAILED`, `INPUT_READINESS_FAILED`, `HDR_CLAIM_FAILED`, `HANDOFF_FAILED`, or `MISSING_HDR` as applicable. A fatal failure to claim a selected HDR and an explicitly requested missing one-shot HDR receive an `ERROR` attempt. A stale watcher entry receives no email-log record. A candidate deferred because its EML is unavailable or its HDR is busy is not a terminal message result and receives no email-log record in either mode. No startup, shutdown, scan, or other process-lifecycle lines are written to this file.

A record is attempted after the message's terminal result is known; a crash or logging failure can leave it absent or partial even after mail has moved. `PASS` and `DIVERT` prove neither final delivery nor downstream tagger success. On formatting, open, append, flush, or close failure, report the log failure to standard error, disable only that file for the rest of the invocation, and preserve the current mail outcome, later processing, and exit status. Do not reopen, retry, rotate, truncate, repair, or recursively log the failure. A later invocation may try opening the file again. Operators may archive or clear it while the sorter is stopped.

**Sorter console debugging (`-v`)** — best-effort standard-output lines prefixed `DEBUG`. Lifecycle, queue scans, readiness deferrals, routing decisions, file-operation intents/results, and stale entries identify their `event`; a terminal message summary uses the email record's result fields. A readiness deferral uses `event=DEFER` with reason and input paths. This output is independent of `-l` and includes no EML contents or tagger-only configuration/mapping inspection. Without `-v`, no debug output is attempted. A console formatting/write failure disables debug output for the invocation and attempts a standard-error report, without affecting mail or the email log. Ordinary errors still go to standard error regardless of both options.

**Tagger `log.txt`** — the optional append-only operational execution trace. It exists as a program output only when tagger `-log` is present. Encode each new event as UTF-8 without a BOM and terminate it with CRLF. Every new event begins with this stable, searchable prefix and may continue with any number of human-readable `name=value` details:

```
2026-09-01T14:30:12.123Z run=0d3b3c3d-6ca6-4c11-84a0-b119576fa3c8 seq=42 context="message-42" event=STATE_RESULT step="7.5.2" result=ERROR operation="move" source="D:\\sm-tagger-data\\process\\message-42-1.eml.pend" destination="D:\\SmarterMail\\Spool\\message-42-1.eml" error="destination exists" residual="EML=.eml.pend; HDR=.hdr.pend; later children=.pend; parent=.break"
```

Use UTC with exactly millisecond precision for the timestamp. `run` is one fresh canonical lowercase UUID for the invocation. `seq` begins at 1 and increases for each attempted event. `context` is the input message basename or `-` for a process-wide event. `event` and `result` are concise uppercase ASCII tokens; `step` identifies the applicable behavior when useful. Keep field names stable enough for ordinary text search, but favor understandable diagnostics over a rigid closed vocabulary or names tied to a particular framework API.

Quote every arbitrary string value. Within it, escape `\` as `\\`, `"` as `\"`, CR as `\r`, LF as `\n`, HTAB as `\t`, and any other control or byte not representable as valid UTF-8 as `\xHH`. Integers and fixed uppercase tokens may be unquoted. These rules prevent newly encoded values from introducing additional physical lines while retaining the complete relevant value; they do not establish the integrity of existing log contents.

The sorter email log and console debugging, tagger `log.txt`, parent diagnostics, retained `-keep` artifacts, and standard-error diagnostics are protected operational data. The programs perform **no redaction**. Any current or retired private-address, auth-address, sender-id, recipient-id, tag-address, basename, full path, parsed metadata/header value, error text, or other datum relevant to the event may and should appear. “Relevant” means read, derived, compared, selected, rejected, mutated, or returned by the program; it does not require dumping an EML body or other bytes the program never examines. These outputs are not sanitized or safe for release outside the trusted operator/security zone.

With `-log`, record enough information for a human to reconstruct the path taken: startup/options, configuration loading, queue scans, message ownership, auth/profile selection, important parsed and canonical values, mapping lookup/allocation, branch decisions, child order, publication, shutdown, and failures.

Immediately before a persistent state transition, attempt a `STATE_INTENT` event containing the operation, paths, expected transition, and known pre-operation state. After the transition completes or fails, attempt a `STATE_RESULT` containing the operation outcome and the residual state known from that outcome. Use `UNKNOWN` or `AMBIGUOUS` when a fact is not known; the trace must not claim facts that the operation did not establish. Failure to record either event does not prevent the next normal transition. Logging its own output is not recursively logged.

After the tagger singleton is held and before configuration is loaded or messages are examined, `-log` attempts to open `log.txt` as the invocation's append-only trace. Only that tagger writes the file; other processes may read it but must not write to it while the tagger runs. Do not automatically rotate, rename, truncate, or delete it. An operator may archive or clear it only while `sm-tagger` is stopped and accepts that a long logging session can grow without an internal bound. The file-lifetime and append mechanics belong in `implementation.md`.

If opening or using the requested `log.txt` fails, attempt a standard-error diagnostic beginning `ERROR logging to "<full-log-path>":` with the failed event and known residual state when available. Disable file tracing for the rest of that invocation and continue normal startup, current-message processing, later messages, and shutdown. Do not repeatedly reopen or retry the failed trace; a later invocation may try again. A close failure is likewise reported without changing the mail outcome or exit status. Standard error is best effort and is not recursively logged; inability to write it creates no additional fallback. The managed operations used by logging are defined in `implementation.md`.

Independently of sorter `-l`/`-v` or tagger `-log`, attempt a complete human-readable standard-error diagnostic for startup/configuration failures, fatal watcher failures, runtime message-contract errors, and all logging or diagnostic-file failures. Standard error is a best-effort operator channel, not a durable journal; inability to write it does not create another fallback. Out-of-band collection and delivery of these messages is deferred to FUT-005 in `future.md`.

**`sender-id.txt`** — In an auth-index directory, points that canonical auth-address to its stable profile. In a tag-address directory, identifies the profile that owns the permanent mapping. In both roles, trim permitted terminal newline bytes, require one canonical lowercase sender-id and no other content, and require that it identifies an existing `profiles\<sender-id>\` directory. The auth-index file is immutable after atomic publication.

**`auth-address.txt`** — In a sender profile, holds exactly one current authenticated account address. Trim newline characters when reading.

**`retired-auth-addresses.txt`** — Required but permitted to be empty. Holds zero or more former auth-addresses, one per non-empty line. A message carrying one of these values fails closed rather than being treated as mail from an unconfigured account.

**`private-address.txt`** — In a sender profile, holds exactly one current private-address. Trim newline characters when reading.

**`retired-private-addresses.txt`** — Required but permitted to be empty. Holds zero or more former private-addresses, one per non-empty line. For the selected sender profile, a message containing one of these values in a supported sender-identity field fails closed rather than passing through and disclosing it.

All current and retired address files are parsed, validated, and canonicalized under §7.1.1 at startup. An address may occur only once by canonical identity, in exactly one role and one sender profile; case-only variants are duplicates. Missing, unreadable, malformed, or duplicate values are startup configuration failures.

**`allow-mdn.txt`** — Required per sender profile. After trimming terminal CR/LF bytes only, its complete contents must be exact lowercase ASCII `false` or `true`; spaces, comments, other casing, additional lines, and other values are invalid. `false` is the initial and recommended value. `true` permits recognized outbound MDN responses for this sender and may be deployed only after the evidence/approval gate in §1.4. This is configuration, not a claim that the program rewrites MDN bodies.

**`recipient-id.txt`** — Holds the canonical recipient-id defined in §7.1.2. The writer emits its exact bytes without a newline. On load, trim terminal CR/LF bytes, decode and validate every escape and address, require strictly increasing canonical address order, and require re-encoding to reproduce the trimmed bytes exactly. Empty, malformed, alternatively encoded, duplicate, or unsorted identity data is invalid.

**`process\<basename>.err`** — the tagger's protected, unrestricted UTF-8 diagnostic text for a human, never parsed. **`proc\<basename>.sort.err`** is the corresponding best-effort sorter diagnostic. The first line is a one-sentence reason. Follow it with every relevant evaluated value, operation/error result, and known source/destination state needed to reconcile the message. Apply the same string escaping rules when an included value would otherwise span lines or contain controls. Neither diagnostic is sanitized or safe for release outside the trusted filesystem boundary.

The `process\<basename>.hdr.err` and `.eml.err` files are retained original mail bytes, not diagnostic text. They coexist with the parent `<basename>.err` reason file for the `From:` contract-error outcome in §7.3.2.

**`tag-log.txt`** — the best-effort append-only publication-attempt diagnostic for a mapping. During allocation, attempt to create an empty file; report failure to standard error and continue creating the mapping. Allocation and lookup do not add an entry. A later append creates the file if absent when possible. Encode each new entry as UTF-8 without a BOM using exactly this format, including its terminal CRLF:

```
YYYYMMDDTHHMMSSZ <output-basename>\r\n
```

The timestamp is UTC from immediately before publication, with exactly four-digit year, two-digit month/day/hour/minute/second, literal `T`, and literal `Z`; for example, `20260901T143012Z message-42-1`. Its digits must identify a real UTC calendar second, including normal leap-year rules and excluding leap-second value `60`. One ASCII space separates it from the complete child output basename, and the basename occupies the rest of the line. It must be nonempty and contain no CR, LF, or NUL.

For each tagged child, immediately before publishing its EML into `spool\`, attempt to append the identical entry once to every distinct tag mapping referenced by the child: always its individual tag, plus its group tag when group-tag handling was used. Attempt distinct logs in ordinal tag-address order before moving the EML. Apart from best-effort `log.txt` trace events and standard-error diagnostics about those operations, no other mail or mapping transition intervenes. If an append fails, attempt a standard-error diagnostic beginning `ERROR logging to "<full-tag-log-path>":`, leave any appended bytes in place, and continue with the remaining log attempts and that same child's EML/HDR publication. Do not retry the failed append for that child or undo any other append. Later children and fresh messages may attempt the same log normally. The append mechanics belong in `implementation.md`.

An intact entry means only that publication of that output basename was about to be attempted. It does not prove that either move succeeded or that SmarterMail delivered the message. Corruption can obscure, truncate, or merge entries, and failed log operations can leave no entry at all even when a child is successfully published. A valid permanent mapping may have an absent, empty, or damaged log. Reconcile uncertain mail using actual retained parent/child states and SmarterMail records; neither the presence nor the absence of a log entry proves publication or delivery. Failed mail is never resumed automatically, so no automatic replay adds a duplicate entry. A machine/power/storage loss is outside the filesystem reconciliation guarantee (§9.6).


### 5.2 Data coherency

The sender-id is the permanent referential key. Never rename or reuse a `profiles\<sender-id>\` folder. Every auth-index and tag-address `sender-id.txt` must identify an existing profile; otherwise tagger startup fails. A profile may not be deleted while an auth index or tag record refers to it.

Every current and retired auth-address in a profile must have one canonical `senders\<auth-address>\sender-id.txt` pointing to that profile, and every published auth-index directory must name an address listed as current or retired by the referenced profile. Missing, duplicate, orphaned, or conflicting entries fail tagger startup because they make sender selection ambiguous. The sorter deliberately does not perform this global validation; folder existence alone is reason to divert, so a tagger configuration failure cannot stop unrelated sorting.

A tag mapping becomes live only when a staging directory containing complete, closed `sender-id.txt` and `recipient-id.txt` identity files is atomically renamed directly under `tag-addresses\`. Those identity files define completeness; `tag-log.txt` is best effort and may be absent or incomplete. Never publish a final tag directory before its identity files are complete. Entries left under `staging\` are inert evidence of interrupted work: report them prominently at startup, never load, publish, retry, or delete them automatically, and leave them for manual disposition. Their presence alone does not prevent startup.

Every live tag directory must identify one usable tag-address and contain readable, valid `sender-id.txt` and `recipient-id.txt` identity files. Its sender-id must resolve to an existing profile. Duplicate `(sender-id, recipient-id)` keys, incomplete identity records, or malformed identity data fail startup because they could create two tags for one mapping or select the wrong profile. Do not require, open, or validate `tag-log.txt` during startup. A missing, unreadable, unwritable, or corrupted tag log cannot invalidate a mapping or fail startup; an actual later logging-operation failure follows §5.1.

The tagger fixes its profile/auth-index configuration at startup and performs no profile reads during message processing. For a supported update, stop the tagger, complete the profile and any new auth index, publish the index as one complete directory, and restart the tagger before the client uses the identity. The sorter may remain running and diverts once the final auth directory exists. Retired index directories are never removed. If an item reaches `process\` but its auth is absent, retired, or inconsistent in the configuration effective for that invocation, it fails closed; the tagger never applies the sorter's unconfigured-auth pass rule.

### 5.3 Backup, restore, layout changes, and retention

Version 1 has no schema/version marker and performs no automatic migration. A future release that changes persistent meanings or layout must define an explicit offline migration and rollback procedure before using an existing datadir.

Neither executable creates backups. A consistent datadir backup requires both programs, SmarterMail processing, and administrative updates to be stopped while the complete datadir—including `process\`, profiles, indexes, mappings, logs, and staging evidence—is copied as one unit. `proc\` and `spool\` are outside that backup.

Restoring an older backup can reintroduce uncertain queue work and omit tag mappings or history created after the checkpoint. Restore and reconciliation must occur offline. Before the restored datadir may be used, the operator must account for every post-checkpoint mapping, tag-log append, profile change, and retained state using surviving current data and external records. If every emitted post-checkpoint mapping cannot be recovered or otherwise accounted for, do not treat the restored tag namespace as complete and do not resume normal tag minting or attribution from it. Otherwise the same recipient could silently receive a second permanent tag. Version 1 cannot infer or repair that loss.

Neither program automatically ages, retries, rotates, truncates, or deletes permanent indexes, mappings, tag logs, profiles, retired identities, staging leftovers, failure diagnostics, `-keep` artifacts, or partial-message evidence except during the documented normal-success transitions. Operator disposition occurs only while the owning program is stopped and is outside version 1's automatic guarantees.

---

## 6. Input file formats

### 6.1 The `.HDR` envelope

Real sample, sanitised — a two-recipient outbound message. Note the trailing space on line 1, and that the file ends with a **blank line** (`\r\n\r\n`):

```
Written 
joe@example.com
alice@example.net,bob@example.org
retry: 0;08/30/2026 22:43:20
from: joe@example.com
auth: joe@example.com
spamcheck: _DMARC=0,skipped - Authenticated
creationdate: 08/30/2026 22:43:20
notify: alice@example.net=
notify: bob@example.org=
containsLocalDeliveries: False
connectedip: 203.0.113.42
helo: JOEPC
connectedhostname: host-203-0-113-42.example.net
smtpSessionId: 41107744
dmarcResult: Skipped (Authenticated)

```

| line | content |
|---|---|
| 1 | SmarterMail status; after final EML discovery, the sorter rejects exact `Failed` after removing trailing SP/HTAB (§7.3.1). Other values stay opaque. Preserve the original bytes. |
| 2 | envelope MAIL FROM |
| 3 | envelope RCPT TO — **all recipients, comma-delimited, on this one line** |
| 4 … | `key: value` pairs to EOF, then a trailing blank line |

> **⚠ Keys can repeat.** Every occurrence and its original order must survive unless a later rule explicitly removes it. In particular, one `notify:` may exist per recipient.

Fields that matter:

- **Status** — a plain HDR may initially say `Writing`, and its envelope and metadata can still change before the final EML appears. The sorter keys discovery off that EML, then rejects a matching HDR whose first line is `Failed`. It does not require `Written` or interpret other statuses. The tagger receives complete pairs through the sorter's own handoff protocol. The current SmarterMail producer guarantees remain an empirical deployment gate (`VERSION-SENSITIVE-001` in `assumptions.md`).
- **`from:`** — envelope sender metadata. In every current real sample it duplicates line 2, but equality is not assumed. For a tagged message, replace each parsed address that equals the selected private-address and preserve a non-matching address.
- **`notify:`** — observed as one line per recipient, but its exact proprietary semantics remain under empirical test. Each child keeps every well-formed line whose embedded address canonically matches that child and drops every nonmatching or unparseable `notify:` line. **Match by address, never position** (§7.4). Zero or multiple matching lines are permitted and do not fail the message.
- **`auth:`** — the authenticated account. Read it to select the sender profile (§1.2), but **never modify it**. It does not go on the wire and the delivery path needs it.

**What `notify:` may be:** the RFC 3461 DSN `NOTIFY` parameter recorded per envelope recipient, or proprietary per-recipient delivery state. The name, address/value shape, and observed one-to-one ordering support the DSN hypothesis, but every captured value is empty and this is not SmarterMail documentation. Read receipts/MDNs and SMTP delivery-status notifications are separate mechanisms, so the integration matrix in `testing-plan.md` exercises both and records exactly what changes. Version 1 does not depend on the hypothesis: matching metadata is retained, unrelated/unknown metadata is dropped, and its opaque value is never interpreted.

#### `proc\` carries inbound mail too

Confirmed from real spool captures: messages arriving *at* this server pass through `proc\` alongside outbound submissions, and they carry `From:` headers like anything else.

They cannot be diverted for tagging because they do not carry an `auth:` identity. After a successful fast HDR scan proves that `auth:` is absent, the sorter sends the untouched pair directly to `spool\` without opening or reading the EML contents. This remains true even if the EML `From:` or envelope sender happens to equal a private-address, or an envelope recipient is a known tag-address. SmarterMail's per-user dedicated-domain catch-all (§1.3), not either executable, handles version 1 inbound delivery.

### 6.2 The `.EML` message

Standard RFC 5322: header block, one blank line, then the body. Header field names begin in column 0; a folded continuation line begins with whitespace. Real sample, sanitised:

```
Return-Path: <joe@example.com>
Received: from JOEPC (host-203-0-113-42.example.net [203.0.113.42]) by mail.example.com with SMTP
    (version=Tls12
    cipher=Aes256 bits=256);
   Thu, 18 Apr 2024 18:37:32 -0400
From: "Joe" <joe@example.com>
To: <alice@example.net>
Subject: test now
Date: Thu, 18 Apr 2024 18:37:28 -0400
Message-ID: <01d401da91e0$fea06fc0$fbe14f40$@example.com>
MIME-Version: 1.0
Content-Type: multipart/alternative;
    boundary="----=_NextPart_000_01D5_01DA91BF.778ECFC0"
X-Mailer: Microsoft Outlook 16.0
```

Only the header block is ever parsed or modified. **The body is copied through byte-for-byte.**

### 6.3 `from-template.txt`

An **address pattern only** — no display name, no angle brackets. A single `%` marks where the token goes:

```
joe-%@example.com
```

The domain portion is the sender profile's dedicated catch-all tag domain (§1.3). The template must not generate addresses in a domain or local-part namespace used for ordinary mailboxes or aliases.

Parsing is deliberately trivial: **skip leading whitespace, then read to the first whitespace character or EOF.** Anything after that — on the same line or on following lines — is ignored, so notes can simply be written underneath with no comment syntax to define.

It must contain exactly one `%`, which is replaced by the five-character tag token. At startup, validate that substitution with the permitted token alphabet always produces one canonical addr-spec usable as a tag-address directory name. A missing file, empty token, missing/extra placeholder, or unusable result is a startup configuration failure (§7.2 and §9).

**ASSUMPTION-TEMPLATE-LENGTH:** The operator provisions templates that never generate an address exceeding the applicable email transport or deployed-provider address-length limits. This is a trusted configuration assumption, not an additional runtime address-length check. Startup still validates the specified syntax and literal-component layout; actual filesystem failures retain their normal outcomes. The separate runtime check on edited and synthesized physical EML header lines is defined in §8.1; a permitted generated address can still make a resulting header line too long.

A literal `%` therefore cannot appear in the template. Email local parts may legally contain one, so the format must change if that is ever needed.

### 6.4 Deterministic message parsing

**VERSION-SENSITIVE-005, VERSION-SENSITIVE-006:** The admitted HDR/EML grammar, null reverse-path spelling, and proprietary `notify:` behavior must have current fixtures after relevant SmarterMail/client changes. **VERSION-SENSITIVE-012:** MDN recognition and every enabled sender's client-path approval must be rechecked when those paths change.

Parsing must have one deterministic interpretation and preserve every byte outside the edits defined by this specification. The parser organization, byte ranges, and edit mechanics are implementation concerns described in `implementation.md`.

#### Fast HDR classification scan

Before opening or reading the EML contents, scan the complete, normally small HDR just far enough to classify `auth:` safely:

1. Require exact CRLF line endings, at least the three positional lines described in §6.1, and one final blank line with no bytes after it.
2. Scan every metadata line through that blank line so a later `auth:` occurrence cannot be missed. Each nonblank metadata line must have a nonempty key followed by `:`; an empty, whitespace-containing, control-containing, or otherwise malformed key makes the result `UNSAFE` because auth absence has not been established from a valid HDR envelope. Repeated and unknown metadata values stay ordered and opaque and do not need to be parsed merely to classify auth.
3. Recognize `auth` ASCII case-insensitively as the complete key before the first colon. After optional leading and trailing SP/HTAB, one recognized value must be exactly one bare version 1 addr-spec—no display name, angle brackets, list, or empty path. Multiple recognized `auth:` fields are ambiguous.

The scan returns exactly one of three results: `NO_AUTH`, `VALID_AUTH(canonical-address)`, or `UNSAFE`. Malformed framing, duplicate `auth:` fields, or a malformed/null `auth:` value returns `UNSAFE` because absence or identity cannot be established safely.

After final EML discovery and the preceding `Failed`-status rejection in §7.3.1, the sorter uses this scan result and the auth-index directory test for routing. `NO_AUTH` takes the direct-pass path. A valid auth-address that cannot be represented by the version 1 literal-folder layout is structurally unenrollable and also passes directly without constructing that path. For an enrollable value, a definitively absent auth directory passes directly while an existing directory diverts the untouched pair to `process\`. `UNSAFE`, an unavailable `senders\` root, a non-directory at the candidate path, or an uncertain lookup holds the message. The sorter never opens the EML contents or validates `sender-id.txt`.

For a pair already in `process\`, the tagger's full HDR parse must resolve exactly one current auth-address against the sender configuration effective for that invocation. No auth, ambiguous/malformed auth, an unknown or retired auth, or a configuration inconsistency is a tagger message failure; none may use the sorter's direct-pass rule.

#### Full HDR and EML parsing

The full HDR interpretation retains positional lines and metadata in order, including repeated keys. It understands only address-bearing values needed by the behavior and leaves unknown values opaque. Recipient identity is defined in §7.1.2. Null reverse-path handling is defined below; the spelling emitted by the approved SmarterMail build remains an empirical fixture requirement in `assumptions.md`.

For a configured sender, locate the EML header/body boundary at the first `CRLF CRLF`. Require exact CRLF within the header block. A logical field starts with a non-whitespace line containing a nonempty field name followed by `:`, and includes every following physical line beginning with SP or HTAB. A field name contains only printable ASCII bytes `!`–`9` or `;`–`~` (that is, no colon). Reject an orphan continuation or otherwise ambiguous field boundary. Match field names ASCII case-insensitively, retain their original spelling and bytes, and never parse the body or an unsupported field value.

Version 1 supports this bounded mailbox grammar in supported sender fields:

- a bare addr-spec, or a name-addr containing an addr-spec inside `<` and `>`;
- one or more mailboxes separated by commas where that field permits a list;
- an unquoted display name made of nonempty printable-ASCII tokens separated by folding whitespace, excluding the structural bytes `(`, `)`, `<`, `>`, `,`, `:`, `;`, `\`, and `"`, or a quoted display name with backslash-escaped characters;
- a syntactically delimited RFC 2047 encoded-word treated as an opaque display-name token, not decoded; and
- SP, HTAB, and CRLF folding whitespace only where whitespace is syntactically allowed, never inside an addr-spec.

An addr-spec uses an unquoted ASCII dot-atom local-part, one `@`, and an ASCII DNS-style domain. Local-part atoms contain only letters, digits, or ``!#$%&'*+-/=?^_`{|}~``; dots may separate nonempty atoms. Domain labels contain only letters, digits, and interior hyphens, with nonempty labels separated by dots. Leading, trailing, or consecutive dots and leading/trailing label hyphens are invalid. Comments, address groups, quoted local-parts, domain literals, obsolete route syntax, and any other mailbox form are unsupported in version 1. Parentheses inside a quoted display name or opaque encoded-word are content, not comments.

The null reverse-path is an address-path token, not an addr-spec or mailbox. In HDR line 2, accept exactly one bare addr-spec, the exact two bytes `<>`, or a zero-byte line; surrounding whitespace is not permitted. In every HDR `from:` value, remove optional leading and trailing SP/HTAB only for parsing, then accept exactly one bare addr-spec, exact `<>`, or an empty value. Both null spellings produce the same internal `NullPath` value while all original bytes remain available for byte-preserving output. Do not require HDR line 2 and any `from:` occurrence to agree. A null path has no canonical-address: it never matches a current or retired private-address, never activates tagging, and is never replaced. Preserve it byte-for-byte in every child. `auth:` must contain exactly one non-null bare addr-spec after its permitted outer SP/HTAB; null or empty auth is a classification failure. HDR recipient line 3 and all EML mailbox-valued fields reject a null path. EML `Return-Path:` alone permits either one angle addr-spec or exact `<>`; every valid occurrence is later removed as a complete logical field. A null `Return-Path:` contains no addr-spec and therefore cannot activate tagging.

For a current configured auth-address, every occurrence of every supported sender field must conform to its field grammar before the program decides whether the message contains a current or retired private-address. Unsupported or malformed syntax therefore fails closed even when no visible current-private match has yet been found; otherwise unusual syntax could conceal the private-address and incorrectly enter pass-through. EML `Sender:` and `Resent-Sender:` use one mailbox per occurrence; the other supported mailbox-bearing EML fields are parsed as comma-separated mailbox lists for trigger classification. Parse every present `From:` and `Reply-To:` occurrence to determine the trigger. Once tagging activates, require exactly one `From:` field containing exactly one mailbox and at most one `Reply-To:` field (§7.3.2). These count requirements apply to every activated message, not only group `Reply-To:` synthesis. Individually valid occurrences do not fail a no-match pass-through solely because of their field or mailbox counts. Repeated supported fields other than `From:` and `Reply-To:` are parsed and rewritten occurrence by occurrence.

Each parsed mailbox retains its original addr-spec bytes and canonical-address (§7.1.1). Remove a `Return-Path:` as one complete logical field, including its continuation lines. When group handling synthesizes `Reply-To:`, insert the copied-and-edited logical field immediately after its qualifying `From:` field. Except for complete field removal/insertion and exact addr-spec replacements, every original header byte remains in its original order and form.

When a profile's `allow-mdn` value is `false`, classify whether the top-level EML is a recognized MDN before private-address trigger classification. Permit zero or one `Content-Type:` field; more than one is ambiguous and fails closed. If present, parse its logical field body without modifying the source, treating SP/HTAB and legal CRLF folding plus following SP/HTAB as whitespace. After optional leading whitespace, require one nonempty `type` token, optional whitespace, `/`, optional whitespace, one nonempty `subtype` token, then zero or more parameters. Each parameter is optional whitespace, `;`, optional whitespace, one nonempty attribute token, optional whitespace, `=`, optional whitespace, and one value; only optional whitespace may follow the last value.

A token contains printable ASCII other than SP/HTAB and the MIME separators ``()<>@,;:\"/[]?=``. A value is either a nonempty token or a quoted string. Inside a quoted string, permit SP/HTAB, legal folding, and printable ASCII other than an unescaped `"` or `\`; require `\` to escape exactly one following printable ASCII, SP, or HTAB byte. Comments, controls, raw non-ASCII, empty tokens/values, malformed escapes/folds, and trailing junk are unsupported and fail closed. Type, subtype, and attribute names compare ASCII case-insensitively; parameter names must be unique. Decode quoted-pair escapes only when comparing a parameter value and otherwise preserve the complete field bytes.

A message is a recognized MDN when either (a) its media type is `multipart/report` with a `report-type` value equal to `disposition-notification`, or (b) its top-level media type is `message/disposition-notification`. Parameter order, casing, legal folding, and unrelated unique parameters do not change classification. Receipt-request headers on an ordinary message do not make it an MDN, and `multipart/report; report-type=delivery-status` is a DSN rather than an MDN. Do not inspect MIME boundaries or body parts. When `allow-mdn` is `true`, Content-Type classification is unnecessary and the message follows the ordinary sender-field rules.

---

## 7. Processing behavior

If `-log` is enabled, trace consequential evaluations, branches, and persistent state transitions under §5.1 so an operator can reconstruct the path taken.

### 7.1 Identities and mappings

#### 7.1.1 Canonicalize an addr-spec

Every successfully parsed addr-spec has two representations:

- **original-address** — the exact addr-spec bytes from the message, retained when that address must be written back to a child envelope; and
- **canonical-address** — the same complete addr-spec with every ASCII `A`–`Z` byte converted to `a`–`z`.

The version 1 grammar is ASCII-only, so every byte other than `A`–`Z` is unchanged. Culture-sensitive casing is not part of address identity.

Use canonical-address for every address equality comparison, current/retired profile lookup, mapping key, duplicate check, recipient sort, and `notify:` association. Case differences therefore never create distinct identities. This intentionally treats the RFC-local-part case distinction as an unsupported real-world edge case: the risk of assigning several permanent tags to one recipient because a sender varied capitalization is judged materially greater than the risk of two actual recipients differing only by local-part case. If such two recipients do exist and both case variants appear in one message, deduplication collapses them and only the first spelling receives a child; this delivery and attribution consequence is explicitly accepted.

Canonicalization never changes arbitrary message bytes. Sender rewriting still replaces only matching addr-spec spans, and a child envelope uses the selected recipient's original-address. A generated tag-address is canonicalized before it is used in a message, map, or lowercase directory name.

#### 7.1.2 Parse recipients and create a recipient-id

A recipient-id holds one or more recipient addresses in a form where the same list of recipients always generates the same recipient-id.

Parse HDR line 3 as a comma-separated list of bare version 1 ASCII addr-specs. Permit SP and HTAB immediately before or after a complete element, but do not include that whitespace in either address representation. Reject an empty line/list, an empty element, a leading/trailing comma, display-name or angle syntax, controls, raw non-ASCII/Unicode, or any addr-spec outside §6.4. An internationalized domain is supported only when SmarterMail already supplies its ASCII `xn--` A-label form.

For each parsed element, retain its original-address and compute its canonical-address under §7.1.1. Deduplicate by canonical-address and retain the first occurrence's original-address for delivery. Sort the unique recipient records by ordinal byte comparison of canonical-address; this is canonical child order. A later message with different address casing therefore reuses the same individual/group tag mappings while retaining that later message's first spelling in its child envelope.

To create recipient-id, take the sorted canonical-address list. In each address, escape `%` as `%%` and `;` as `%;`, in that order. Append an unescaped `;` delimiter after each encoded address and concatenate the results. The recipient-id is nonempty and always ends in `;`.

The representation is canonical and reversible: on decode, `%%` means `%`, `%;` means `;`, and an unescaped `;` ends one address. Any other `%` escape, a missing final delimiter, an empty element, an invalid decoded canonical addr-spec, a duplicate, or a non-increasing address is invalid. The current addr-spec grammar excludes `;`, but its reserved escape remains part of the persistent format so the format is stable if that grammar later expands.

#### 7.1.3 The sender-id and recipient-id to tag-address mapping

One permanent mapping associates each `(sender-id, recipient-id)` pair with exactly one tag-address. The tagger loads those mappings before processing and makes each newly allocated mapping available to later messages in the same invocation. We expect fewer than 100,000 mappings in total and normally 100–5,000 for one sender. The five-character entropy decision is scoped to that per-sender range; materially exceeding it requires design review.

#### 7.1.4 Obtain a tag-address

For a `(sender-id, recipient-id)` pair:

1. If a mapping exists, return its tag-address. Lookup and allocation do not add a tag-log entry; only publication does.
2. Otherwise create one complete, inert staging identity record containing the sender-id and recipient-id. Attempt to create its empty tag log, but a logging failure only prints to standard error and does not prevent publication of the complete identity record. Incomplete identity files are never a live mapping.
3. Generate a five-character token from a cryptographically secure random source using the alphabet `0123456789abcdefghjkmnpqrstvwxyz`. The five characters are independent and uniformly distributed, so all `32^5` tokens are equiprobable and provide the stated 25 bits. There is no weaker fallback.
4. Substitute that token for the template's `%` placeholder. Template validation at startup guarantees that any token from this alphabet produces one canonical, usable tag-address; the generated value is trusted thereafter.
5. Publish the complete staging record atomically under `tag-addresses\<tag-address>\`, without replacing or adopting an existing mapping. This directory transition is the mapping's single live-publication point.
6. If that tag-address already exists, generate another token and try another final address, up to sixteen token proposals. Another publication failure fails the message and leaves the staging record inert.
7. Once the mapping is live, make it available to subsequent messages in the running tagger. If the process cannot do so, it must stop; restart will load the complete published record and preserve mapping uniqueness.

An in-scope crash before mapping publication leaves only inert staging. A crash afterward leaves a complete live record that startup will load. This is process-crash visibility under §9.6, not a power-loss guarantee. UUID naming, randomness APIs, directory creation, and in-memory structures are defined in `implementation.md`.

### 7.2 Program startup

Each executable owns and validates only its own resource boundary.

**Sorter startup:** Before scanning `proc\` or touching a message, acquire only the sorter singleton under §11. The sorter does not open `log.txt`, preload auth indexes, read `sender-id.txt`, inspect profiles or mappings, or wait for the tagger. It consults `senders\` only for a message with one valid enrollable auth. If that lookup is unavailable, no-auth and structurally unenrollable messages can still pass while affected auth messages are held. Version 1 performs no volume, free-space, or destination-existence preflight.

**Tagger startup:** Before loading configuration, scanning `process\`, or processing a message, acquire only the tagger singleton. If `-log` is enabled, attempt to open the trace before the remaining startup work; failure prints to standard error and startup continues without file tracing. Then establish and validate the complete sender configuration that will govern this invocation:

- every usable sender profile and its current/retired auth and private identities, validated template, and MDN policy;
- a complete, unambiguous correspondence between each published auth index and its profile.

Separately validate every complete live `(sender-id, recipient-id)` mapping, with no duplicate key or unresolved sender. A mapping published during this invocation must be reused by every later message rather than allocated again. The in-memory representations that provide these behaviors belong in `implementation.md`.

The `senders\` and `profiles\` roots must exist and be accessible and enumerable; otherwise the tagger cannot prove that its configuration view is complete and startup fails. Missing required files, malformed identity data, or a conflict that could change profile selection or mapping uniqueness likewise fails startup before any message is processed. Extra unrelated files are ignored. Inert entries under either staging directory and suffixed message leftovers are named in a startup warning but are neither parsed as live data nor repaired, retried, or deleted. An absent `tag-addresses\` directory means the mapping set is empty.

Sender configuration takes effect only at tagger startup and remains unchanged for the life of the process. Concurrent administrative editing is unsupported. Stop the tagger, complete and publish the configuration update, and restart it before the client uses the new identity. The sorter independently observes a newly published auth folder on a later lookup. Live configuration reload is deferred to `future.md`.

### 7.3 Trigger

#### 7.3.1 Sorter classification and handoff

For each `proc\<basename>.eml`, either selected by the basename on the sorter command line or found while watching `proc\`:

1. Before reading or probing the matching HDR, establish that the final `.eml` exists as a file, without opening or reading its contents. A missing EML is a stale selection in watch mode; one-shot mode defers with `EML_NOT_AVAILABLE`. Neither inspects an HDR-only attempt. A directory in its place or another failure to determine its attributes becomes a hold decision with `INPUT_READINESS_FAILED`.
2. Read the matching same-basename plain HDR in place. If it vanished before the read, it is stale and skipped in watch mode; in one-shot mode the requested message fails. A Windows sharing/lock violation defers the candidate untouched with `HDR_BUSY`. Any other HDR read failure becomes a hold decision so the sorter can attempt to make the ambiguous trigger inert.
3. Read the first CRLF-terminated status line, removing trailing ASCII SP/HTAB for this comparison only. Exact, case-sensitive `Failed` becomes a hold decision with `UPSTREAM_FAILED`: it must never pass or divert. Otherwise run the fast HDR scan in §6.4. There is no `Written` requirement or status whitelist; other status values remain opaque. Malformed HDR framing with a final EML follows the normal `UNSAFE_HDR` hold path rather than a status-based deferral.
4. Choose one routing decision before claiming the pair:
   - `NO_AUTH` → direct pass to `spool\`.
   - `UNSAFE` → hold.
   - `VALID_AUTH` that cannot be represented by the literal auth-folder layout → direct pass as structurally unenrollable, without constructing that path.
   - Any other `VALID_AUTH` → divert when its exact auth directory exists, pass when that child is definitively absent beneath an accessible `senders\` root, and hold when the lookup is unavailable or uncertain.
5. Claim the message by making the plain HDR inert as `.hdr.sort`, without replacing an existing artifact. If the plain HDR disappeared first, skip it without a diagnostic in watch mode; in one-shot mode the requested message fails. Any other ownership failure is fatal to that sorter invocation because it could not make the selected live trigger inert. Once claimed, the routing decision is fixed.
6. For a hold decision, leave the EML plain and the HDR as `.hdr.sort`, attempt `proc\<basename>.sort.err`, report the error to standard error, and continue with later EML candidates. Do not move either message file after that ownership rename.
7. For direct pass, move the plain EML to `spool\<basename>.eml`, then publish `.hdr.sort` there as the plain HDR. For diversion, move the EML to `process\<basename>.eml`, then publish the HDR there. The HDR transition is the destination's readiness event and no destination is overwritten.

A deferral creates neither `.hdr.sort` nor `.sort.err`, writes no terminal email-log record, and leaves all input bytes and names untouched. Watch mode continues with other EML candidates and reconsiders them on a later notification or the ordinary rescan within 30 seconds while idle. One-shot mode does not wait: it prints `NOT READY` to standard error and returns 1. There is no per-message sleep, retry loop, or readiness timeout. A `Failed` HDR is a real terminal rejection: claim it as `.hdr.sort`, leave its EML in place, attempt `.sort.err`, report stderr and `ERROR`, and do not delete either message file.

**VERSION-SENSITIVE-001:** Discovery relies on SmarterMail publishing its final EML complete with corresponding final HDR metadata available. The EML existence check does not prove those producer guarantees; establish them on the deployed build. The tagger's input contract remains the sorter's controlled EML-first/HDR-last publication. A shared basename associates the two files; no additional numeric-only basename restriction is introduced.

The sorter does not parse any other HDR address, inspect `sender-id.txt`, validate whether an auth is current or retired, or verify tagger readiness. Folder existence is deliberately only a conservative routing decision. Because every retired auth index remains published, retired mail is diverted and then rejected by the tagger. A newly published auth directory affects the first lookup that observes it.

If the EML disappears after the readiness check, a destination transition fails, or the diagnostic cannot be written, retain the exact state created by completed operations, report the available information, and continue with later EML candidates. Never roll a moved EML back. Existing `.hdr.sort` or `.sort.err` artifacts are not completed by this readiness gate. A remaining plain EML whose only HDR is `.hdr.sort` may be discovered, but its matching plain HDR is absent, so watch mode skips it as stale without replay. If a new matching plain pair appears alongside that residual, the conflicting ownership destination remains a fatal collision requiring manual disposition.

#### 7.3.2 Tagger trigger and activation

For each `process\<basename>.hdr`, either specified on the tagger command line or found while watching `process\`:

1. Claim the pair by renaming the HDR to `.hdr.start`, then the EML to `.eml.start`, both without replacement. If the plain HDR disappeared before the first rename, skip it as stale in watch mode; in one-shot mode the requested message fails. Any other first-rename failure is fatal to that tagger invocation because no inert ownership state exists; an EML rename failure after the HDR claim is message-local. After both claims succeed, make `.in` copies of the two `.start` files if `-keep` is enabled. (`-keep` copies are not written to `log.txt`.)
2. Parse the complete HDR framing and the metadata/sender values needed for trigger classification once, but leave recipient line 3 uninterpreted at this stage. Require exactly one auth-address in the tagger's current configuration and select that profile. Missing, ambiguous, malformed, unknown, or retired auth is a message failure. A pair already routed into `process\` never falls back to the sorter's unconfigured-auth pass rule.
3. Parse every occurrence of the EML sender fields before tag lookup or generation. Each present occurrence of a supported sender-identity field listed in §8 must individually conform to the version 1 grammar; syntactic ambiguity within an occurrence is a failure even if no current private-address match has yet been found. Record the `From:` field and mailbox counts and the `Reply-To:` field count. The activated-message count requirements below apply only after a current-private match; individually valid occurrences do not by themselves prevent a no-match pass-through. When `allow-mdn` is `false`, also perform the top-level MDN classification in §6.4.
4. If that classification recognizes an MDN, fail closed before private-address pass-through classification, recipient parsing, or tag lookup. When `allow-mdn` is `true`, skip this classification and follow the ordinary sender-field rules; the flag does not force tagging.
5. Compare canonical-addresses under §7.1.1. If any parsed field matches the profile's canonical retired-private-address set, fail closed. If none matches the current private-address, pass the unchanged `.start` pair to `spool\`: when `-keep` is enabled, first copy the pair to `.out`, then move the EML and then the HDR without replacement. Do not parse recipients or create or look up a tag. If at least one current-private match exists, tagging is activated.

6. For every activated message, require exactly one EML `From:` field containing exactly one mailbox, and at most one `Reply-To:` field. A missing or repeated `From:`, or a `From:` with the wrong mailbox count, is a runtime contract error and follows the error-retention procedure below. Repeated `Reply-To:` fields are likewise a message-local contract failure but retain their current `.start` state. Enforce these requirements before fan-out, recipient parsing, or any tag lookup or allocation. They apply regardless of which supported sender field activated tagging and whether the message needs group-tag handling. The one `From:` mailbox need not equal the private-address; preserve a non-matching address under the ordinary rewrite rule.

**`From:` contract-error retention:** Starting with the owned `.start` pair, rename `<basename>.hdr.start` to `<basename>.hdr.err`, then `<basename>.eml.start` to `<basename>.eml.err`, without replacement. These are retention transitions only; do not allocate a tag, construct a child, or publish any mail. If either rename fails, stop the retention moves and keep the exact state left by completed operations; do not overwrite a colliding artifact or roll back. Attempt `<basename>.err` containing the contract reason, observed field/mailbox counts, and actual retained paths, attempt an error event in `log.txt` when requested and available, and print the error to standard error regardless of `-log`. Include any retention or diagnostic failure in the available error report. Logging failures do not change this contract-error outcome. Watch mode continues with later fresh messages; one-shot mode returns nonzero. The error pair is never resumed automatically.

### 7.4 Fan-out

1. Rename `<basename>.hdr.start` → `.hdr.break`, then `<basename>.eml.start` → `.eml.break`.

2. Parse line 3 of `process\<basename>.HDR.break` into the canonical ordered and deduplicated recipient records and complete-list recipient-id defined by §7.1.2. Recipient parsing begins only after tagging has activated; tagger pass-through messages do not need it.

3. If there is more than one unique canonical recipient record, this is a multi-recipient message:
    a. Use the already parsed optional `Reply-To:` field and the single-mailbox `From:` established by §7.3.2.
    b. If one valid `Reply-To:` exists and contains no addr-spec canonically equal to the private-address, preserve it byte-for-byte. Do not create or look up a group tag.
    c. Otherwise, group-tag handling is required. Generate or look up the stable group-tag-address using the complete-list recipient-id from step 2 (§7.1.4).
    d. If `Reply-To:` was absent, synthesize exactly one by starting with the validated single-mailbox `From:` field, changing the field name to `Reply-To:`, and replacing its mailbox addr-spec with the group-tag-address while retaining its display-name formatting.
    e. If one valid `Reply-To:` existed and contained the private-address, replace every matching addr-spec with the group-tag-address. Preserve every non-matching mailbox and every byte outside the replaced addr-specs.


4. For each recipient *n*, numbered by position in the sorted and deduplicated canonical recipient list, construct a new child without overwriting any existing artifact:
    1. Create `process\<basename>-<n>.eml.process` from the parent EML with these changes:
        a. If multi-recipient group-tag handling was required, apply the prepared `Reply-To:` addition or replacement before ordinary child sender rewriting. If the original non-private `Reply-To:` suppressed group-tag creation, leave it byte-for-byte.
        b. Encode the current record's single canonical-address as a one-address recipient-id under §7.1.2, then generate or look up its child tag-address (§7.1.4).
        c. In the EML headers, replace every parsed addr-spec in a supported sender-identity field whose canonical-address matches the private-address canonical identity with the child tag-address. Preserve non-matching sender identities and every byte outside the replaced addr-spec. Do not apply sender rewriting to recipient fields.
        d. Create `process\<basename>-<n>.hdr.process` with line 3 reduced to **exactly one** recipient—the retained first original-address for the recipient currently being processed. For `notify:` filtering, skip optional leading SP/HTAB in the value, parse one bare addr-spec, and require the next byte to be the `=` delimiter; treat everything after that delimiter as opaque. Keep every well-formed `notify:` line whose address has canonical-address equality with the child recipient, preserving each complete kept line byte-for-byte and in original order. Drop all nonmatching or unparseable `notify:` lines. Zero or multiple matches are valid and never fail the message. In HDR line 2 and every HDR `from:` field, replace an addr-spec if and only if its canonical-address equals the private-address canonical identity. Preserve every null path and non-matching sender addr-spec byte-for-byte. Keep `auth:` unchanged. Keep every other line byte-for-byte, including the trailing blank line.
        e. Only after both child files are completely written and the resulting edited/synthesized EML header lines pass §8.1, make the EML `.pend` and then the HDR `.pend`. A child is ready only when both `.pend` files exist; the HDR transition is the last readiness marker.


### 7.5 Publish

Do not begin publication until every intended child is a complete `.pend` pair that has passed §8.1.

1. If `-keep` is specified, create **all** child `.out` pairs before publishing any child. A copy failure stops the operation while every child remains unpublished.

2. For each child `.pend` pair in the defined child order:
    a. Build the tag-log entry for output basename `<id>` under §5.1 and attempt each distinct referenced log in ordinal tag-address order. Report failures to standard error and continue. These are the last mapping-log attempts before publication; best-effort `log.txt` result and next-intent events and standard-error diagnostics may intervene.
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

For a configured `auth:` profile, tagging is activated when at least one supported sender-identity field contains a parsed addr-spec whose canonical-address equals the profile's canonical private-address (§7.1.1). Every activated message must satisfy §7.3.2: exactly one EML `From:` field containing exactly one mailbox, and at most one `Reply-To:` field. Replace **every** such complete addr-spec match with the child tag-address. Legitimate non-matching sender identities are preserved.

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

Check the resulting lengths after the actual individual/group tag addresses and all sender edits for the child are known, before either child file becomes `.pend`. A synthesized `Reply-To:` must be checked with its new field name; replacing `From:` with `Reply-To:` adds four bytes even when the address length stays equal. Count each physical line separately, not the total unfolded field length. Removed fields have no resulting line to check. Unchanged physical lines, unsupported opaque fields, bodies, HDR files, and no-match pass-through are outside this check. Do not refold, truncate, shorten, or otherwise change preserved bytes to make an output fit.

An overlong result is a **message-local runtime contract error**. Stop further child construction and publish no child for that parent. Retain the immutable `.break` parent and any constructed children in their exact inert states (`.process`, `.pend`, or no child files yet), following the normal pre-publication fan-out failure rules. Any mapping already published remains permanent and reusable. Attempt the parent `<basename>.err` diagnostic, an error event in the requested trace when available, and standard-error output regardless of `-log`. Include the child, field, physical-line position, measured byte length, 998-byte limit, and retained paths in the diagnostic. Failures to write diagnostics do not release rejected mail. Watch mode continues with later fresh messages; one-shot mode returns nonzero. This uses the fan-out retention states, not the early `From:`-count `.start`-to-`.err` rename procedure in §7.3.2.

---

## 9. Failure model

The governing safety rule is simple:

- The sorter passes a message only when it can establish that auth is absent, structurally unenrollable, or definitively not enrolled. Ambiguity is held rather than passed.
- Once a message has been diverted to `process\`, the tagger fails closed unless it can prove that unchanged pass-through or the complete specified transformation is safe.
- No incomplete output becomes live: a destination's plain HDR appears only after its EML and all preceding preparation are complete.

### 9.1 Failure scope

A **startup/fatal failure** stops only the affected executable, releases its singleton, reports to standard error, and returns nonzero. These failures include:

- inability to acquire that program's singleton;
- tagger configuration or live-mapping ambiguity;
- inability to maintain reliable queue discovery;
- inability to make a selected live HDR inert after it was chosen for ownership;
- inability to make an already-published live mapping available to later messages in the same invocation.

A **message-local failure** stops normal processing for the owned message, performs any specifically required contract-error retention transition (§7.3.2), preserves the resulting files, attempts the applicable `.sort.err` or parent `.err`, and lets watch mode continue with later fresh messages. These include malformed or unsupported enrolled mail, a missing EML after ownership, pre-publication identity-record allocation or random-source failure, child construction failure, an overlong edited or synthesized EML header line (§8.1), and handoff/publication/cleanup failure after ownership. Report runtime contract errors to standard error and attempt an execution-trace error event when `-log` is active. The post-publication mapping-availability failure listed above is fatal because continuing without reusing the live mapping could mint a second tag for the same identity. Log and diagnostic-file failures themselves are not mail-processing failures.

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

At startup, each watcher reports the leftovers in its own input queue and then continues with fresh work. Retained `.in` and `.out` files are expected debugging evidence rather than failed work.

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

The operator-provided datadir and SmarterMail spooldir—their locations, permissions, topology, filesystem, and same-volume placement—are trusted installation inputs. The program does not audit ACLs, ownership, reparse points, volume identity, or filesystem type and does not attempt to repair them. An administrator or service identity able to alter those trees while a program runs is outside the threat model.

This trust is intentional: version 1 is a small local mail utility, not a defense against its own administrator. Actual access, read, write, or move failures still follow §9.

Configuration changes are supported only while the tagger is stopped. Program logs and persistent records have one program writer. Once the applicable singleton is acquired, races with another copy of that program are outside the version 1 operating model.

### 10.2 Data boundaries

HDR and EML content remains syntactically untrusted. Ambiguous auth or supported sender syntax must not cause a private-address to pass through after enrollment. Configuration and persisted mappings are validated when the tagger establishes its startup view because accidental corruption could select a wrong sender or create a second mapping.

Values generated by the program or deliberately provisioned by the trusted administrator remain trusted after their boundary validation.

Any value used as one literal child name must be one usable Windows path component so it cannot escape its trusted root. This is a simple containment and layout requirement, not a general hostile-filesystem subsystem. An otherwise valid auth-address that cannot be represented by the literal-folder layout is structurally unenrollable and takes the sorter's direct-pass branch without constructing that path. Actual path-length or filesystem limitations are discovered through the required operation.

### 10.3 Entropy and diagnostics

Tag tokens use the cryptographically secure, uniform five-character generation defined in §7.1.4. Their 25-bit size is an accepted attribution limitation, not an authentication mechanism. Private-addresses are also created from a cryptographically secure source during onboarding and kept non-public, but only authenticated enrollment—not possession of that string alone—selects a sender profile.

The sorter email log, console debugging, tagger `log.txt`, parent diagnostics, `-keep` artifacts, and standard error may contain any identity or message metadata useful for diagnosis. They are intentionally unredacted and belong inside the same protected operational boundary as the datadir and spool. Message-derived controls must be escaped so they cannot forge additional diagnostic lines.
## 11. Concurrency and atomicity

Version 1 has two independent serialization boundaries:

- one sorter owns one SmarterMail `proc\` through `proc\sm-sorter.lock`; and
- one tagger owns one datadir, its `process\` queue, and its permanent mapping set through `<datadir>\sm-tagger.lock`.

Each executable must acquire its own singleton before configuration, logging, scanning, or message work and retain it until no later message can be claimed. Contention is a startup failure: report it, return nonzero, and touch no message in that executable's input queue. The persistent lock file may remain after exit; its existence alone does not mean the resource is owned.

The sorter and tagger do not acquire or wait for one another's singleton and are expected to run concurrently. A sorter one-shot conflicts with its sorter watcher, and a tagger one-shot conflicts with its tagger watcher.

Within each singleton:

- process messages sequentially;
- do not launch per-message workers or process two basenames concurrently;
- let only the tagger allocate mappings, make newly published mappings available to later messages, and append tag logs;
- require profile/auth-index administration to occur while the tagger is stopped; and
- retain ownership through completion of the current message during orderly shutdown.

These stipulations eliminate same-program races from the version 1 design; behavior under a second same-role process that somehow bypasses singleton enforcement is not supported.

The atomic visibility requirements for our transitions are the observable state rules in §§4, 7, and 9: ownership removes the plain HDR first, handoff/publication exposes the plain HDR last, a live mapping appears only as one complete directory, and existing destinations are never replaced. SmarterMail's incoming candidates additionally require the readiness gate in §7.3.1. The supported same-volume NTFS installation makes individual moves atomic. Locking and move choices are specified in `implementation.md`.

## 12. Future scope

Deferred features are tracked in `future.md`. They do not weaken the version 1 behavior above and require their own design decision before implementation.
