# Storage reference

This is the single current reference for directory trees, filenames, file contents, and their readers and writers. Its runtime definitions are incorporated by [spec.md](spec.md) as part of the version 1 contract. The specification defines processing behavior and failure outcomes; [implementation.md](implementation.md) defines the platform operations. Change storage definitions here rather than duplicating them in other guides. Numbered section references below refer to `spec.md` unless a local link is supplied.

The current storage contract is rc.10: mail working state is under the selected spool's Proc subtree, while sender configuration and permanent mappings retain the rc.9 data layout. There is no on-disk schema/version marker, automatic migration, or fallback to earlier locations or meanings. Existing installations must follow the [offline queue migration](queue-layout-2026-09-17.md), including earlier configuration conversions when applicable. Use [bring-up.md](bring-up.md) for staged setup and [deployment.md](deployment.md) for installing and running the executables. Dated experiment reports describe versions tested at that time; they are evidence, not alternate current storage definitions.

- [Runtime directory trees](#runtime-directory-trees)
- [Required directories and creation](#required-directories-and-creation)
- [Readers, writers, and ownership](#readers-writers-and-ownership)
- [Identifiers and addresses](#identifiers-and-addresses)
- [Sender configuration](#sender-configuration)
- [Persistent tag mappings](#persistent-tag-mappings)
- [Message files](#message-files)
- [Input grammar](#input-grammar)
- [Message state suffixes](#message-state-suffixes)
- [Logs and diagnostics](#logs-and-diagnostics)
- [Locks](#locks)
- [Release and repository trees](#release-and-repository-trees)
- [Lab configuration and evidence](#lab-configuration-and-evidence)

## Runtime directory trees

`<spooldir>` is the configured SmarterMail spool root, not its `proc` child. `<mailroot>` is a documentation shorthand for the fixed `<spooldir>\proc\sm-tagger` directory, not another CLI argument. `<datadir>` is the separate configuration/mapping root. Relative command-line roots and sorter log paths resolve against the process working directory; prefer absolute paths. These trees describe possible contents, not a list to create in full before first use.

All mail transitions stay on the spool's ordinary local NTFS volume: Proc, the fixed mailroot, its `process`/`failed` children, and final spool output. Do not redirect these paths through junctions or other reparse points. The datadir may be on a different ordinary local NTFS volume; its mapping `staging` and `tag-addresses` directories must share that data volume for atomic mapping publication. Auth-index staging likewise stays with its destination root. Placement, permissions, and topology are trusted installation conditions, without runtime ACL, volume, or reparse-point auditing. Keep binaries, generated build output, and optional logs separate from mail/configuration paths.

**VERSION-SENSITIVE-001 — accepted deployment assumption:** the owner confirms SmarterMail does not read mail within the selected Proc tree, so the nested mailroot is inert to SM. The sorter scans only direct Proc entries and never recursively enters this subtree. This is the accepted isolation basis for rc.10, not a newly performed live test. Revisit the assumption after relevant SmarterMail queue behavior changes.

```text
<spooldir>\
    <basename>.eml                  complete released output
    <basename>.hdr                  published last; SmarterMail trigger
    proc\
        sm-sorter.lock
        <basename>.eml              sorter discovery candidate
        <basename>.hdr              SmarterMail envelope/status
        <basename>.hdr.sort         sorter-owned, inert header
        <basename>.sort.err         best-effort failure diagnostic
        sm-tagger\                 <mailroot>; inert to SmarterMail
            sm-tagger.lock         tagger queue singleton
            process\
                <message files listed under Message state suffixes>
            failed\                created only for confirmed UPSTREAM_FAILED
                <basename>.eml
                <basename>.hdr.sort
                <basename>.sort.err (best effort)
    <other SmarterMail-managed directories, such as delay>
```

SmarterMail owns its other internal trees; neither executable scans or edits them. Their exact topology and contents are outside our file contract and may change with SmarterMail versions. Tagged child basenames add an ordinal as defined under [message state suffixes](#message-state-suffixes).

### Application data tree

```
<datadir>\
    sm-tagger.lock
    log.txt                         (only created/opened with -log)

    senders\
        auth-addresses\
            .staging\
                <staging-id>.authtmp\
                    sender-id.txt
            <canonical-auth-address>\
                sender-id.txt
        sender-ids\
            <sender-id>\
                private-address.txt
                from-template.txt
                allow-mdn.txt
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

- **`senders\auth-addresses\<canonical-auth-address>\`** — an active enrollment index. Folder existence diverts that canonical auth-address; its closed `sender-id.txt` points to an existing stable sender-id record. Multiple distinct auth directories may point to the same record, sharing its private-address, template, MDN flag, and tags. There is no primary auth address, reverse list, or retired enrollment state. Removing an index makes future sorter lookups treat the auth as unenrolled.
- **`senders\sender-ids\<sender-id>\`** — one complete tagger sender-id record per stable sending identity (§1.2). The folder name is a canonical lowercase UUID in `8-4-4-4-12` form, generated once and never changed, reused, or derived from an email address.
- **`senders\auth-addresses\.staging\<staging-id>.authtmp\`** — an unpublished auth-index entry. Populate and close its `sender-id.txt`, then rename the directory without replacement to the canonical auth-address path. The sorter never consults `.staging`; tagger startup reports but does not load leftovers.
- **`<mailroot>\process\`** — the sorter-to-tagger queue and all tagger per-message working state. A plain HDR is a complete handoff; suffixed states are inert. This directory is not under the datadir.
- **`<mailroot>\failed\`** — manual retention in the inert Proc subtree, used only for confirmed `UPSTREAM_FAILED`. It is not required at startup or for pass-through and has no dependency on `senders\sender-ids\`. The sorter creates it lazily after claiming the failed HDR. Neither program watches, scans for residuals in, replays, or cleans this directory.
- **`staging\<staging-id>.tagtmp\`** — an inert, unpublished tag record being created. The staging-id is a fresh canonical lowercase UUID used only to avoid name collisions. Nothing under `staging\` is ever a live mapping.
- **`<tag-address>\`** — one folder for each tag-address we have ever generated.

Write auth-address index, sender-id, and tag-address folder names in canonical lowercase. Auth-address folder names additionally obey the [literal Windows name rules](#literal-windows-names); an otherwise valid auth-address that cannot be a literal Windows component is not enrollable in version 1.

The sorter uses only `senders\auth-addresses\` for enrollment; an empty accessible root permits ordinary non-enrolled pass-through and requires no `senders\sender-ids\` root. Tagger startup requires both nested roots, even for empty configuration. A sender-id record may have zero auth indexes so permanent historical mappings remain loadable without enrolling an account. The executables neither fall back to earlier paths nor detect or migrate an earlier configuration. Unlike the path-only rc.8 change, rc.9 changes enrollment semantics; see [spec.md §5.3](spec.md#53-backup-restore-layout-changes-and-retention) before using an existing datadir.

## Required directories and creation

| Path | When needed; who creates it |
|---|---|
| `<spooldir>` and `<spooldir>\proc` | Provision through SmarterMail before running either executable. Sorter startup does not create them; it opens its lock in `proc`. |
| `<datadir>` | Administrator provisions before bring-up. Tagger startup needs it for the lock and optional trace before configuration loading. |
| `<datadir>\senders\auth-addresses` | Administrator creates an accessible, enumerable root. Empty means sorter-only pass-through. Sorter consults it only for valid enrollable auth; tagger always requires it at startup. |
| `<datadir>\senders\sender-ids` | Administrator creates for tagger startup, even when empty. Not needed by sorter-only pass-through. |
| `<mailroot>` | Tagger creates after acquiring the data lock, before acquiring its queue lock. Sorter creates it as needed through creation of `process` or `failed`; ordinary pass-through does not need it. |
| `<mailroot>\process` | Sorter creates on diversion; tagger creates after successful configuration/mapping validation. No need to pre-create for pass-through. |
| `<mailroot>\failed` | Sorter creates only after claiming a confirmed `Failed` HDR. Optional for startup and ordinary pass-through. |
| `<datadir>\senders\auth-addresses\.staging` | Administrator creates when publishing an auth index; neither executable completes these administrative entries. |
| `<datadir>\staging` and `<datadir>\tag-addresses` | Tagger creates on first mapping allocation. Absent mapping root means an empty set; present roots must be accessible directories. |
| Parent of sorter `-l <logfile>` | Administrator creates. A missing/inaccessible parent only disables this best-effort log. |

A missing auth-address root is **not** the empty-root configuration. For valid enrollable auth, missing or uncertain lookup holds mail. No-auth and structurally unenrollable messages do not need that lookup. Empty roots are an intentional configuration, not a migration of existing enrollment.

## Readers, writers, and ownership

“Read” means interpreting contents unless stated otherwise. Administrators may inspect protected records/evidence, but must stop the relevant owners before manual changes or disposition.

| Tree or file | Created/written by | Read/used by | Ownership and lifetime |
|---|---|---|---|
| `proc` plain HDR/EML | SmarterMail | Sorter reads HDR; checks EML existence without reading contents | Sorter claims by HDR rename, then relocates unchanged bytes. HDR-only attempts stay untouched. |
| Spool root plain output pair | Sorter for pass-through; tagger for final output | SmarterMail | EML first, plain HDR last; our programs never reclaim released output. |
| `senders\auth-addresses\<auth>\sender-id.txt` | Administrator, via complete-directory publication | Tagger reads/validates pointer; sorter checks only directory existence | Every published index is active; removal deliberately ends future sorter enrollment. |
| `senders\auth-addresses\.staging` | Administrator | Tagger reports leftovers without loading; sorter ignores them | Unpublished administrative work; no automatic adoption/cleanup. |
| `senders\sender-ids\<sender-id>\*` | Administrator | Tagger reads all three required files at startup | Fixed invocation snapshot; no sorter access. |
| `<mailroot>\process` plain pairs and suffixed mail | Sorter publishes input; tagger claims, writes children/copies, renames, publishes, and deletes success intermediates | Tagger | Only plain HDR triggers processing. Failed inert states remain for reconciliation. |
| `<mailroot>\failed` retained EML / HDR.sort | Sorter relocates confirmed upstream failures | Administrator | Neither program watches, scans, replays, or cleans this directory. |
| `staging\<uuid>.tagtmp` identity files | Tagger | Tagger during allocation; startup only reports leftovers | Never live until complete-directory publication. |
| `tag-addresses\<tag>\sender-id.txt`, `recipient-id.txt` | Tagger publishes together from staging | Tagger reads at startup, then uses its in-memory map | Permanent immutable identity; no sorter access. |
| `tag-addresses\<tag>\tag-log.txt` | Tagger creates/appends best effort, independently of `-log` | Administrator; neither program parses old contents | Not required for mapping validity, not delivery proof. |
| Chosen sorter email log | Sorter with `-l` | Administrator | Append-only; separate from tagger trace. |
| `<datadir>\log.txt` | Tagger with `-log` | Administrator | Append-only execution trace. |
| `<basename>.sort.err` / `<basename>.err` | Sorter / tagger respectively | Administrator | Best-effort human diagnostics, never recovery inputs. |
| Lock files | Matching executable opens/creates and holds exclusive handle | Matching executable uses handle, not contents | Persistent filename does not imply a running owner. |

Neither executable writes administrative sender configuration. Program-created `sender-id.txt` files belong to mapping records, not the auth index. The sorter never reads EML contents, sender pointers/records, or tag mappings.

Tagger startup reports **all** leftover entries in both staging roots, not just names matching the illustrated suffix convention. They are not loaded or replayed. Neither a `.staging` entry nor a `.tagtmp` record is a valid published index/mapping.

## Identifiers and addresses

- **Sender-id:** one canonical lowercase UUID in `8-4-4-4-12` form, generated once for a sender and never renamed or reused. It is a local reference key in configuration, mappings, and diagnostics; the programs do not insert it into email.
- **Staging-id:** a fresh canonical lowercase UUID used to avoid staging-directory collisions; it does not identify a sender.
- **Auth-address:** the authenticated account from HDR metadata. Its active index selects one sender-id record; several accounts may share that record.
- **Private-address:** the configured trigger identity. It is not the auth-index key and has no directory of its own.
- **Tag-address:** the generated mail address and literal permanent mapping directory name. One `(sender-id, recipient-id)` key has one permanent tag.
- **Basename:** the shared HDR/EML filename stem, excluding extension/state suffix. It must be a usable literal Windows component; there is no numeric-only restriction. Tagged children add their canonical ordinal.

The tag token is five independent uniformly distributed characters from `0123456789abcdefghjkmnpqrstvwxyz`, providing 25 bits. Generation and collision handling are specified in [spec.md §7.1.4](spec.md#714-obtain-a-tag-address).

### Literal Windows names

Auth and tag directory names are lowercase canonical addresses. Any literal child name must be nonempty, neither `.` nor `..`, contain no control character or any of `< > : " / \ | ? *`, and end in neither a space nor a dot. Reserved Windows device stems (case-insensitive, before the first dot, with trailing stem spaces removed) are rejected: `CON`, `PRN`, `AUX`, `NUL`, `CONIN$`, `CONOUT$`, and `COM`/`LPT` followed by one of `1`–`9`, `¹`, `²`, or `³`. Actual path-length and filesystem failures are handled by the attempted operation.

The mail grammar admits some addresses that cannot be a literal folder name. Such an auth is structurally unenrollable: the sorter passes it without constructing the candidate path. Administrative auth indexes and generated tag addresses must pass both checks.

### Address identity

Every successfully parsed addr-spec has two representations:

- **original-address** — the exact addr-spec bytes from the message, retained when that address must be written back to a child envelope; and
- **canonical-address** — the same complete addr-spec with every ASCII `A`–`Z` byte converted to `a`–`z`.

The version 1 addr-spec grammar is ASCII-only, so every address byte other than `A`–`Z` is unchanged. Culture-sensitive casing is not part of address identity.

Use canonical-address for every address equality comparison, auth-index lookup, private-address match, mapping key, duplicate check, recipient sort, and `notify:` association. Case differences therefore never create distinct identities. This intentionally treats the RFC-local-part case distinction as an unsupported real-world edge case: the risk of assigning several permanent tags to one recipient because a sender varied capitalization is judged materially greater than the risk of two actual recipients differing only by local-part case. If such two recipients do exist and both case variants appear in one message, deduplication collapses them and only the first spelling receives a child; this delivery and attribution consequence is explicitly accepted.

Canonicalization never changes arbitrary message bytes. Sender rewriting still replaces only matching addr-spec spans, and a child envelope uses the selected recipient's original-address. A generated tag-address is canonicalized before it is used in a message, map, or lowercase directory name.

### Recipient identity

A recipient-id holds one or more recipient addresses in a form where the same list of recipients always generates the same recipient-id.

Parse HDR line 3 as a comma-separated list of bare version 1 ASCII addr-specs. Permit SP and HTAB immediately before or after a complete element, but do not include that whitespace in either address representation. Reject an empty line/list, an empty element, a leading/trailing comma, display-name or angle syntax, controls, raw non-ASCII/Unicode, or any addr-spec outside [input grammar](#input-grammar). An internationalized domain is supported only when SmarterMail already supplies its ASCII `xn--` A-label form.

For each parsed element, retain its original-address and compute its canonical-address under [address identity](#address-identity). Deduplicate by canonical-address and retain the first occurrence's original-address for delivery. Sort the unique recipient records by ordinal byte comparison of canonical-address; this is canonical child order. A later message with different address casing therefore reuses the same individual/group tag mappings while retaining that later message's first spelling in its child envelope.

To create recipient-id, take the sorted canonical-address list. In each address, escape `%` as `%%` and `;` as `%;`, in that order. Append an unescaped `;` delimiter after each encoded address and concatenate the results. The recipient-id is nonempty and always ends in `;`.

The representation is canonical and reversible: on decode, `%%` means `%`, `%;` means `;`, and an unescaped `;` ends one address. Any other `%` escape, a missing final delimiter, an empty element, an invalid decoded canonical addr-spec, a duplicate, or a non-increasing address is invalid. The current addr-spec grammar excludes `;`, but its reserved escape remains part of the persistent format so the format is stable if that grammar later expands.

## Sender configuration

The administrator writes the auth-index pointers and sender records; only the tagger reads their contents. Each `senders\sender-ids\<sender-id>` directory requires exactly these configuration roles: `private-address.txt`, `from-template.txt`, and `allow-mdn.txt`. Extra unrelated files are ignored. The former `auth-address.txt`, `retired-auth-addresses.txt`, and `retired-private-addresses.txt` files are not read or validated and confer no enrollment or rejection behavior. Remove them from the working configuration during the explicit offline conversion, preserving their originals in the protected backup.

Identity and policy files use strict UTF-8 without a BOM. Single-value files permit only terminal CR/LF removal: leading whitespace, surrounding spaces, comments, and extra content are invalid. The private-address value may contain ASCII uppercase and is canonicalized on load; directory names and sender UUIDs must already be lowercase canonical values. Template annotations have the exception described below.

**`sender-id.txt`** — In an auth-index directory, points that canonical auth-address to its stable sender-id record. In a tag-address directory, identifies the sender-id record that owns the permanent mapping. In both roles, trim permitted terminal newline bytes, require one canonical lowercase sender-id and no other content, and require that it identifies an existing `senders\sender-ids\<sender-id>\` directory. Write and close an auth-index pointer before publishing its directory. Changes to existing indexes require the stopped-tagger configuration-update procedure below; permanent tag-mapping identity files remain immutable.

**`private-address.txt`** — In a sender-id record, holds exactly one current private-address. Trim newline characters when reading.

Published auth-directory names and configured private addresses are validated under [address identity](#address-identity) at startup. Each canonical auth belongs to one index; private addresses must be unique across sender records, and no auth address may equal any configured private address, including in its own record. Case-only variants are duplicates. Several different auth addresses pointing to the same sender-id are permitted. Missing, unreadable, malformed, duplicate, or conflicting live values are startup configuration failures. Former private addresses are neither loaded nor blocked; after replacement they are ordinary non-matching identities.

**`allow-mdn.txt`** — Required per sender-id record. After trimming terminal CR/LF bytes only, its complete contents must be exact lowercase ASCII `false` or `true`; spaces, comments, other casing, additional lines, and other values are invalid. `false` is the initial and recommended value. `true` permits recognized outbound MDN responses for this sender-id and may be deployed only after the evidence/approval gate in §1.4 covers every account and path sharing it. Adding an auth index to an enabled record requires extending that evidence before use. This is configuration, not a claim that the program rewrites MDN bodies.

### `from-template.txt`

An **address pattern only** — no display name, no angle brackets. A single `%` marks where the token goes:

```
joe-%@example.com
```

The domain portion is the sender-id record's dedicated catch-all tag domain (§1.3). The template must not generate addresses in a domain or local-part namespace used for ordinary mailboxes or aliases.

Parsing is deliberately trivial: **skip leading whitespace, then read to the first whitespace character or EOF.** Anything after that — on the same line or on following lines — is ignored, so notes can simply be written underneath with no comment syntax to define.

It must contain exactly one `%`, which is replaced by the five-character tag token. At startup, validate that substitution with the permitted token alphabet always produces one canonical addr-spec usable as a tag-address directory name. A missing file, empty token, missing/extra placeholder, or unusable result is a startup configuration failure (§7.2 and §9).

**ASSUMPTION-TEMPLATE-LENGTH:** The operator provisions templates that never generate an address exceeding the applicable email transport or deployed-provider address-length limits. This is a trusted configuration assumption, not an additional runtime address-length check. Startup still validates the specified syntax and literal-component layout; actual filesystem failures retain their normal outcomes. The separate runtime check on edited and synthesized physical EML header lines is defined in §8.1; a permitted generated address can still make a resulting header line too long.

A literal `%` therefore cannot appear in the template. Email local parts may legally contain one, so the format must change if that is ever needed.

Template annotation bytes after the first token are ignored; invalid UTF-8 in those annotations is tolerated. The token must still produce the strict ASCII address and literal directory name defined above.

## Persistent tag mappings

A live `tag-addresses\<tag-address>` directory has two required identity files: `sender-id.txt` (the same UUID format as an auth-index pointer) and `recipient-id.txt`. The tagger writes both as UTF-8 without a BOM or terminal newline, closes them, and publishes their complete containing directory from `staging\<uuid>.tagtmp` without replacement. A separate `tag-log.txt` is optional diagnostic data.

The final directory name is the canonical generated tag address. Existing mappings remain valid when a template or current sender address changes: lookup uses permanent sender-id and recipient-id, not the new template. Neither program rewrites or deletes published identity files.

**`recipient-id.txt`** — Holds the canonical recipient-id defined in [recipient identity](#recipient-identity). The writer emits its exact bytes without a newline. On load, trim terminal CR/LF bytes, decode and validate every escape and address, require strictly increasing canonical address order, and require re-encoding to reproduce the trimmed bytes exactly. Empty, malformed, alternatively encoded, duplicate, or unsorted identity data is invalid.

### Cross-file consistency and publication

The sender-id is the permanent referential key. Never rename or reuse a `senders\sender-ids\<sender-id>\` folder. Every auth-index and tag-address `sender-id.txt` must identify an existing sender-id record; otherwise tagger startup fails. A sender-id record may not be deleted while an auth index or tag record refers to it.

Every published auth-index directory must have one valid `sender-id.txt` pointing to an existing complete sender-id record. The directory name is the authenticated address; no sender-record file repeats or approves it. Every index is active. Multiple distinct indexes may reference one record, and a record with zero indexes is valid, including when existing mappings refer to it. Missing pointers, missing target records, malformed or conflicting addresses, and duplicate identities fail tagger startup. The sorter deliberately does not perform this global validation; folder existence alone is reason to divert, so a tagger configuration failure cannot stop unrelated sorting.

A tag mapping becomes live only when a staging directory containing complete, closed `sender-id.txt` and `recipient-id.txt` identity files is atomically renamed directly under `tag-addresses\`. Those identity files define completeness; `tag-log.txt` is best effort and may be absent or incomplete. Never publish a final tag directory before its identity files are complete. Entries left under `staging\` are inert evidence of interrupted work: report them prominently at startup, never load, publish, retry, or delete them automatically, and leave them for manual disposition. Their presence alone does not prevent startup.

Every live tag directory must identify one usable tag-address and contain readable, valid `sender-id.txt` and `recipient-id.txt` identity files. Its sender-id must resolve to an existing sender-id record. Duplicate `(sender-id, recipient-id)` keys, incomplete identity records, or malformed identity data fail startup because they could create two tags for one mapping or select the wrong sender-id record. Do not require, open, or validate `tag-log.txt` during startup. A missing, unreadable, unwritable, or corrupted tag log cannot invalidate a mapping or fail startup; an actual later logging-operation failure follows [logs and diagnostics](#logs-and-diagnostics).

The tagger fixes its sender-id record/auth-index configuration at startup and performs no sender-id record reads during message processing. For a supported update, pause affected submissions, stop the tagger, complete the sender-id record and any new auth index, publish each new index as one complete directory, and restart the tagger before clients use the identity. The sorter may remain running for unrelated mail and diverts once a final auth directory exists. Removing an index deliberately makes later sorter lookups pass that auth as unenrolled; it does not disable the account upstream or remove its sender-id record/mappings. Reconcile affected queued mail before removal: if an item in `process\` has an auth absent from the startup snapshot, it fails closed. The tagger never applies the sorter's unconfigured-auth pass rule. A private-address replacement similarly has no retired-address protection; former values become ordinary non-matches.

## Message files

HDR is SmarterMail's proprietary envelope/status/metadata; EML is the Internet message. Their same basename associates the pair. SMTP envelope addresses are distinct from EML `From:`/`To:` display headers. The sorter reads only HDR contents; the tagger interprets supported HDR/EML fields for enrolled messages.

The examples use reserved domains and illustrate positions and bytes, not an exhaustive vendor schema. Unknown metadata and unsupported EML field values remain opaque. See [input grammar](#input-grammar) for the exact preserved leading UTF-8 BOM exception.

### The `.HDR` envelope

Sanitized illustrative sample — a two-recipient outbound message. Note the trailing space on line 1, and that the file ends with a **blank line** (`\r\n\r\n`):

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
| 1 | SmarterMail status; after final EML discovery, compare the first CRLF-terminated line case-sensitively after removing trailing ASCII SP/HTAB only (§7.3.1). `Written` permits classification, `Failed` is retained, and every other or incomplete status defers. Preserve the original bytes. |
| 2 | envelope MAIL FROM |
| 3 | envelope RCPT TO — **all recipients, comma-delimited, on this one line** |
| 4 … | `key: value` pairs to EOF, then a trailing blank line |

> **⚠ Keys can repeat.** Every occurrence and its original order must survive unless a later rule explicitly removes it. In particular, one `notify:` may exist per recipient.

Fields that matter:

- **Status** — a plain HDR may initially say `Writing`, and its envelope and metadata can still change. The sorter keys discovery off final EML, then requires a readable matching HDR whose first line compares exactly as `Written` before classification. `Writing` defers quietly in watch mode; unexpected/incomplete statuses defer with stderr on every encounter, and `Failed` is retained. No status bytes are changed. The tagger receives complete pairs through the sorter's own handoff protocol. The current SmarterMail producer guarantees remain an empirical deployment gate (`VERSION-SENSITIVE-001` in `assumptions.md`).
- **`from:`** — envelope sender metadata. It can differ from positional line 2, including a nonempty value beside an empty line 2. Each location is interpreted independently; equality is not assumed. For a tagged message, replace each parsed address that equals the selected private-address and preserve a non-matching address.
- **`notify:`** — observed as one line per recipient, but its exact proprietary semantics remain under empirical test. Each child keeps every well-formed line whose embedded address canonically matches that child and drops every nonmatching or unparseable `notify:` line. **Match by address, never position** (§7.4). Zero or multiple matching lines are permitted and do not fail the message.
- **`auth:`** — the authenticated account. Read it to select the sender-id record (§1.2), but **never modify it**. It does not go on the wire and the delivery path needs it.

**What `notify:` may be:** the RFC 3461 DSN `NOTIFY` parameter recorded per envelope recipient, or proprietary per-recipient delivery state. The name, address/value shape, and observed association support the DSN hypothesis. Tested SMTP DSN requests produced opaque values such as `failure,delay` and `success,failure,delay`; this observation is not a complete vendor schema. Read receipts/MDNs and SMTP delivery-status notifications are separate mechanisms, so the integration matrix in `testing-plan.md` exercises both and records exactly what changes. Version 1 does not depend on the hypothesis: matching `notify:` lines are retained, nonmatching or unparseable `notify:` lines are dropped, and their opaque values are never interpreted. Other unknown metadata is preserved.

#### `proc\` carries inbound mail too

Confirmed from real spool captures: messages arriving *at* this server pass through `proc\` alongside outbound submissions, and they carry `From:` headers like anything else.

They cannot be diverted for tagging because they do not carry an `auth:` identity. After a successful fast HDR scan proves that `auth:` is absent, the sorter sends the untouched pair directly to `spool\` without opening or reading the EML contents. This remains true even if the EML `From:` or envelope sender happens to equal a private-address, or an envelope recipient is a known tag-address. SmarterMail's per-user dedicated-domain catch-all (§1.3), not either executable, handles version 1 inbound delivery.

### The `.EML` message

Standard RFC 5322: header block, one blank line, then the body. Header field names begin in column 0; a folded continuation line begins with whitespace. Sanitized illustrative sample:

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

### EML field inventory

The tagger parses `From:`, `Reply-To:`, `Sender:`, `Resent-From:`, `Resent-Sender:`, `Return-Path:`, `Disposition-Notification-To:`, `Return-Receipt-To:`, and `X-Confirm-Reading-To:` as the supported sender-identity surface. It also parses top-level `Content-Type:` for MDN classification when that sender's MDN policy is false. The exact admitted values are in [input grammar](#input-grammar); rewrite/removal/insertion rules are in [spec.md §8](spec.md#8-sender-rewriting).

Recipient display fields (`To:`, `Cc:`, `Bcc:`, and their `Resent-` variants), `Message-ID:`, trace/signature/authentication fields, other unsupported values, and the complete body remain outside sender rewriting. EML display recipients do not supply the routing recipient-id: HDR positional line 3 does. `Return-Path:` in EML is distinct from both HDR line 2 and HDR `from:` metadata.

## Input grammar

**VERSION-SENSITIVE-005, VERSION-SENSITIVE-006:** The admitted HDR/EML grammar, null reverse-path spelling, and proprietary `notify:` behavior must have current fixtures after relevant SmarterMail/client changes. **VERSION-SENSITIVE-012:** MDN recognition and every enabled sender's client-path approval must be rechecked when those paths change.

Parsing must have one deterministic interpretation and preserve every byte outside the edits defined by the behavioral specification. The parser organization, byte ranges, and edit mechanics are implementation concerns described in `implementation.md`.

### Fast HDR classification scan

Before opening or reading the EML contents, scan the complete, normally small HDR just far enough to classify `auth:` safely:

1. Require exact CRLF line endings, at least the three positional lines described in [HDR format](#the-hdr-envelope), and one final blank line with no bytes after it.
2. Scan every metadata line through that blank line so a later `auth:` occurrence cannot be missed. Each nonblank metadata line must have a nonempty key followed by `:`; an empty, whitespace-containing, control-containing, or otherwise malformed key makes the result `UNSAFE` because auth absence has not been established from a valid HDR envelope. Repeated and unknown metadata values stay ordered and opaque and do not need to be parsed merely to classify auth.
3. Recognize `auth` ASCII case-insensitively as the complete key before the first colon. After optional leading and trailing SP/HTAB, one recognized value must be exactly one bare version 1 addr-spec—no display name, angle brackets, list, or empty path. Multiple recognized `auth:` fields are ambiguous.

The scan returns exactly one of three results: `NO_AUTH`, `VALID_AUTH(canonical-address)`, or `UNSAFE`. Malformed framing, duplicate `auth:` fields, or a malformed/null `auth:` value returns `UNSAFE` because absence or identity cannot be established safely.

After final EML discovery and the positive `Written`-status check in §7.3.1, the sorter uses this scan result and the auth-index directory test for routing. `NO_AUTH` takes the direct-pass path. A valid auth-address that cannot be represented by the version 1 literal-folder layout is structurally unenrollable and also passes directly without constructing that path. For an enrollable value, a definitively absent auth directory passes directly while an existing directory diverts the untouched pair to `process\`. `UNSAFE`, an unavailable `senders\auth-addresses\` root, a non-directory at the candidate path, or an uncertain lookup holds the message. The sorter never opens the EML contents or validates `sender-id.txt`.

For a pair already in `process\`, the tagger's full HDR parse must resolve exactly one auth-address through the active auth-index snapshot for that invocation. No auth, ambiguous/malformed auth, an unknown auth, or a configuration inconsistency is a tagger message failure; none may use the sorter's direct-pass rule.

### Full HDR and EML parsing

The full HDR interpretation retains positional lines and metadata in order, including repeated keys. It understands only address-bearing values needed by the behavior and leaves unknown values opaque. Recipient identity is defined in [recipient identity](#recipient-identity). Null reverse-path handling is defined below; the spelling emitted by the approved SmarterMail build remains an empirical fixture requirement in `assumptions.md`.

For a configured sender, locate the EML header/body boundary at the first `CRLF CRLF`. Require exact CRLF within the header block. A logical field starts with a non-whitespace line containing a nonempty field name followed by `:`, and includes every following physical line beginning with SP or HTAB. A field name contains only printable ASCII bytes `!`–`9` or `;`–`~` (that is, no colon). Reject an orphan continuation or otherwise ambiguous field boundary. Match field names ASCII case-insensitively, retain their original spelling and bytes, and never parse the body or an unsupported field value.

**EML preamble compatibility exception — VERSION-SENSITIVE-005.** Permit one exact UTF-8 BOM, the three bytes `EF BB BF`, only at absolute EML byte offset zero. Treat it as a preserved preamble outside every field name and value; begin field parsing immediately after those three bytes. Preserve it exactly once at byte zero in unchanged and tagged output. It is not part of a leading `Return-Path:` field and must survive that field's removal; it is not copied when synthesizing `Reply-To:` from `From:`. Original field, value, and addr-spec byte positions remain relative to the complete original EML, including the preamble.

This exception addresses the observed SmarterMail build 9742 immediate API output; it does not introduce UTF-8 decoding, whitespace trimming, general prefix removal, or a broader header grammar. A repeated or partial leading BOM, a UTF-16/UTF-32 prefix, or a BOM prefixed to a later field name does not receive this exception and fails the existing framing/field-name rules. HDR parsing is unchanged. BOM bytes occurring in a body or otherwise opaque value remain subject to the existing preservation rules, without scanning or normalization. Recheck both producer input and SmarterMail's handling of the preserved output prefix after relevant upgrades; see [the compatibility follow-up](bom-compatibility-2026-09-16.md).

Version 1 supports this bounded mailbox grammar in supported sender fields:

- a bare addr-spec, or a name-addr containing an addr-spec inside `<` and `>`;
- one or more mailboxes separated by commas where that field permits a list;
- an unquoted display name made of nonempty printable-ASCII tokens separated by folding whitespace, excluding the structural bytes `(`, `)`, `<`, `>`, `,`, `:`, `;`, `\`, and `"`, or a quoted display name with backslash-escaped characters;
- a syntactically delimited RFC 2047 encoded-word treated as an opaque display-name token, not decoded; and
- SP, HTAB, and CRLF folding whitespace only where whitespace is syntactically allowed, never inside an addr-spec.

An addr-spec uses an unquoted ASCII dot-atom local-part, one `@`, and an ASCII DNS-style domain. Local-part atoms contain only letters, digits, or ``!#$%&'*+-/=?^_`{|}~``; dots may separate nonempty atoms. Domain labels contain only letters, digits, and interior hyphens, with nonempty labels separated by dots. Leading, trailing, or consecutive dots and leading/trailing label hyphens are invalid. Comments, address groups, quoted local-parts, domain literals, obsolete route syntax, and any other mailbox form are unsupported in version 1. Parentheses inside a quoted display name or opaque encoded-word are content, not comments.

The null reverse-path is an address-path token, not an addr-spec or mailbox. In HDR line 2, accept exactly one bare addr-spec, the exact two bytes `<>`, or a zero-byte line; surrounding whitespace is not permitted. In every HDR `from:` value, remove optional leading and trailing SP/HTAB only for parsing, then accept exactly one bare addr-spec, exact `<>`, or an empty value. Both null spellings produce the same internal `NullPath` value while all original bytes remain available for byte-preserving output. Do not require HDR line 2 and any `from:` occurrence to agree. A null path has no canonical-address: it never matches a configured private-address, never activates tagging, and is never replaced. Preserve it byte-for-byte in every child. `auth:` must contain exactly one non-null bare addr-spec after its permitted outer SP/HTAB; null or empty auth is a classification failure. HDR recipient line 3 and all EML mailbox-valued fields reject a null path. EML `Return-Path:` alone permits either one angle addr-spec or exact `<>`; every valid occurrence is later removed as a complete logical field. A null `Return-Path:` contains no addr-spec and therefore cannot activate tagging.

For an indexed auth-address, every occurrence of every supported sender field must conform to its field grammar before the program decides whether the message contains a configured private-address. Unsupported or malformed syntax therefore fails closed even when no visible current-private match has yet been found; otherwise unusual syntax could conceal the private-address and incorrectly enter pass-through. EML `Sender:` and `Resent-Sender:` use one mailbox per occurrence; the other supported mailbox-bearing EML fields are parsed as comma-separated mailbox lists for trigger classification. Parse every present `From:` and `Reply-To:` occurrence to determine the trigger. Once tagging activates, require exactly one `From:` field containing exactly one mailbox and at most one `Reply-To:` field (§7.3.2). These count requirements apply to every activated message, not only group `Reply-To:` synthesis. Individually valid occurrences do not fail a no-match pass-through solely because of their field or mailbox counts. Repeated supported fields other than `From:` and `Reply-To:` are parsed and rewritten occurrence by occurrence.

Each parsed mailbox retains its original addr-spec bytes and canonical-address ([address identity](#address-identity)). Remove a `Return-Path:` as one complete logical field, including its continuation lines. When group handling synthesizes `Reply-To:`, insert the copied-and-edited logical field immediately after its qualifying `From:` field. Except for complete field removal/insertion and exact addr-spec replacements, every original header byte remains in its original order and form.

When a sender-id record's `allow-mdn` value is `false`, classify whether the top-level EML is a recognized MDN before private-address trigger classification. Permit zero or one `Content-Type:` field; more than one is ambiguous and fails closed. If present, parse its logical field body without modifying the source, treating SP/HTAB and legal CRLF folding plus following SP/HTAB as whitespace. After optional leading whitespace, require one nonempty `type` token, optional whitespace, `/`, optional whitespace, one nonempty `subtype` token, then zero or more parameters. Each parameter is optional whitespace, `;`, optional whitespace, one nonempty attribute token, optional whitespace, `=`, optional whitespace, and one value; only optional whitespace may follow the last value.

A token contains printable ASCII other than SP/HTAB and the MIME separators ``()<>@,;:\"/[]?=``. A value is either a nonempty token or a quoted string. Inside a quoted string, permit SP/HTAB, legal folding, and printable ASCII other than an unescaped `"` or `\`; require `\` to escape exactly one following printable ASCII, SP, or HTAB byte. Comments, controls, raw non-ASCII, empty tokens/values, malformed escapes/folds, and trailing junk are unsupported and fail closed. Type, subtype, and attribute names compare ASCII case-insensitively; parameter names must be unique. Decode quoted-pair escapes only when comparing a parameter value and otherwise preserve the complete field bytes.

A message is a recognized MDN when either (a) its media type is `multipart/report` with a `report-type` value equal to `disposition-notification`, or (b) its top-level media type is `message/disposition-notification`. Parameter order, casing, legal folding, and unrelated unique parameters do not change classification. Receipt-request headers on an ordinary message do not make it an MDN, and `multipart/report; report-type=delivery-status` is a DSN rather than an MDN. Do not inspect MIME boundaries or body parts. When `allow-mdn` is `true`, Content-Type classification is unnecessary and the message follows the ordinary sender-field rules.

## Message state suffixes

SmarterMail deposits pairs in `<spooldir>\proc\`. The sorter sends a safe pass-through pair directly to `<spooldir>`, or sends a potentially enrolled pair to `<mailroot>\process\`. A confirmed upstream `Failed` pair is retained in `<mailroot>\failed\`, within the inert Proc subtree, under §7.3.1. The tagger sends its unchanged or transformed outputs from `process\` to `<spooldir>`. Both input directories are flat and all per-message state is represented by filename suffixes.

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
| `x.eml` + `x.hdr` | final EML discovery candidate; require `Written` before routing, retain `Failed`, and defer other statuses |
| `x.hdr.sort` + `x.eml` | sorter-owned pair before either destination move |
| `x.hdr.sort` with destination `x.eml` | first handoff/publication or upstream-failure retention move succeeded; HDR move is incomplete/failed |
| `x.sort.err` | best-effort sorter diagnostic; the HDR/EML locations remain authoritative |

Completed upstream-failure retention leaves `x.eml` and `x.hdr.sort` in `<mailroot>\failed\`, with a best-effort `x.sort.err` beside that HDR. The HDR remains suffixed and no consumer watches this directory. A failed second move can leave the EML there and the owned HDR and diagnostic in `proc\`; completed moves are never undone.

The tables show paired milestones, not every possible state between individual moves. Tagger states in `<mailroot>\process\`:

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

Every suffix is appended after the file's original `.hdr` or `.eml` extension, except the standalone parent diagnostic names. Bytes keep the same HDR/EML interpretation in every retained state; a suffix is a lifecycle marker, not a different encoding. Intermediate pair moves may leave different suffixes on the two files. Inspect both actual paths rather than assuming both moves succeeded.

With `-keep`, `.in` copies belong to the accepted parent; `.out` copies belong to an unchanged parent or to each transformed child. All child `.out` copies complete before any child is published. These copies are never processing inputs, are not included in startup residual warnings, and their copy operations deliberately do not create transition trace events.

Pass-through output keeps the SmarterMail-assigned input basename. Each tagged child keeps traceable continuity by using `<input-basename>-<n>`, where *n* is its canonical child ordinal (§7.4). Version 1 deliberately does not replace this with an unrelated random output id because the common basename is useful when correlating files with SmarterMail logs.

The sorter checks readiness and classifies the HDR while it is still plain, then makes the HDR inert as `.hdr.sort`. Pass/divert decisions send the pair to `spool\` or `process\`, with EML first and plain HDR last. An `UPSTREAM_FAILED` hold instead moves EML first and the still-suffixed HDR last to `failed\`. Other holds stay in `proc\`. A crash or error between either pair of moves leaves an EML at the destination and the inert HDR in `proc\`; the sorter never rolls the EML back.

The tagger watches only plain HDRs in `process\`, so it cannot observe a sorter handoff before its EML is present. The tagger constructs its `.start`/`.break`/`.process`/`.pend` states inside `process\`. Individual moves can leave mixed suffixes: for example `.hdr.start` beside a plain EML, `.hdr.break` beside `.eml.start`, or `.eml.pend` beside `.hdr.process`. During publication the EML may already be in the spool while its `.hdr.start` or `.hdr.pend` remains in `process\`; no rollback occurs. `spool\` receives only complete outputs whose HDR arrives last.

Every individual state transition must be atomic in the supported deployment and must not replace an existing destination. Proc, mailroot `process`/`failed`, and final spool output therefore stay on the same ordinary local NTFS volume without junctions. Mapping staging and `tag-addresses` stay together on the datadir's NTFS volume, which may differ from the mail volume; no message move crosses to the datadir. Correct placement, permissions, Proc-subtree isolation, and SmarterMail basename uniqueness are installation conditions; version 1 adds no volume, ACL, or location preflight. The platform operations used to implement these transitions are defined in `implementation.md`.

**VERSION-SENSITIVE-004:** Recheck move atomicity and sharing/singleton behavior after Windows Server, .NET, security-software, or storage-topology changes.

## Logs and diagnostics

**Best-effort logs and accepted corruption.** Logging is diagnostic and must not control mail flow. A failure to create, open, encode, write, flush, or close a log prints to standard error and processing continues with the current message and later work. It never causes a message hold, process termination, failed mapping allocation, or nonzero exit by itself. The formats below define new entry bytes, not an integrity requirement on existing contents. Corrupted logs—including malformed UTF-8, incomplete lines, and entries joined by a later append to an unfinished line—are acceptable in version 1. This loss of diagnostic quality is preferable to the complexity of validation, repair, recovery separators, or tracking unusable mappings. Append at the existing end without reading or validating prior contents. Do not repair corruption or block work because a log is absent or unusable. Logs remain diagnostic evidence, never mapping or message-state authority.

**Sorter email log (`-l <logfile>`)** — an optional append-only record of terminal sorter outcomes. After acquiring the sorter singleton, attempt to open the selected file for the invocation. Do not create its parent directory. For each terminal message result, attempt one UTF-8 line without a BOM, terminated by CRLF, with this field order:

```text
<UTC timestamp> basename="..." result=PASS|DIVERT|ERROR auth="..." reason="..." operation="..." hdr="..." eml="..." destination="..." error="..."
```

The timestamp format is `yyyy-MM-ddTHH:mm:ss.fffZ`. Quote and escape string values using the rules below; use `"-"` for unavailable values. `auth` is the canonical authenticated address when known. `hdr` and `eml` name the last-known/current message paths; `destination` names the attempted destination when applicable. These fields describe known state and attempted operations, not a fresh existence check. `error` preserves the applicable error detail when available.

`PASS` is recorded only after unchanged publication into the spool completes; its reason is `NO_AUTH`, `UNENROLLED_AUTH`, or `UNENROLLABLE_AUTH`. `DIVERT` is recorded only after handoff into `process` completes, with reason `ENROLLED_AUTH`. `ERROR` records the failed terminal outcome, with reason `UPSTREAM_FAILED`, `UNSAFE_HDR`, `AUTH_LOOKUP_FAILED`, `HDR_READ_FAILED`, `INPUT_READINESS_FAILED`, `HDR_CLAIM_FAILED`, `HANDOFF_FAILED`, or `MISSING_HDR` as applicable. A fatal failure to claim a selected HDR and an explicitly requested missing one-shot HDR receive an `ERROR` attempt. A stale watcher entry receives no email-log record. A candidate deferred because its EML is unavailable, its HDR is busy, or its status is `Writing` or unexpected is not a terminal message result and receives no email-log record in either mode. No startup, shutdown, scan, or other process-lifecycle lines are written to this file.

A record is attempted after the message's terminal result is known; a crash or logging failure can leave it absent or partial even after mail has moved. `PASS` and `DIVERT` prove neither final delivery nor downstream tagger success. On formatting, open, append, flush, or close failure, report the log failure to standard error, disable only that file for the rest of the invocation, and preserve the current mail outcome, later processing, and exit status. Do not reopen, retry, rotate, truncate, repair, or recursively log the failure. A later invocation may try opening the file again. Operators may archive or clear it while the sorter is stopped.

**Sorter console debugging (`-v`)** — best-effort standard-output lines prefixed `DEBUG`. Lifecycle, queue scans, readiness deferrals, routing decisions, file-operation intents/results, and stale entries identify their `event`; a terminal message summary uses the email record's result fields. A readiness deferral uses `event=DEFER` with reason and input paths. This output is independent of `-l` and includes no EML contents or tagger-only configuration/mapping inspection. Without `-v`, no debug output is attempted. A console formatting/write failure disables debug output for the invocation and attempts a standard-error report, without affecting mail or the email log. Ordinary errors still go to standard error regardless of both options.

**Tagger `log.txt`** — the optional append-only operational execution trace. It exists as a program output only when tagger `-log` is present. Encode each new event as UTF-8 without a BOM and terminate it with CRLF. Every new event begins with the fixed timestamp/run/seq/context/event/result prefix below and may continue with any number of human-readable `name=value` details:

```
2026-09-01T14:30:12.123Z run=0d3b3c3d-6ca6-4c11-84a0-b119576fa3c8 seq=42 context="message-42" event=STATE_RESULT result=ERROR operation="move" source="D:\\SmarterMail\\Spool\\proc\\sm-tagger\\process\\message-42-1.eml.pend" destination="D:\\SmarterMail\\Spool\\message-42-1.eml" error="destination exists" residual="EML=.eml.pend; HDR=.hdr.pend; later children=.pend; parent=.break"
```

Use UTC with exactly millisecond precision for the timestamp. `run` is one fresh canonical lowercase UUID for the invocation. `seq` begins at 1 and increases for each attempted event. `context` is the input message basename or `-` for a process-wide event. `event` and `result` are concise uppercase ASCII tokens. Additional details follow that prefix; an optional `step` may identify applicable behavior, although current call sites do not emit it. Keep field names stable enough for ordinary text search, but favor understandable diagnostics over a rigid closed vocabulary or names tied to a particular framework API.

Quote every arbitrary string value. Within it, escape `\` as `\\`, `"` as `\"`, CR as `\r`, LF as `\n`, HTAB as `\t`, and any other control or byte not representable as valid UTF-8 as `\xHH`. Integers and fixed uppercase tokens may be unquoted. These rules prevent newly encoded values from introducing additional physical lines while retaining the complete relevant value; they do not establish the integrity of existing log contents.

The sorter email log and console debugging, tagger `log.txt`, parent diagnostics, retained `-keep` artifacts, and standard-error diagnostics are protected operational data. The programs perform **no redaction**. Any configured private-address, auth-address, sender-id, recipient-id, tag-address, basename, full path, parsed metadata/header value, error text, or other datum relevant to the event may and should appear. “Relevant” means read, derived, compared, selected, rejected, mutated, or returned by the program; it does not require dumping an EML body or other bytes the program never examines. These outputs are not sanitized or safe for release outside the trusted operator/security zone.

With `-log`, record enough information for a human to reconstruct the path taken: startup/options, configuration loading, queue scans, message ownership, auth/sender-id record selection, important parsed and canonical values, mapping lookup/allocation, branch decisions, child order, publication, shutdown, and failures.

Except for the explicitly untraced `-keep` copy operations, immediately before a persistent state transition attempt a `STATE_INTENT` event containing the operation, paths, expected transition, and known pre-operation state. After the transition completes or fails, attempt a `STATE_RESULT` containing the operation outcome and the residual state known from that outcome. Use `UNKNOWN` or `AMBIGUOUS` when a fact is not known; the trace must not claim facts that the operation did not establish. Failure to record either event does not prevent the next normal transition. Logging its own output is not recursively logged.

After both tagger locks are held and before configuration is loaded or messages are examined, `-log` attempts to open `log.txt` as the invocation's append-only trace. Only that tagger writes the file; other processes may read it but must not write to it while the tagger runs. Do not automatically rotate, rename, truncate, or delete it. An operator may archive or clear it only while `sm-tagger` is stopped and accepts that a long logging session can grow without an internal bound. The file-lifetime and append mechanics belong in `implementation.md`.

If opening or using the requested `log.txt` fails, attempt a standard-error diagnostic beginning `ERROR logging to "<full-log-path>":` with the failed event and known residual state when available. Disable file tracing for the rest of that invocation and continue normal startup, current-message processing, later messages, and shutdown. Do not repeatedly reopen or retry the failed trace; a later invocation may try again. A close failure is likewise reported without changing the mail outcome or exit status. Standard error is best effort and is not recursively logged; inability to write it creates no additional fallback. The managed operations used by logging are defined in `implementation.md`.

Independently of sorter `-l`/`-v` or tagger `-log`, attempt a complete human-readable standard-error diagnostic for startup/configuration failures, fatal watcher failures, runtime message-contract errors, and all logging or diagnostic-file failures. Standard error is a best-effort operator channel, not a durable journal; inability to write it does not create another fallback. Out-of-band collection and delivery of these messages is deferred to FUT-005 in `future.md`.

**`process\<basename>.err`** — the tagger's protected, unrestricted UTF-8 diagnostic text for a human, never parsed. **`<basename>.sort.err`**, beside the sorter's current owned HDR, is the corresponding best-effort sorter diagnostic. It normally lies in `proc\`; successful upstream-failure retention puts it in `<mailroot>\failed\`. The first line is a one-sentence reason. Follow it with every relevant evaluated value, operation/error result, and known source/destination state needed to reconcile the message. For `UPSTREAM_FAILED`, include the attempted retention target, any retention I/O error, and the actual current paths, including a split pair. Apply the same string escaping rules when an included value would otherwise span lines or contain controls. Neither diagnostic is sanitized or safe for release outside the trusted filesystem boundary. Both programs write new diagnostic text as UTF-8 without a BOM using CRLF line endings and create the file without replacement. If it already exists, preserve it and report the diagnostic-write failure to stderr; do not truncate or append to it.

The `process\<basename>.hdr.err` and `.eml.err` files are retained original mail bytes, not diagnostic text. They coexist with the parent `<basename>.err` reason file for the `From:` contract-error outcome in §7.3.2.

**`tag-log.txt`** — the best-effort append-only publication-attempt diagnostic for a mapping. During allocation, attempt to create an empty file; report failure to standard error and continue creating the mapping. Allocation and lookup do not add an entry. A later append creates the file if absent when possible. Encode each new entry as UTF-8 without a BOM using exactly this format, including its terminal CRLF:

```
YYYYMMDDTHHMMSSZ <output-basename>\r\n
```

The timestamp is UTC from immediately before publication, with exactly four-digit year, two-digit month/day/hour/minute/second, literal `T`, and literal `Z`; for example, `20260901T143012Z message-42-1`. Its digits must identify a real UTC calendar second, including normal leap-year rules and excluding leap-second value `60`. One ASCII space separates it from the complete child output basename, and the basename occupies the rest of the line. It must be nonempty and contain no CR, LF, or NUL.

For each tagged child, immediately before publishing its EML into `spool\`, attempt to append the identical entry once to every distinct tag mapping referenced by the child: always its individual tag, plus its group tag when group-tag handling was used. Attempt distinct logs in ordinal tag-address order before moving the EML. Apart from best-effort `log.txt` trace events and standard-error diagnostics about those operations, no other mail or mapping transition intervenes. If an append fails, attempt a standard-error diagnostic beginning `ERROR logging to "<full-tag-log-path>":`, leave any appended bytes in place, and continue with the remaining log attempts and that same child's EML/HDR publication. Do not retry the failed append for that child or undo any other append. Later children and fresh messages may attempt the same log normally. The append mechanics belong in `implementation.md`.

An intact entry means only that publication of that output basename was about to be attempted. It does not prove that either move succeeded or that SmarterMail delivered the message. Corruption can obscure, truncate, or merge entries, and failed log operations can leave no entry at all even when a child is successfully published. A valid permanent mapping may have an absent, empty, or damaged log. Reconcile uncertain mail using actual retained parent/child states and SmarterMail records; neither the presence nor the absence of a log entry proves publication or delivery. Failed mail is never resumed automatically, so no automatic replay adds a duplicate entry. A machine/power/storage loss is outside the filesystem reconciliation guarantee (§9.6).

## Locks

- `<spooldir>\proc\sm-sorter.lock` serializes sorter invocations for that Proc queue.
- `<datadir>\sm-tagger.lock` serializes tagger invocations for that datadir/mapping set.
- `<mailroot>\sm-tagger.lock` serializes tagger invocations for the selected spool's process queue, including invocations using different datadirs.

The matching executable creates each file if absent, opens it read/write with no sharing, and holds the handle throughout its invocation. The tagger acquires the data lock first, creates the mailroot if needed, then acquires the queue lock before requested tracing, configuration loading, or queue work. Failure or contention at the second lock releases the first through normal cleanup; it neither waits for another owner nor processes mail. The data lock prevents one mapping set being used by two queues, and the queue lock prevents one queue being consumed using two datadirs. Each file has no payload: a new lock file is empty, and existing contents are neither interpreted nor changed. These are not PID files. The handle provides ownership; a filename left after exit/crash does not require “stale lock” repair. One-shot and watcher modes use the same locks; sorter and tagger ownership remain independent.

## Release and repository trees

These are development/distribution files, not runtime mail state. Neither mail executable reads the repository, release manifest, tests, or documentation during normal processing.

### Release artifacts and installed binaries

`scripts/Publish-Release.ps1` writes the following under the repository. Deploy either both standalone EXEs or both complete application folders, retaining runtime notices. All current forms target Windows x64 and include their runtime.

```text
artifacts/release/
    standalone/
        sm-sorter.exe
        sm-tagger.exe
        DOTNET-LICENSE.txt
        THIRD-PARTY-NOTICES.txt
    sm-sorter/
        sm-sorter.exe
        sm-sorter.deps.json
        sm-sorter.runtimeconfig.json
        <application assemblies, symbols, and bundled runtime files>
        DOTNET-LICENSE.txt
        THIRD-PARTY-NOTICES.txt
    sm-tagger/
        sm-tagger.exe
        sm-tagger.deps.json
        sm-tagger.runtimeconfig.json
        <application assemblies, symbols, and bundled runtime files>
        DOTNET-LICENSE.txt
        THIRD-PARTY-NOTICES.txt
    sm-sorter-win-x64.zip
    sm-tagger-win-x64.zip
    manifest.json
    .singlefile-build/
        <intermediate standalone publishing output; not for deployment>
```

Standalone EXEs need no companion DLLs. An EXE from an application folder needs that **whole folder**. Each ZIP contains its complete named application folder. Runtime payload filenames can change with servicing updates; `manifest.json` is the exact inventory for one build. Publishing recreates generated output directories, so never keep configuration, mail, or operator evidence in them.

`manifest.json` is JSON written by the publish script and read by validation/tests/operators. `CreatedUtc` is the creation timestamp; `Sdk`, `Version`, `RuntimeVersion`, `RuntimeIdentifier`, and `SelfContained` describe the build; `StandaloneExecutables` lists release-relative EXE paths. `Files` contains each deployment file's release-relative `Path`, byte count `Bytes`, and lowercase SHA-256 `Sha256`; `Packages` contains each ZIP's `Path` and `Sha256`. The manifest excludes itself and `.singlefile-build` from its inventory.

The .NET runtime, not application queue logic, may use `%TEMP%\.net` or `DOTNET_BUNDLE_EXTRACT_BASE_DIR` for single-file native extraction when required by the bundled runtime. See [deployment verification](deployment.md#release-files) for tested scope and checks under the actual process identity.

### Repository and local evidence

```text
<repository>/
    README.md, AGENTS.md
    spec.md, storage-reference.md, implementation.md
    bring-up.md, deployment.md, testing-plan.md, assumptions.md
    future.md, validation-report.md, <dated evidence/migration reports>
    SmTagger.slnx, global.json, Directory.Build.props, .gitignore
    src/
        SmSorter/                   sorter executable and diagnostics
        SmTagger/                   tagger entry point
        SmTagger.Shared/            shared platform/identity/HDR classification
        SmTagger.Mail/              tagger parsing and byte-preserving edits
        SmTagger.Engine/            configuration, mappings, processing
    tests/
        SmTagger.Tests/             synthesized fixtures and automated tests
        SmTagger.CrashWorker/       isolated process-crash test helper
        smtp_disconnect.py          local manual SMTP-disconnect probe
    examples/
        README.md
        datadir/senders/
            auth-addresses/auth@example.com/sender-id.txt
            auth-addresses/alias@example.com/sender-id.txt
            sender-ids/22222222-2222-4222-8222-222222222222/
                <the three sender files defined above>
    scripts/
        Validate.ps1
        Publish-Release.ps1
    lab/
        SmTagger.LabServices/        reusable DNS/SMTP capture tools
        SmTagger.LabDkim/            reusable DKIM evidence tooling
        <ignored machine-specific helpers and records>
    samples/                        ignored private actual-server messages
    artifacts/                      ignored releases and test evidence
```

| Area/file | Contents and reader/writer |
|---|---|
| Root Markdown documents | Maintainer-authored contracts, guides, plans, and historical reports; read by operators/developers. `future.md` is deferred scope. |
| `AGENTS.md` | Working instructions read by coding agents, not runtime configuration. |
| `SmTagger.slnx` | XML solution membership read by .NET build/test tooling. |
| `global.json` | JSON SDK selection read by .NET tooling; pins the approved SDK. |
| `Directory.Build.props` | Shared MSBuild XML build/version properties read by projects and the release script. |
| `.gitignore` | Git exclusion patterns for generated output/private evidence, not a security boundary. |
| `src`, project files, `packages.lock.json` | Maintainer-authored C#, MSBuild configuration, and locked NuGet dependency resolution read by build/restore tools. Project `bin`/`obj` directories are generated and ignored. |
| `tests` | Separately synthesized fixtures and automated checks. The crash-worker executable is test-only; the local SMTP probe supports manual experiments. Tests create disposable runtime-layout directories and clean them where appropriate. Local helper scripts are not deployment components. |
| `examples/datadir` | Synthetic sender configuration illustrating the formats above. Replace example identities before use; follow [examples/README.md](examples/README.md). Neither program implicitly reads the repository example location. |
| `scripts` | PowerShell validation/publishing procedures run by developers/operators; produce build/test/release output. |
| `lab` | Optional isolated experiment tools independent of production binaries. Lab services write captures; DKIM tooling reads supplied evidence. File formats are defined in [lab configuration and evidence](#lab-configuration-and-evidence); commands and tool behavior remain in the lab README files. |
| `samples` | Private original HDR/EML pairs read by local compatibility tests. Keep originals unchanged, out of source control/releases/public reports. |
| `artifacts` / `TestResults` | Generated binaries/ZIPs, manifests, TRX results, logs, and scenario-specific JSON/captures written by build/test/lab tools and read by developers. Dated evidence subtrees are not a stable runtime schema. |

Backup, offline restore, and retention policy remain in [spec.md §5.3](spec.md#53-backup-restore-layout-changes-and-retention). Neither executable makes backups or infers recovery from diagnostics. Keep runtime data separate from generated output trees that tools may recreate.

## Lab configuration and evidence

These formats belong to the separate, dependency-free lab tools, which are excluded from the production solution and releases. They do not extend the sorter/tagger storage contract. Commands, listener behavior, verification policy, and limitations remain in the [LabServices workflow](lab/SmTagger.LabServices/README.md) and [LabDkim workflow](lab/SmTagger.LabDkim/README.md).

### DNS/SMTP helper configuration

[`lab/SmTagger.LabServices/lab.example.json`](lab/SmTagger.LabServices/lab.example.json) is the synthetic example for an operator-selected `--config` file. The file is a UTF-8 JSON object, at most 1 MiB. Property names are case-insensitive; unknown properties are rejected. Omitted optional properties use these defaults:

| Property | JSON type | Meaning and validation |
|---|---|---|
| `bindAddress` | string | Default `127.0.0.2`; must be IPv4 loopback. |
| `dnsPort` | integer | Default `53`; range 0–65535. Zero requests an ephemeral test port. DNS UDP and TCP use the same selected port. |
| `smtpPort` | integer | Default `25`; range 0–65535. Must differ from `dnsPort` unless both are zero. |
| `captureDirectory` | string | Default `captures`; resolved relative to the configuration file's directory. An explicit CLI `--capture-dir` instead resolves relative to the working directory. The helper creates the directory. |
| `zones` | array of strings | Required non-null list of 1–16 configured `.test` zones; normalized and deduplicated after the count check. |
| `records` | array of objects | Required non-null list of 1–128 records with the fields below. |

Each record has `name`, `type`, and `value` strings, plus optional integer `preference` (0–65535, default 10). Names are lowercased and trailing dots removed; normalized zones, owner names, and MX targets must end in `.test`, fit 253 ASCII characters, and contain nonempty labels of at most 63 ASCII letters, digits, hyphens, or underscores. Every owner must be at or below a configured zone. `type` is case-insensitive and supports only:

| Type | `value` | `preference` |
|---|---|---|
| `A` | An IPv4 loopback address. | Ignored. |
| `MX` | A normalized `.test` target name; it need not have another record in this configuration. | The MX preference. |
| `TXT` | One string, at most 4,096 UTF-8 bytes. Put an entire TXT value here; the responder splits its wire character strings as necessary. | Ignored. |

For a DKIM TXT record, the owner is the actual selector's `_domainkey` name under a configured `.test` zone and the value is its actual complete public-key record. The checked-in example deliberately has no placeholder DKIM key. The helper reads configuration at startup; it does not save edits or reload it while running.

### SMTP capture tree and envelope JSON

The capture directory is selected by the configuration or CLI; it is not a production queue and has no fixed relationship to `spooldir` or `datadir`.

```text
<captureDirectory>/
  <capture-id>.eml
  <capture-id>.json
```

`capture-id` is a fresh lowercase, hyphenated UUID. The helper creates both files without replacement. EML is written and closed first, then JSON; SMTP success is sent only after both close. Mere file existence does not prove the pair is complete. A write or session failure can leave partial evidence; the helper does not retry, delete, or automatically clean it.

The EML contains the exact received DATA bytes after removing one SMTP transparency dot from each applicable line. It excludes SMTP commands and the terminating dot line, preserves every remaining byte and CRLF, and adds no headers. The separate JSON is indented UTF-8 without a BOM, with these case-sensitive output property names:

| Property | Type | Meaning |
|---|---|---|
| `CaptureId` | string | The UUID shared by both filenames. |
| `ReceivedAtUtc` | timestamp string | UTC `DateTimeOffset` recorded after the EML write and before JSON creation. |
| `Peer` | string or null | Remote endpoint, when available. |
| `Helo` | string | The accepted EHLO/HELO argument. |
| `MailFrom` | string | Envelope sender without angle brackets; the null reverse-path is an empty string. |
| `MailParameters` | string | Accepted MAIL parameters, with outer spaces removed; empty when absent. |
| `Recipients` | array of objects | Accepted recipients in transaction order. Each object has `Address` (without angle brackets) and `Parameters` strings. RCPT parameters are currently refused, so captured `Parameters` is empty. |
| `EmlFileName` | string | The matching `<capture-id>.eml` filename, not an absolute path. |

Envelope spellings are retained separately from EML headers and must not be inferred from visible To/From fields. The capture JSON has no message hash or authentication verdict; record those separately when an experiment needs them.

### Offline DKIM inputs

The verifier reads the caller-selected `--message` and `--key-file` paths and writes neither input. Relative paths resolve from the working directory. Both are size-bounded snapshots read with write sharing denied on Windows.

| Input | File content |
|---|---|
| `--message` | Received RFC message bytes, excluding SMTP commands, dot-stuffing, and the SMTP terminator. Preserve CRLF, folds, transfer encoding, and body bytes; do not supply a decoded body or normalized export. |
| `--key-file` | Strict UTF-8 text of **one** DNS TXT resource record. Concatenate that record's character-string segments in order; remove DNS presentation quotes, parentheses, owner name, TTL, and `IN TXT`. Never concatenate separate TXT resource records. |

The key parser trims outer U+FEFF, space, tab, CR, and LF characters, but the evidence hash covers the supplied text before that trimming. Internal CRLF must be valid folding whitespace. `p=` contains base64 public-key DER; complete RSA SubjectPublicKeyInfo and raw PKCS#1 encodings are accepted, with trailing DER bytes rejected. Public-key evidence does not authenticate its own DNS provenance: preserve the query time, server, response, supplied `--dns-name`, and `--expected-domain` separately.

Input bounds are 32 MiB per message, a header/body separator starting at an offset no greater than 256 KiB, 64 KiB per logical header including its line endings, 2,048 parsed headers, 2,048 `h=` entries, 16 signatures, and 32 KiB of UTF-8 key-record text. The CLI rejects an oversized input file before verification. Supported algorithms, domain/key policies, canonicalization, and the distinction between cryptographic success and deployment evidence remain in the LabDkim workflow.

### DKIM report JSON

A normal verification command emits one indented JSON report to stdout; it creates no report file. Save stdout to an operator-selected evidence path if persistence is wanted. Input errors can instead produce a stderr diagnostic without a JSON report. Property names retain the following capitalization; nullable values are serialized as JSON `null`.

| Top-level property | Type | Meaning |
|---|---|---|
| `Verified` | boolean | No message-level error and at least one signature has `Status: "Pass"`. |
| `Scope` | string | The verifier's explicit offline, supplied-key scope and exclusions. |
| `InputSha256` | string or null | Lowercase hexadecimal SHA-256 of the exact message bytes; null if not reached. |
| `SuppliedKeyRecordSha256` | string or null | Lowercase hexadecimal SHA-256 of the supplied key text re-encoded as UTF-8, before parser trimming; null if not reached. |
| `ExpectedDomain` | string | The caller's expected signing-domain argument. |
| `SuppliedDnsName` | string | The caller's DNS owner-name argument. |
| `CheckedUtc` | timestamp string | UTC verification time used for expiry checks. |
| `MessageError` | string or null | Message-level rejection reason, or null. |
| `Signatures` | array of objects | Per-signature results in parsed header order, using the fields below. |

| Signature property | Type | Meaning |
|---|---|---|
| `HeaderIndex` | integer | Zero-based index among **all parsed message headers**, not just signatures. |
| `Status` | string | `Pass`, `Fail`, or `Unsupported`. |
| `Reason` | string | Human-readable outcome or rejection. |
| `Domain`, `Selector` | string or null | Parsed `d=` and `s=` values when reached. |
| `HeaderCanonicalization`, `BodyCanonicalization` | string or null | Selected canonicalization names when reached. |
| `SignedHeaders` | array of strings | Parsed `h=` names in order, lowercased; initially empty. Repeated names remain repeated. |
| `BodyHashMatches`, `HeaderSignatureMatches` | boolean or null | Independent checks; null means that check was not reached. |
| `RsaBits` | integer or null | Imported RSA key size when reached. |
| `CanonicalBodyBytes` | integer or null | Length of the complete canonical body when reached. |
| `KeyTestingMode` | boolean | Whether a parsed key declares `t=y`; defaults false before that stage. |
| `Expired` | boolean | Whether a parsed expiration is before the check time; defaults false before that stage. |

Interpret incomplete results with `Status` and `Reason`; a default false flag is not proof its validation stage ran. The report omits message content and public-key material. Keep the exact inputs and capture/DNS provenance with it.

### Self-test and fixture storage

```text
<OS temporary directory>/
  sm-tagger-lab-selftest-<UUID>/     # LabServices: retained capture or empty listener-test directory
    <capture-id>.eml               # only when that test captured DATA
    <capture-id>.json

lab/SmTagger.LabDkim/Fixtures/
  rfc8463.eml                      # public synthetic signed RFC message
  rfc8463-key.txt                  # corresponding public RSA TXT record
  README.md                       # provenance, exact hashes, formatting, and license
```

LabServices self-tests allocate fresh temporary directories and retain them; the main capture path is printed, while the listener-failure check can leave a separate empty directory. LabDkim self-tests create no temporary evidence directory or key files: synthesized keys and messages remain in memory, results go to the console, and the public fixtures above are read. A lab DKIM build copies `Fixtures/` beside its executable for those checks. The fixture README records the exact historical source bytes and license, rather than defining another general input format. Neither tool deletes accumulated evidence automatically.
