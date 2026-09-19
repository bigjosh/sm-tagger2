# Base36 filename and Delivery log search experiment — 2026-09-18

**Removing the hyphen is insufficient for base36.** On the locally installed SmarterMail build 9742, numeric-prefix basenames with suffixes `g`, `z`, `1z`, or uppercase `Z` still return no Related Traffic results. A separate clean run with suffixes `1`, `10`, `a`, `f`, `1f`, and uppercase `F` returned each complete conversation correctly. The result suggests hexadecimal session-ID recognition, not general base36; the server-side parser source was not inspected.

This follows the [hyphenated-child investigation](outbound-logging-2026-09-18.md). The first three runs were filename experiments before implementation changes. Every run here published prepared spool fixtures without invoking either executable. The subsequently approved rc.14 implementation uses a literal `c` delimiter; its code and executable verification are recorded separately in [validation-report.md](validation-report.md).

**Both tested literal delimiters, `a` and the finally chosen `c`, passed their nine-case checks**, including multi-digit children and an adjacent parent. See the [third run](#third-run-a-separator-and-decimal-child-number) and [fourth run](#fourth-run-c-separator-and-decimal-child-number). Only `c` was selected for implementation.

## Method and scope

Fresh synthetic EML/HDR pairs were prepared from the earlier verified synthetic HDR format and published directly into `C:\SmarterMail\Spool`, EML first and HDR last, without replacing existing files. The existing `sequence.test` loopback route delivered them to the Python SMTP receiver at `127.0.0.2:25`. Every message had a distinct recipient and Message-ID. The receiver returned DATA 250 and QUIT 221 and preserved the received bytes and transcript.

The test used SmarterMail `100.0.9742.26305+420ad0abfc12ec298959057b3a7aae179d13f0e1` on Windows 11 Pro. Relevant domain and Proc settings were checked before and after; none were changed. There was no public delivery or new DNS/firewall configuration. These are prepared spool-consumer fixtures, not additional producer-readiness, tagger-rewrite, or Windows Server tests.

Each case was correlated against its actual bracketed ID in the raw Delivery log. The installed viewer submits its searches to `POST /api/v1/settings/sysadmin/log-files` and displays the response directly. Queries tested recipient, full basename, and bracketed session with Related Traffic both off and on. All responses succeeded and reported no truncation.

## First run: mixed supported and unsupported names

Most cases shared numeric prefix `838660290900`; the uppercase case used a different prefix to avoid a case-insensitive filesystem collision. A numeric-only sentinel followed after the other nine deliveries completed.

| Suffix | Published basename | Related Traffic search by exact bracketed session |
| --- | --- | --- |
| `1` | `8386602909001` | Own 34 lines found, plus concatenated unsupported traffic |
| `a` | `838660290900a` | Own 34 lines found, plus concatenated unsupported traffic |
| `f` | `838660290900f` | Own 34 lines found, plus concatenated unsupported traffic |
| `g` | `838660290900g` | Zero results |
| `z` | `838660290900z` | Zero results |
| `10` | `83866029090010` | Own 34 lines found, plus concatenated unsupported traffic |
| `1z` | `8386602909001z` | Zero results |
| `Z` | `838660290901Z` | Zero results |
| `-1` (control) | `838660290900-1` | Zero results |
| None (numeric sentinel) | `838660290902` | Exactly its own 34 lines |

All ten messages delivered once, and all ten had complete 34-line raw conversations. Matching-only searches of their exact bracketed IDs returned the correct lines. The five unsupported sessions' 170 lines were wrongly concatenated into the four recognized sessions' Related Traffic output. This is the same grouping defect observed with the original hyphenated child, not a delivery failure. The final numeric sentinel provided a clean boundary before the follow-up.

## Second run: isolated digits and hexadecimal letters

Six further fresh messages used no unsupported filename characters. Each recipient query and exact bracketed-session query with Related Traffic enabled returned exactly the corresponding 34 raw lines after the API's added date prefix was removed, with no lines from another session.

| Suffix | Published basename | Actual session | Related Traffic result |
| --- | --- | --- | --- |
| `1` | `9338904796001` | `[04796001]` | Exact 34-line match |
| `10` | `93389047960010` | `[47960010]` | Exact 34-line match |
| `a` | `933890479600a` | `[0479600a]` | Exact 34-line match |
| `f` | `933890479600f` | `[0479600f]` | Exact 34-line match |
| `1f` | `9338904796001f` | `[4796001f]` | Exact 34-line match |
| `F` | `933890479601F` | `[0479601F]` | Exact 34-line match |

Basename searches are substring searches: the name ending `1` also matches names ending `10` and `1f`. The extra sessions in that particular result are expected. Use a distinct recipient or exact bracketed session when verifying correlation.

Base36 child value 16 is `g`, so a base36 scheme cannot rely on passing tests for only its first few children. Hexadecimal is a promising alternative for this log viewer based on the tested cases. Any production naming change still needs its own namespace-uniqueness and retained-state review; this experiment does not approve a complete replacement naming contract. SmarterMail upgrades require rechecking this behavior under `VERSION-SENSITIVE-015` / `LAB-015`.

## Third run: a separator and decimal child number

The user's proposed literal `a` delimiter was tested with the same receiver, publication order, and exact raw-versus-search comparison. Eight children shared the specified base `42615432`; a ninth used neighboring base `42615433` to check that its session stayed separate.

| Published basename | Actual Delivery session | Related Traffic result |
| --- | --- | --- |
| `42615432a1` | `[615432a1]` | Exact 34-line match |
| `42615432a2` | `[615432a2]` | Exact 34-line match |
| `42615432a9` | `[615432a9]` | Exact 34-line match |
| `42615432a10` | `[15432a10]` | Exact 34-line match |
| `42615432a16` | `[15432a16]` | Exact 34-line match |
| `42615432a36` | `[15432a36]` | Exact 34-line match |
| `42615432a99` | `[15432a99]` | Exact 34-line match |
| `42615432a100` | `[5432a100]` | Exact 34-line match |
| `42615433a1` | `[615433a1]` | Exact 34-line match |

All nine arrived once with their expected Message-IDs and decoded bodies. Both recipient and exact bracketed-session queries with Related Traffic enabled returned each message's complete 34-line conversation, with no lines from another session. All responses succeeded without truncation. The observed IDs retain the final eight basename characters; this test does not establish global uniqueness of these shortened log IDs across arbitrary parents.

Searching basename `42615432a1` also finds `42615432a10`, `42615432a16`, and `42615432a100`, because the search matches substrings. That query correctly returns four complete sessions. Use the distinct recipient or exact bracketed session ID for one conversation.

The user's preferred parent lookup was separately verified: searching `42615432` with Related Traffic enabled returned exactly all eight children's 272 raw lines, excluding neighboring parent `42615433a1`, with no truncation. This provides the desired whole-parent view. Square brackets refer to the log's session notation, for example `[615432a1]`; that exact search is useful only when narrowing to an individual child. See the [parent-search result](artifacts/a-delimiter-log-cycle-20260918/parent-search-summary.json).

The results support this proposed format for the tested server and parent/child values. A decimal child number needs no hexadecimal conversion: the literal `a` and decimal digits are already compatible with the observed parser. This remains a filename compatibility experiment, not an implemented naming change or a vendor guarantee.

## Fourth run: c separator and decimal child number

After the user selected `c` for "child", nine fresh prepared pairs repeated the third run with `42615432c1`, `c2`, `c9`, `c10`, `c16`, `c36`, `c99`, `c100`, and neighboring `42615433c1`. Every message delivered once and had a complete 34-line raw conversation. All recipient and exact bracketed-session searches with Related Traffic enabled matched their own raw lines exactly, with no foreign session lines and no truncation. For example, `42615432c1` logged as `[615432c1]`, and `42615432c100` as `[5432c100]`.

This verifies the selected delimiter on the tested build for these prepared consumer fixtures. It does not replace the independent rc.14 executable tests or the remaining deployment evidence. The receiver was stopped and relevant settings and pre-existing mail were unchanged.

## Evidence and cleanup

Ignored local evidence, unavailable in a fresh public checkout:

- First run: [cases and receipts](artifacts/base36-log-cycle-20260918/cases.json), [raw Delivery slice](artifacts/base36-log-cycle-20260918/2026.09.18-delivery.log), [search matrix](artifacts/base36-log-cycle-20260918/search-summary.json), and [audit note](artifacts/base36-log-cycle-20260918/audit-note.json).
- Isolated follow-up: [cases and receipts](artifacts/hex-log-cycle-20260918/cases.json), [raw Delivery slice](artifacts/hex-log-cycle-20260918/2026.09.18-delivery.log), and [search matrix](artifacts/hex-log-cycle-20260918/search-summary.json).
- Proposed `a` delimiter: [cases and receipts](artifacts/a-delimiter-log-cycle-20260918/cases.json), [raw Delivery slice](artifacts/a-delimiter-log-cycle-20260918/2026.09.18-delivery.log), and [search matrix](artifacts/a-delimiter-log-cycle-20260918/search-summary.json).
- Selected `c` delimiter: [cases and receipts](artifacts/c-delimiter-log-cycle-20260918/cases.json), [raw Delivery slice](artifacts/c-delimiter-log-cycle-20260918/2026.09.18-delivery.log), and [search matrix](artifacts/c-delimiter-log-cycle-20260918/search-summary.json).
- Each run's `pairs`, `sink`, and `searches` directories preserve prepared inputs, received messages/transcripts, and individual API query/response records. Private authentication and settings snapshots must remain unpublished.

All 34 messages across the four runs arrived exactly once with the expected Message-ID and decoded body. SmarterMail converted these prepared plain-text bodies to quoted-printable and changed trailing CRLF; decoded body comparisons allow trailing CRLF differences. The first run's initial literal-body assertion caught that transformation after delivery; verification resumed read-only from saved evidence, without resubmitting messages.

The receiver was stopped after each run. All test pairs left spool through successful delivery. Pre-existing queue-file hashes remained unchanged, and relevant server settings were identical before/after. These fixture runs required no server configuration change; the separate rc.14 naming implementation is identified above.
