# Upstream-failure retention — 2026-09-16

Local candidate **1.0.0-rc.6** moves confirmed SmarterMail `Failed` pairs from Proc into `<datadir>\failed\`. This implements the approved policy following the [Failed-spool consumer experiment](failed-spool-2026-09-16.md), which showed the tested SM build deleting these pairs when returned to normal spool. Retention protects the evidence; it does not repair SmarterMail's post-250 disconnect behavior or decide whether a failed transaction was accepted.

**Owner disposition, 2026-09-16:** Failed-on-disconnect is resolved for version 1 by this implemented retention policy. No additional disconnect-setting experiment or upstream correction is required for our project to move forward. The existing manual-disposition contract remains in force.

The latest full run passed **455/455 tests, with zero failures and zero skips**, after the user disabled Smart App Control. Both standalone executables were unchanged; all 14 previously blocked cases now pass. The earlier failures remain recorded below. Read-only checks confirm Smart App Control is Off. The assistant made no security-policy, allow-list, certificate-trust, or signing changes. Execution with Smart App Control enabled remains unverified for this unsigned candidate.

The local launch blocker is closed under that verified configuration. Supporting a different host policy is outside this local result and is not an active blocker for the current host.

## Implemented contract

For an exact case-sensitive `Failed` first HDR line, ignoring only terminal SP/HTAB for comparison, the sorter:

1. Claims the existing plain HDR as `<basename>.hdr.sort` in Proc, using the existing ownership rule.
2. Creates `<datadir>\failed\` only if needed, then moves `<basename>.eml` there before moving `<basename>.hdr.sort` there. The retained header never regains a live `.hdr` suffix. All message bytes remain unchanged, and the sorter does not read the EML body or require parseable later HDR metadata.
3. Attempts one terminal `ERROR` log record with reason `UPSTREAM_FAILED`, reports stderr, and attempts `<basename>.sort.err` beside the **current owned HDR**, after the retention attempt. Successful retention remains a failed message outcome and one-shot exit 1; watch mode continues with later fresh mail.

The folder must be outside the entire SM spool tree on the same ordinary local NTFS volume. These are trusted installation conditions, without new runtime topology or permission checks. Neither `failed` nor `profiles` becomes a startup or normal pass-through dependency. There is no new option, status rewriting, automatic replay, migration of earlier Proc residuals, cleanup, or watcher scan of retained mail. Other error classes keep their existing retention locations.

| Retention boundary | Last-known owned file locations | Diagnostic attempt |
|---|---|---|
| Directory creation or EML move fails | EML and `.hdr.sort` source paths in Proc | Proc |
| EML move succeeds; HDR move fails | EML in `failed`; `.hdr.sort` in Proc | Proc |
| Both moves succeed | EML and `.hdr.sort` in `failed` | `failed` |

No move or diagnostic overwrites earlier evidence. The first move failure stops further moves without rollback. An external deletion can invalidate a last-known path; the error records the actual failed operation rather than promising that another process left the file present. Diagnostic and email-log failures cannot prevent the retention attempt. The folder requires manual inspection and disposition; see [bring-up.md](bring-up.md).

## Automated verification

Added 15 filesystem cases in [SorterFailedRetentionTests.cs](tests/SmTagger.Tests/SorterFailedRetentionTests.cs) and one standalone executable case. Existing readiness cases now check the new location, and the application-folder watcher test now checks continued processing and restart while retained files are locked.

The filesystem cases cover exact opaque bytes, padded status, lazy/existing directories, a directory path occupied by a file, EML/HDR destination collisions, real Windows delete-sharing denial, an EML that cannot be read but can be renamed, diagnostic collisions, log failure, final diagnostic paths, restart/no replay, and unchanged handling of other holds and normal passes. They add no production fault controls or recovery paths. The focused sorter run passed **75/75**.

The full run before the user's policy change executed **455 tests: 441 passed, 14 failed, zero skipped**. All 14 failures were Windows refusing to launch the copied standalone `sm-sorter.exe`, before its entry point; there were no code assertion failures. The application-folder failed-retention/watcher/restart case and all other application-folder cases passed. The three private-sample cases also ran; their original files and contents remain excluded from shared source and packages.

Windows Code Integrity events 3033 and 3077 corroborate the signing-policy rejection. Event 3077 names Smart App Control's `VerifiedAndReputableDesktop` policy and the exact sorter hash; correlated event 3089 reports no signature. These events and the process-start exception establish a launch/load admission failure, not a failure during mail processing. They do not reveal the exact cloud reputation verdict or explain why other hashes or packaging were allowed. An explicit standalone retention retry also failed to launch while that policy was active. The final build and format checks passed with zero build warnings/errors. Required CRLF normalization changed the sorter artifact identity between the first and final package checks; both full-run records and both artifact manifests were preserved. That rebuilt standalone also remained blocked before the user changed the setting.

After the user disabled Smart App Control, the entire same 455-case suite was rerun **without rebuilding or changing either standalone binary**. It passed **455/455, zero failures and zero skips**, including all 14 previously blocked cases and the standalone failed-retention check. This establishes execution and the tested behavior under the current Off policy; it does not establish enabled-policy compatibility. Earlier launch failures were not erased or reclassified as passes.

Commands used:

```powershell
dotnet format SmTagger.slnx --verify-no-changes --no-restore
dotnet build SmTagger.slnx -c Release
.\scripts\Publish-Release.ps1
dotnet test SmTagger.slnx -c Release --no-build --logger 'trx;LogFileName=failed-retention-final.trx' --results-directory artifacts/test-results/failed-retention
```

Local evidence: [final full TRX](artifacts/test-results/failed-retention/failed-retention-final.trx), [focused TRX](artifacts/test-results/failed-retention/failed-retention-focused-final.trx), [blocked retry](artifacts/test-results/failed-retention/failed-retention-standalone-retry.trx), and [final Windows events](artifacts/failed-retention-20260916/application-control-events-final.json). An initial test-only path-separator expectation was corrected before the focused run passed; its earlier failing record is preserved.

The subsequent policy-change retest used only `dotnet test SmTagger.slnx -c Release --no-build`, with a separate TRX path. Evidence: [455-pass retest](artifacts/test-results/failed-retention/failed-retention-smartapp-off.trx), [unchanged hashes and previously blocked case comparison](artifacts/failed-retention-20260916/smartapp-retest/result.json), and [policy/event evidence](artifacts/failed-retention-20260916/smartapp-retest/policy-evidence.json). The retest left no test workers running and did not touch the SM configuration or repeat the live SMTP experiment. MailService and the user's Procmon remained running.

## Fresh SmarterMail verification

Fresh synthetic localhost experiments are recorded under `artifacts/failed-retention-20260916/cases/`. They use the published **application-folder sorter**, a new empty `senders` directory without `profiles`, and a separate loopback SMTP receiver. No tagger runs, earlier message replay, or SM settings changes are involved. The data root is `C:\SmarterMail\FailedRetention20260916\data`, outside `C:\SmarterMail\Spool` on local NTFS. Each actual closed producer pair is preserved and hashed before the one-shot sorter runs.

| Fresh SMTP case | Closed producer status | Sorter and receiver result |
|---|---|---|
| Client receives final DATA 250, then immediately closes without QUIT; basename `422405313435` | `Failed ` | EML and `.hdr.sort` retained byte-for-byte, diagnostic beside retained HDR, one `ERROR reason=UPSTREAM_FAILED`, exit 1; zero captures in 30 seconds |
| Aborted DATA fragment without terminating dot or final DATA reply; basename `422405313436` | `Failed ` | Same complete retention and diagnostics, exit 1; zero captures in 30 seconds |
| Later complete transaction with final DATA 250 and QUIT 221; basename `422405313437` | `Written ` | Unenrolled-auth pass, exit 0; exactly one matching delivery to the intended loopback recipient in 30 seconds |

All three cases passed. The two failures left exactly six files in `failed`: two EMLs, two inert HDRs, and two diagnostics. Their retained hashes still matched the preserved originals at final reconciliation; there were no corresponding leftovers in Proc. Each retained-message diagnostic and optional email log named the actual final paths. The only receiver capture across the entire experiment was the later successful control. Finite observation does not establish an unlimited guarantee about future delivery or notifications.

The first live-harness preflight encountered the automated test runner's sorter/tagger processes and stopped **before sending mail**. It was preserved as an unsuccessful preflight; the three actual submissions ran only after those workers exited. Neither earlier held mail nor any of these fresh submissions was replayed. Standalone launch failures were kept separate from the application-folder live results.

The live queues were empty afterward. All 434 earlier private-queue files and four preserved manual-test originals still matched their prior hashes. Settings readback matched the baseline, including Proc enabled and `smtpAcceptDisconnectedClients = false`. The owned receiver was stopped, no sorter/tagger worker remained, MailService retained its existing process, and the user's visible Procmon remained open. The fresh retained evidence was left in place for inspection.

Evidence: [final audit](artifacts/failed-retention-20260916/final-audit.json), [settings readback](artifacts/failed-retention-20260916/settings-after.json), and [process readback](artifacts/failed-retention-20260916/restored-processes.json). The per-case directories hold fresh source fixtures, sanitized SMTP transcripts, closed original pairs, sorter stdout/stderr and email logs, 30-second observations, and results. No actual-server private sample message was used for these live sends.

## Artifact identity and scope

Independent checks verified all **400 deployment files**, all **396 ZIP entries**, both ZIP hashes, and exact declared package contents. Parsed standalone bundle manifests identify rc.6 and .NET 10.0.11. The sorter contains only its own and Shared project assemblies, with no Mail/Engine dependency. These byte/content checks do not establish that Windows permits an executable to run.

| Item | Value |
|---|---|
| Candidate / SDK / runtime | `1.0.0-rc.6` / `10.0.400` / `10.0.11` |
| Host / filesystem | Windows 11 Pro `10.0.26200`, local NTFS |
| SmarterMail | `100.0.9742.26305+420ad0abfc12ec298959057b3a7aae179d13f0e1` |
| Standalone sorter SHA-256, unchanged across policy-change retest | `1a15c1c9ffc7b2b145f33851ff4a01cff0ea3ea026928fd1a5384caecfc3fe97` |
| Standalone tagger SHA-256 | `af3b801a084e19dda9bafe31dd6519fe334054f6e30fe3ae229c6e65cd7444a5` |

Evidence: [preserved rc.6 manifest](artifacts/written-readiness-20260917/prior-rc6/manifest.json), [independent artifact audit](artifacts/failed-retention-20260916/artifact-verification.json), and the [preserved rc.5 manifest](artifacts/failed-retention-20260916/prior-rc5/manifest.json). Earlier rc.6 artifacts are retained under `artifacts/failed-retention-20260916/blocked-preformat/`. The later [rc.7 readiness gate](written-readiness-2026-09-17.md) preserves this Failed-retention behavior; current build outputs are identified by their own manifest. Generated evidence and lab helpers are ignored by Git; the source tests and this report are shared. No new public release was created.

Windows Server, other SM builds, production identities/storage, public senders, and general client compatibility remain deployment gates. The earlier [rc.5 BOM/signing tests](bom-compatibility-2026-09-16.md) are separate historical evidence, not a fresh rc.6 signing run. Recheck source-status behavior, retention permissions/placement, executable launch policy, unchanged byte hashes, actual partial paths, and subsequent good-message delivery after relevant upgrades. Do not return Failed mail to normal spool as a retention procedure.
