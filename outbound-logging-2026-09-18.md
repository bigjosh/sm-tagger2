# Live outbound logging test — 2026-09-18

**SmarterMail wrote the complete outbound SMTP conversation for both messages, but its Related Traffic search failed for the tagged child's hyphenated session ID.** Disabling Related Traffic and searching the actual bracketed session ID returned the complete conversation. This reproduces a possible explanation for a delivered message appearing absent from the log viewer; it does not establish the cause of a particular production search without that server's evidence.

The later [base36 filename experiment](basename-logging-2026-09-18.md) refines the likely parser boundary: suffixes `g`, `z`, `1z`, and `Z` also fail, while isolated tests of `1`, `10`, `a`, `f`, `1f`, and `F` group correctly. Removing the hyphen alone does not make general base36 work. No program naming behavior changed.

## Tested scope

- Local Windows 11 Pro, SmarterMail `100.0.9742.26305+420ad0abfc12ec298959057b3a7aae179d13f0e1`, and the published local `1.0.0-rc.13` standalone sorter and tagger.
- Two fresh synthetic messages submitted through authenticated SMTP, discovered in Proc, diverted by the real sorter, processed by the real tagger, and delivered by SmarterMail to a Python standard-library SMTP receiver bound to `127.0.0.2:25`.
- The existing `sequence.test` test-domain route and detailed Delivery/SMTP logging were used unchanged. The receiver accepted only the two test recipients and completed DATA acceptance and QUIT. No public mail or production server was used.
- One enrolled control had no configured-private-address match and passed through the tagger unchanged. The other matched and produced one tagged child. Both executable invocations exited successfully, and the receiver captured each message once.

| Case | Original basename | Published basename | Delivery log session | Raw lines |
| --- | --- | --- | --- | --- |
| Unchanged control | `422405313454` | `422405313454` | `[05313454]` | 34 |
| Tagged child | `422405313455` | `422405313455-1` | `[313455-1]` | 34 |

The control EML was byte-identical through the tagger. Both cases preserved the body and HDR authentication value through the tagger. The tagged From address matched the independently received message. These are scoped live observations of the current mail workspace and binaries, not approval of other routes, Windows Server, public-provider delivery, TLS, or all deployment gates.

## Raw logging versus log search

SmarterTools identifies **Delivery** as the outbound conversation log and **SMTP** as the incoming connection log. See the [staff explanation](https://portal.smartertools.com/community/a94043/smtp-send-and-smtp-receive-logs.aspx). Both local conversations were present in `C:\SmarterMail\Logs\2026.09.18-delivery.log`, including EHLO, MAIL FROM, RCPT TO, DATA, final acceptance, QUIT, and Delivered. An excerpt from the tagged session (completion-line tab rendered as a space):

```text
14:08:54.154 [313455-1] CMD: DATA
14:08:54.185 [313455-1] RSP: 354 send message; end with dot
14:08:54.232 [313455-1] RSP: 250 2.0.0 accepted by controlled local receiver
14:08:54.232 [313455-1] CMD: QUIT
14:08:54.263 [313455-1] RSP: 221 2.0.0 closing connection
14:08:54.267 [313455-1] Delivery for sender@sequence.test to logging-tagged-20260918t180829z@sequence.test has completed (Delivered)
14:08:57.001 [313455-1] Delivery finished for sender@sequence.test at 2:08:57 PM [id:422405313455-1]
```

The installed web interface uses `POST /api/v1/settings/sysadmin/log-files`. Read-only searches through that endpoint tested the viewer's `related` option against the same date and `type: "delivery"`. All responses below reported success and no truncation:

| Search | Related Traffic off | Related Traffic on |
| --- | --- | --- |
| Tagged recipient | 4 matching lines | 0 lines |
| Full tagged parent or child basename | 1 completion line | 0 lines |
| Tagged session `[313455-1]` | All 34 correct session lines | 0 lines |
| Control session `[05313454]` | All 34 correct session lines | 35 rendered lines with incorrectly combined sessions |
| `CMD: EHLO` | 2 matching lines | The same incorrectly combined output |
| Either RFC Message-ID | 0 lines | 0 lines |

The 35-line control result actually contained all 68 raw lines as substrings: tagged entries were concatenated into surrounding output without proper line separation. The matching-only tagged-session result exactly matched the 34 raw lines after allowing for the API's added date prefix. Receiver transcripts independently confirmed successful DATA acceptance for both messages.

This is a reproduced SmarterMail log-search bug affecting this hyphenated delivery-session ID. Related Traffic **appears** to misparse the identifier; the internal parser implementation was not inspected. Neither raw logging nor delivery failed. The RFC Message-IDs appeared in the incoming SMTP log, but were not useful search keys for these Delivery sessions. Do not treat an empty viewer search as evidence that a message needs resubmission.

## Verified operator workaround

1. Select the **Delivery** log and the relevant date. Detailed logging must already have been enabled when the conversation occurred to recover that level of detail.
2. Disable **Display Related Traffic** / select **Only Matching Rows**.
3. Search the recipient or full published child basename to find its bracketed Delivery session ID.
4. Search that exact bracketed ID, keeping Related Traffic disabled. In this test, `[313455-1]` returned the entire conversation.

Alternatively inspect/download the raw `YYYY.MM.DD-delivery.log` and search there. The [current troubleshooting help](https://help.smartertools.com/SmarterMail/Current/Topics/SystemAdmin/Manage/Troubleshooting) describes log levels, related/matching-only searches, and the browser result-size limit. The [staff recipe using EHLO and Related Traffic](https://portal.smartertools.com/community/a92626/is-there-a-way-to-see-the-outgoing-smtp-log-and-only-those.aspx) needs this caveat for our tested child session.

No queue-naming or application behavior was changed as part of this investigation. Recheck search correlation after SmarterMail updates (`VERSION-SENSITIVE-015` / `LAB-015`), including negative or otherwise different basenames used on the actual deployment. Numeric-only child naming would require a separate design decision about namespace uniqueness and retained-state correlation; it is not an incidental logging fix.

## Evidence and cleanup

Protected local evidence is under `artifacts/outbound-log-cycle-20260918/`, which is ignored and unavailable in a fresh public checkout:

- [Raw Delivery log slice](artifacts/outbound-log-cycle-20260918/2026.09.18-delivery.log), [run results](artifacts/outbound-log-cycle-20260918/run-result.json), and [byte/settings audit](artifacts/outbound-log-cycle-20260918/audit.json).
- [Initial search matrix](artifacts/outbound-log-cycle-20260918/search-summary.json), [workaround results](artifacts/outbound-log-cycle-20260918/workaround-summary.json), and individual saved query/response JSON files.
- The `control`, `tagged`, and `sink` subdirectories retain synthetic input/output pairs, executable traces, received messages, and SMTP transcripts. Authentication material remains separately protected under `private` and must not be published.

The receiver was stopped, both test deliveries completed, and this test's inert debugging copies were archived before removal from the live work queue. The pre-existing Proc message pair was unchanged. SmarterMail settings, routing, DNS, firewall, service state, and the user's Procmon session were left unchanged. The standard empty sorter/tagger lock files remain. No source or binary change was needed for this test.
