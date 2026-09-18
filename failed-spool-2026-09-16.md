# Failed HDR handling and spool-consumer experiment — 2026-09-16

The question is whether moving a SmarterMail `Failed` pair out of Proc and into the normal spool preserves, delivers, rejects, or deletes it. At the time of this experiment, the sorter held these pairs in Proc. The result below led to the separately approved and implemented [external retention policy in rc.6](failed-retention-2026-09-16.md); the consumer experiment itself ran without either processor.

## Vendor guidance

Matt Petty, identified as a SmarterTools senior software developer, wrote on May 9, 2024: “The spool will automatically remove spool messages marked as Failed.” [Employee reply](https://portal.smartertools.com/community/a96224/archive-and-spool-overview.aspx)

A February 6, 2024 employee reply explains that Failed marks bad/incomplete SMTP input, the normal spool removes it, and an external Proc handler takes responsibility for failed/orphan handling while interception is enabled. [Employee reply](https://portal.smartertools.com/community/a95888/failed-incoming-emails-remain-in-proc-folder_.aspx)

Neither statement distinguishes a legitimately aborted SMTP transaction from the [observed post-250 disconnect failure](live-smartermail-2026-09-16.md#2-an-immediate-smtp-disconnect-can-produce-failed-after-data-250). The first HDR line alone does not establish whether SMTP responsibility had already transferred. The normal spool is therefore a potentially destructive destination for a Failed pair, not a safe storage folder.

## Controlled experiment

Environment: the existing local Windows 11 / SmarterMail `100.0.9742.26305+420ad0abfc12ec298959057b3a7aae179d13f0e1` laboratory. Fresh synthetic `.test` messages only; no earlier held or private sample message is replayed. A separate loopback SMTP receiver captures any delivery. The synthetic domain already routes to that receiver's explicit loopback IP. Sorter and tagger remain stopped. No SmarterMail configuration change is required.

Three separately recorded cases cover a complete SMTP transaction with QUIT as a delivery control, a complete transaction followed by immediate socket close after the client receives final DATA 250, and an aborted DATA transfer without its terminating dot. Each observed completed producer pair is copied and hashed before any handoff. A mismatched status or absent final EML is a coverage limitation, not permission to fabricate a status or retry an earlier message.

For the consumer test, preserve all bytes and publish EML first, plain HDR last, with non-overwriting same-volume moves. Observe names and attributes after publication without holding read handles on active files. Record receiver captures, queue disappearance, and relevant SmarterMail log entries independently; disappearance alone is not evidence of successful delivery. Raw evidence belongs under ignored `artifacts/failed-spool-20260916/`.

### Results

| Fresh case | SMTP outcome / closed HDR status | Result after EML-first/HDR-last handoff |
|---|---|---|
| Normal control, basename `422405313432` | Client received final 250, then QUIT received 221; `Written ` | Exactly one expected SMTP delivery; queue cleared |
| Immediate close, basename `422405313433` | Client received final DATA 250, then immediately closed without QUIT; `Failed ` | SM removed both queue files; zero receiver captures during 30 seconds |
| Aborted DATA, basename `422405313434` | Client sent a message fragment without the terminating dot; no final DATA reply; `Failed ` | SM removed both queue files; zero receiver captures during 30 seconds |

For both Failed cases, SmarterMail's delivery log explicitly records `Removing Spool message: Killed: False, Failed: True, Finished: False`, followed by a delivery-failed entry. The original filenames were first observed absent approximately 2.60 seconds and 1.04 seconds after the respective HDR handoffs. Those are polling observations, not precise deletion times. No live queue files remained. This establishes that the normal spool consumer on this tested build discards the post-250 Failed input just as it discards the incomplete transfer; it does not recover or deliver either specimen.

The post-250 SMTP log again records final `250 OK`, then a disconnected-client rejection, then publication of the Failed pair. The malformed-input control never completed DATA. The producer's status bytes were retained unchanged in both handoffs; no status was changed to Written and neither sorter nor tagger processed these cases.

There was exactly one receiver capture across the experiment: the successful control. No failure notification or other new capture was observed in either Failed case's 30-second window, and the queue was empty at final reconciliation. This finite observation is not proof that a notification could never occur later. No packet capture establishes whether either close used FIN or RST.

All three pre-handoff HDR/EML copies were verified against their recorded hashes and remain outside the SM queue. Older held experiments were untouched, the four original manual-test files still matched their preservation hashes, and the prior private error-file inventory was unchanged. The helper receiver was stopped afterward. Settings readback matched the baseline, no sorter/tagger worker remained running, MailService kept its original process, and the user's visible Procmon session remained open. Proc interception remains enabled, as before this experiment.

Local evidence: [final audit](artifacts/failed-spool-20260916/final-audit.json), [SM log excerpts](artifacts/failed-spool-20260916/sm-log-audit.json), [settings readback](artifacts/failed-spool-20260916/settings-after.json), and [process readback](artifacts/failed-spool-20260916/restored-processes.json). Each directory under `artifacts/failed-spool-20260916/cases/` contains the fresh fixture, sanitized client transcript, preserved producer pair, move events, full observation timeline, and result. These generated local records are ignored by Git.

## Approved retention policy

Use `<datadir>\failed\` as an inert holding directory outside the entire SmarterMail queue tree, on the same ordinary local NTFS volume. Preserve the original EML bytes and claimed `.hdr.sort` file under their original basename, and attempt a `.sort.err` diagnostic there. That diagnostic is best-effort, not a prerequisite for retaining the message. A retained HDR keeps its non-live suffix. Record each file's actual retained path in stderr and the optional sorter email log.

This project policy was approved after the experiment and implemented in rc.6. It clears terminal upstream failures from Proc without giving SmarterMail permission to dispose of them. It is limited to confirmed `UPSTREAM_FAILED` cases; other errors retain their existing behavior. There is no automatic deletion, replay, status rewriting, or release into spool.

**Owner disposition, 2026-09-16 — resolved for version 1.** The owner accepts moving these pairs into `failed` as the complete project response to Failed-on-disconnect. Further testing of the SM disconnect setting or an upstream fix is not required to close this implementation item. Retained mail remains subject to manual disposition; this decision does not claim that SmarterMail's producer behavior changed.

The implementation preserves no-overwrite behavior, retains the actual partial state if a move fails, and requires manual handling of existing retained files. Diagnostics describe completed moves and any split locations, rather than relocating an earlier diagnostic whose paths are now stale. A diagnostic collision or logging failure does not itself prevent message retention. Folder preparation and retention monitoring are described in [bring-up.md](bring-up.md), with verification in the [retention report](failed-retention-2026-09-16.md). Moving evidence to a holding folder does not itself repair the SMTP acceptance/disconnect issue.

## Historical setting observation; no further experiment required

The current official SMTP-In help documents **Continue delivery if session is disconnected by client**, disabled by default. It describes accepting legacy clients that disconnect before acknowledgment completes and warns that enabling it can cause duplicate deliveries when senders retry. [Official help](https://help.smartertools.com/SmarterMail/Current/Topics/SystemAdmin/Settings/SMTP-In)

A read-only check of this installation found `globalMailSettings.smtpSettings.smtpAcceptDisconnectedClients = false`; the installed frontend maps that field to the documented control. Its effect on the observed **client-received-250** race has not been tested. The setting was left unchanged. This observation remains historical context, not an outstanding investigation under the owner's resolution above or evidence that a Failed pair is safe to deliver or discard.
