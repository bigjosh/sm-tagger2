# Local implementation validation

The local **1.0.0-rc.10** candidate moves working mail and upstream-Failed retention into the fixed `<spooldir>\proc\sm-tagger` workspace. Sender configuration, mapping staging, permanent mappings, and the optional tagger trace remain in the datadir. The tagger now holds both the existing data lock and a separate queue lock, in that order, for its full invocation. The sorter still scans only direct Proc entries. Readiness, handoff order, retained suffixes, and shared-account policy are unchanged. See the [storage contract](storage-reference.md#runtime-directory-trees) and [offline queue migration](queue-layout-2026-09-17.md).

Locked restore, the Release build, formatting verification, and fresh folder/standalone publication passed. The build had **zero warnings and zero errors**. All **500 tests passed, zero failures and zero skips**, on 2026-09-17 (local date), including the fresh published/standalone binaries. The initial 134 focused cases and all five new queue/startup cases passed separately. New coverage verifies old data queues are untouched with no fallback, nested work is excluded from sorter discovery, and conflicting taggers are excluded through either data or spool. Real blocked-workspace/queue-lock failures release the first lock without touching trace, configuration, or message bytes. Existing seven crash-boundary tests now check both lock handles through processing and cleanup boundaries.

Independent checks matched **400 deployment files and 396 ZIP entries**. An isolated upgrade check used actual archived rc.9 and fresh rc.10 standalone programs: it moved ready queued mail, upstream failures, and inert residual files offline without byte changes, processed the ready item, and reused its existing tag. Fresh diverted and Failed mail used the new locations. Leftover old datadir queues remained ignored and unchanged; sender configuration and permanent mapping identity bytes stayed unchanged. Message bodies and HDR auth were preserved. A separate check used **C: for data and D: for mail**, independently confirmed as distinct NTFS volumes, and exercised both reuse of an existing mapping and allocation/publication of a new mapping.

These are local synthetic filesystem/executable checks with no SMTP submissions, actual-server data migration, or SmarterMail settings changes. The Proc-subtree isolation basis is the owner's accepted confirmation recorded in [assumptions.md](assumptions.md#version-sensitive-001); this run does not claim a fresh live isolation measurement or Windows Server deployment approval. Existing queues require the documented offline conversion before deployment; the executables do not migrate or replay them.

The preceding local **1.0.0-rc.9** candidate makes auth-index directories authoritative and permits multiple active accounts to share one sender-id. The redundant auth declaration and both retirement files are no longer read or required. Only the configured private address triggers tagging; former addresses receive ordinary nonmatching treatment. Shared accounts reuse the same stable recipient mappings, template, and MDN policy. See the [current storage contract](storage-reference.md#sender-configuration) and [offline conversion procedure](sender-configuration-2026-09-17.md). Every index left published is active, so former retired indexes require explicit review before cutover.

The rc.9 Release build passed with **zero warnings and zero errors**, formatting verification passed, and both distribution forms were regenerated. All **495 tests passed, zero failures and zero skips**, on 2026-09-17 (local date), including the published and standalone executable checks. The 108 focused configuration/layout/processor checks also passed. New or revised coverage proves shared individual/group tags across accounts and restart, original HDR auth preservation, three-file sender records, ignored unreadable obsolete files, index snapshot behavior, pointer validation, indexless historical records, unchanged global identity checks, and former-private pass-through across every supported sender location.

Independent package checks matched **400 deployment files and 396 ZIP entries**. An isolated check used the archived rc.8 standalone programs to create one mapping, converted the synthetic configuration offline, and ran rc.9 with three auth addresses pointing to the same sender-id. All three reused the original tag. Removing an index produced byte-for-byte sorter pass-through; deliberate re-enrollment made that account active. Mapping identity bytes and the three retained sender files stayed unchanged, and output bodies and HDR auth values were preserved. This used local synthetic queue files: no SMTP submissions, actual-server datadir changes, or new live SmarterMail/Windows Server deployment evidence.

The following earlier results retain their original version scope; rc.9 configuration rules and rc.10 queue locations supersede the corresponding earlier behavior.

The preceding local **1.0.0-rc.8** candidate reorganized permanent sender configuration under `senders/auth-addresses/<auth-address>/sender-id.txt` and `senders/sender-ids/<sender-id>/`. Both programs, CLI hints, example data, and operating instructions use these roots. The sorter still routes solely by auth-directory existence, and the tagger still validates every identity relationship. Existing datadirs require the documented [offline migration](sender-layout-2026-09-17.md); there is no runtime fallback or automatic migration.

Independent rc.8 package checks matched 400 deployment files and 396 ZIP entries. An isolated cross-version check processed a synthetic message with rc.7 and the old layout, moved the unchanged identity records to the new layout, then processed the same sender/recipient with both rc.8 programs. The new tagger reused the exact existing tag; the mapping count stayed one, all mapping identity bytes and sender records remained unchanged, and message bodies were preserved. All seven checked-in example files were also moved without changing their bytes. These are offline checks, with no actual-server datadir changes or new live delivery evidence.

The rc.8 Release build passed with **zero warnings and zero errors**, formatting verification passed, and the full suite passed **489 tests, zero failures and zero skips** on 2026-09-17 (local date). Seven added layout cases verify unavailable nested roots, rejection of old-layout fallback, empty-index sorter independence, mismatched sender pointers, and unreadable pointer isolation. All existing configuration/coherence, auth-staging, mapping, readiness, published/standalone executable, private-capture, and message-byte checks also passed using the updated fixtures.

The preceding local **1.0.0-rc.7** candidate added the approved positive `Written` readiness gate. `Writing` and unexpected or incomplete statuses remain unclaimed; unexpected statuses report to stderr on each encounter, including watch mode without verbose output. `Failed` keeps the accepted external-retention behavior. See the [readiness change and evidence](written-readiness-2026-09-17.md). Its package hashes matched all 400 deployment files and 396 ZIP entries; seven offline replays of saved synthetic SmarterMail captures passed with unchanged original and output bytes. These are saved-capture compatibility checks, not new live submissions or delivery evidence.

The rc.7 Release build completed with **zero warnings and zero errors**; formatting verification passed. The full automated suite passed **482 tests, zero failures and zero skips** on 2026-09-17 (local date). New coverage exercises 27 status-gate cases and two application-folder executable checks, while replacing the earlier non-Failed acceptance expectations. It verifies untouched Writing/unknown/incomplete states, exact comparisons, escaped diagnostics, repeated unexpected-status reporting without verbose output, diagnostic failures, fresh routing after Written, later Failed retention, and watcher continuation. Existing writer-exclusion, standalone, private-capture, and tagger tests passed in the same run.

The preceding local **1.0.0-rc.6** implementation added external retention only for confirmed `UPSTREAM_FAILED`, following the [Failed-spool consumer experiment](failed-spool-2026-09-16.md). Its final Release build had zero warnings/errors, formatting verification passed, and publication plus independent checks of 400 deployment files and 396 ZIP entries completed. After the user disabled Smart App Control, the unchanged binaries passed **455 tests, zero failures and zero skips**. The earlier full run recorded 441 passed and 14 failed, all caused by Windows Application Control blocking copied standalone sorter launches before startup. Both runs are preserved. The three local SmarterMail scenarios passed using the application-folder sorter; details and limits are in the [retention follow-up](failed-retention-2026-09-16.md).

**The local standalone launch blocker is closed, 2026-09-16.** Readback confirmed the user-disabled Smart App Control setting, and the same binaries passed all 455 tests. SHA-256 checks confirmed that both standalone binaries were unchanged, with no rebuild or code changes; the testing agent changed no security policy or certificates. The successful full retest includes standalone executable cases. Support with Smart App Control enabled is outside the verified scope, not an active blocker for this host. Package hashes verify artifact bytes, while execution permission still depends on the host policy and process identity. The three live SM scenarios remain application-folder evidence; no new live trial accompanied this policy retest. Earlier failures and rc.5 results remain separately recorded.

The preceding local **1.0.0-rc.5** candidate passed **439 tests, with zero failures and zero skips**, on **2026-09-16**. Its Release build completed with **zero warnings and zero errors**; formatting verification and self-contained publication passed. Independent artifact checks matched all 400 deployment files and 396 ZIP entries against its preserved manifest. The [fresh BOM compatibility retest](bom-compatibility-2026-09-16.md) also passed: five natural API/SMTP cases and one prepared consumer fixture produced eight verified deliveries. The historical 398-test rc.4 baseline and its artifacts remain identified separately below. [Live SmarterMail sequencing results](live-sequencing-2026-09-16.md) provide scoped Windows 11 evidence; remaining deployment tests are defined in [testing-plan.md](testing-plan.md). Earlier implementation review and fixes are recorded in [audit-2026-09-07.md](audit-2026-09-07.md).

The [broader live compatibility suite](live-smartermail-2026-09-16.md) found the rc.4 failure on BOM-prefixed immediate API input; delayed and scheduled examples succeeded. The rc.5 correction permits one exact offset-zero UTF-8 BOM as a preserved preamble, without changing HDR/body handling or the other grammar. The fresh local retest confirms the corrected immediate API cases on the recorded build. Untested deployments still require their own evidence; the historical trial and held failures remain preserved.

## Reproduce and inspect

Run [scripts/Validate.ps1](scripts/Validate.ps1). It performs a locked restore, Release build, formatting verification, self-contained folder and standalone publication, and the complete test suite. The historical rc.4 validation-script run used Windows PowerShell 5.1; the rc.5 follow-up records its individual validation commands separately. From the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate.ps1
```

The execution policy applies only to that process. The saved machine policy was not changed. See [deployment.md](deployment.md) for ordinary build and setup instructions.

Evidence from the rc.10 run:

- [500-test full results](artifacts/test-results/mail-queue/mail-queue-full.trx), [test output](artifacts/mail-queue-20260918/tests.log), [134 focused results](artifacts/test-results/proc-workspace/proc-workspace-focused.trx), and [five startup/queue results](artifacts/mail-queue-20260918/startup-boundaries-focused.trx).
- [Current release manifest](artifacts/release/manifest.json), [Release build output](artifacts/mail-queue-20260918/build.log), [locked restore](artifacts/mail-queue-20260918/restore.log), [format verification](artifacts/mail-queue-20260918/format.log), and [publication output](artifacts/mail-queue-20260918/publish.log).
- [Independent package, migration, and separate-volume audit](artifacts/mail-queue-20260918/migration-and-artifact-audit.json), [observed NTFS volume identities](artifacts/mail-queue-20260918/volume-evidence.json), [validation summary and standalone hashes](artifacts/mail-queue-20260918/validation-summary.json), and [documentation checks](artifacts/mail-queue-20260918/documentation-check.json).
- [Preserved rc.9 manifest](artifacts/mail-queue-20260918/prior-rc9/manifest.json); every archived deployment file was hash-verified before publication.

Evidence from the rc.9 run:

- [495-test full results](artifacts/test-results/auth-aliases/auth-aliases-full.trx), [test output](artifacts/auth-aliases-20260918/tests.log), and [108 focused results](artifacts/test-results/auth-alias/auth-alias-focused.trx).
- [Preserved rc.9 release manifest](artifacts/mail-queue-20260918/prior-rc9/manifest.json), [publication output](artifacts/auth-aliases-20260918/publish.log), and [format verification record](artifacts/auth-aliases-20260918/format.log).
- [Independent package and rc.8-to-rc.9 conversion audit](artifacts/auth-aliases-20260918/migration-and-artifact-audit.json), [validation summary and standalone hashes](artifacts/auth-aliases-20260918/validation-summary.json), and [preserved rc.8 manifest](artifacts/auth-aliases-20260918/prior-rc8/manifest.json).

Evidence from the rc.8 run:

- [489-test full results](artifacts/test-results/sender-layout/sender-layout-full.trx), [test output](artifacts/sender-layout-20260917/tests.log), and [format verification output](artifacts/sender-layout-20260917/format.log).
- [Preserved rc.8 release manifest](artifacts/auth-aliases-20260918/prior-rc8/manifest.json) and [independent package and cross-version migration audit](artifacts/sender-layout-20260917/migration-and-artifact-audit.json).
- [Unchanged example relocation](artifacts/sender-layout-20260917/example-relocation.json), [Release build output](artifacts/sender-layout-20260917/build.log), and [publication output](artifacts/sender-layout-20260917/publish.log).

Evidence from the completed rc.7 run:

- [482-test full results](artifacts/test-results/written-readiness/written-readiness-full.trx), [test output](artifacts/written-readiness-20260917/tests.log), [Release build output](artifacts/written-readiness-20260917/build.log), and [format verification output](artifacts/written-readiness-20260917/format.log).
- [Preserved rc.7 release manifest](artifacts/sender-layout-20260917/prior-rc7/manifest.json) and [independent package and saved-capture audit](artifacts/written-readiness-20260917/capture-and-artifact-audit.json).
- [Saved publication output](artifacts/written-readiness-20260917/publish.log).
- [Preserved rc.6 release archive record](artifacts/written-readiness-20260917/prior-release.json).

Evidence from the completed rc.6 run:

- [455-test passing retest with Smart App Control off](artifacts/test-results/failed-retention/failed-retention-smartapp-off.trx) and [policy/hash readback](artifacts/failed-retention-20260916/smartapp-retest/result.json).
- [Preserved pre-change 455-test record](artifacts/test-results/failed-retention/failed-retention-final.trx), retaining all 14 pre-start launch failures.
- [Preserved rc.6 release manifest](artifacts/written-readiness-20260917/prior-rc6/manifest.json) and [independent artifact verification](artifacts/failed-retention-20260916/artifact-verification.json).
- [Fresh live final audit](artifacts/failed-retention-20260916/final-audit.json) and [scoped result report](failed-retention-2026-09-16.md).

Evidence from the completed rc.5 run:

- [439-test result record](artifacts/test-results/bom/bom-full.trx).
- [Preserved rc.5 release file and package SHA-256 manifest](artifacts/failed-retention-20260916/prior-rc5/manifest.json).
- [Independent rc.5 artifact verification](artifacts/bom-fix-20260916/artifact-verification.json).

Preserved baseline evidence:

- [398-test baseline results](artifacts/test-results/sequencing/sequencing-full.trx), [baseline test output](artifacts/test-results/sequencing/sequencing-full-tests.txt), and [baseline format verification](artifacts/test-results/sequencing/sequencing-format.txt).
- [Validation command output](artifacts/test-results/validation-v1.0.0-rc.4.txt).
- [Preserved rc.4 release file and package SHA-256 manifest](artifacts/bom-fix-20260916/prior-rc4/manifest.json).
- [Independent artifact verification](artifacts/test-results/artifact-verification-v1.0.0-rc.4.json).
- [Isolated private-capture readiness checks](artifacts/test-results/private-readiness-summary-v1.0.0-rc.4.json); the summary contains no captured identities or message contents.
- Seven deterministic crash-boundary tests, included in the complete test results above.
- [Dependency advisory audit](artifacts/test-results/dependency-audit-2026-09-16.json).

Generated evidence and packages live under ignored `artifacts/`. The validation script recreates the packages and complete test results; independent artifact verification, advisory audits, and the isolated capture checks are supplemental local evidence. The private sample tests skip explicitly if `samples/` is absent. Release executable tests skip explicitly if the applications have not been published; the validation script publishes them first.

The publicly released baseline is **v1.0.0-rc.4**, available with its manifest from [GitHub](https://github.com/bigjosh/sm-tagger2/releases/tag/v1.0.0-rc.4). Its standalone EXEs and manifest are preserved under `artifacts/bom-fix-20260916/prior-rc4/`. The rc.5 BOM correction, rc.6 retention change, rc.7 readiness gate, rc.8 sender-layout change, rc.9 auth-index simplification, and rc.10 mail-workspace relocation are local candidates, not new public releases; regenerated paths under `artifacts/release/` must be identified by their own manifest and follow-up results. Rc.5 artifacts are preserved under `artifacts/failed-retention-20260916/prior-rc5/`, rc.6 under `artifacts/written-readiness-20260917/prior-rc6/`, rc.7 under `artifacts/sender-layout-20260917/prior-rc7/`, rc.8 under `artifacts/auth-aliases-20260918/prior-rc8/`, and rc.9 under `artifacts/mail-queue-20260918/prior-rc9/`. Raw local evidence and private messages are not published.

## Completed rc.6 retention evidence

**Owner disposition, 2026-09-16:** rc.6 failed-folder retention is accepted as the complete resolution for upstream `Failed` messages within version 1/project scope, including the observed post-250 disconnect case. No further disconnect-workaround experiment or vendor fix is required for this implementation. This closes the project issue without claiming that the upstream SmarterMail behavior is fixed or that wider deployment gates are verified. Retained mail still receives the documented manual disposition.

All 75 focused sorter tests passed. Added coverage includes 15 filesystem cases and one standalone executable case; the latter was among the 14 OS-blocked tests before the user changed Smart App Control. The unchanged-binary retest then passed all 455 cases, including standalone tests, with that policy off. Application-folder checks passed, including retention, continued processing, and restart without replay. Enabled-policy launch support remains outside the verified scope; the current host's launch issue is closed.

Fresh actual SmarterMail build 9742 trials used the published application-folder sorter, a new data root on the same local NTFS volume outside the entire spool tree, and empty `senders` without `profiles`. An immediate connection close after SMTP DATA 250 and an aborted DATA submission each produced a `Failed` pair. Each pair was retained byte-for-byte in `failed` with its diagnostic, returned one-shot 1, and produced zero receiver captures in its 30-second observation window. A subsequent normal-QUIT control had status `Written`, returned 0, and produced one correct loopback delivery in its 30-second window. These are three passed local scenarios, not general producer-route or standalone approval.

Final reconciliation found empty live queues, six retained files for the two failed inputs, unchanged hashes for 434 earlier private files and four manual originals, and unchanged SM settings, including `smtpAcceptDisconnectedClients=false`. The owned receiver was stopped, no sorter/tagger workers remained, and the original MailService and user-owned Process Monitor processes were left running. See the [final audit](artifacts/failed-retention-20260916/final-audit.json). No new public release was created, and all full deployment gates remain pending.

## Completed rc.5 automated coverage and artifacts

All 398 baseline cases below passed again. The added BOM coverage contributed 29 parser cases, 10 integration cases, and two real published-executable cases, for **439 total**. These cover the exact prefix allowance, malformed-prefix rejection, absolute edit offsets, Return-Path removal, Reply-To construction, physical-line limits, byte preservation, and unchanged retention behavior.

Fresh live checks produced six deliveries from five natural API/SMTP cases, plus two from a prepared private-queue fixture exercising leading Return-Path removal and synthesized Reply-To. All retained tagger outputs matched the exact edit/body/prefix expectations. All eight wire captures had no BOM, the expected tagged From, and one independently valid aligned DKIM signature; each wire body equaled the retained body **plus one CRLF added by SmarterMail**, not byte-for-byte equality. No extra delivery was observed in the recorded window. The prepared fixture tests the tagger and downstream consumer, not SmarterMail's producer or the sorter. [The final audit](artifacts/bom-fix-20260916/live/final-audit.json) records this separate evidence.

The rc.5 publication used Release, self-contained `win-x64`, SDK 10.0.400 and runtime 10.0.11. Artifact verification confirmed package/file hashes, exclusion of private samples and test/lab assemblies, and the sorter's assembly independence. Its standalone hashes are:

| Executable | SHA-256 |
|---|---|
| `sm-sorter.exe` | `47299827036ac68fef54e34ee0d7cd16bc927d0b4a287fdc76fc7b492fc79c3d` |
| `sm-tagger.exe` | `83fb1a2c13351f9ab21f259285d4ff8e90bec34fb7531f4270d0580540a682cc` |

## Baseline tested environment

| Item | Recorded value |
|---|---|
| Build/test host | Windows 11 x64, OS version 10.0.26200 |
| Filesystems | Local C: and D: volumes reported NTFS and healthy; isolated test trees used the local temporary directory |
| SDK | 10.0.400, pinned by `global.json` |
| Target / bundled runtime | `net10.0` / Microsoft.NETCore.App 10.0.11 |
| Publication | `1.0.0-rc.4`, Release, self-contained `win-x64`; application folders and single-file EXEs; no trimming |
| Dependency audit | `dotnet list SmTagger.slnx package --vulnerable --include-transitive --format json` reported no vulnerable package entries on this date |

Production projects use the .NET base class library. Test package versions are locked. The dependency audit is a dated advisory check, not a guarantee that future vulnerabilities will not be discovered.

## Baseline automated coverage

| Area | Passing tests | Evidence exercised |
|---|---:|---|
| Mail parsing and identities | 87 | Bounded ASCII syntax, canonicalization, folding/display tokens, HDR framing, MDN classification, recipient codec, exact preservation outside permitted edits, edited/synthesized continuation lines at 998/999 bytes |
| Configuration and permanent mappings | 71 | Required files and cross-profile uniqueness, current/retired identities, complete checked-in example, startup validation, ignored template annotations, stable mapping reuse, token collisions, atomic directory publication, fatal post-publication cache failure |
| Tagger processing and failure states | 80 | Current/retired matches in every supported sender location, fan-out and group replies, `.err` retention for From counts, real-file partial-write/close failure retention, complete stderr failure state, all-child readiness and keep-copy gates, sequential publication, cleanup, header length rejection and new mapping reuse after restart |
| Best-effort diagnostics | 24 | Missing/corrupt logs, initialization/append/encoding/flush/disposal failures, trace disablement, stderr reporting, preservation of the underlying processing result |
| Sorter diagnostics | 23 | One result per selected message, all route types, retained error state, stale watcher suppression, escaped append-only UTF-8 records, unread EML contents, and independent file/console failures without changed mail outcomes |
| Sorter input readiness | 19 | Final EML checked before HDR access, untouched provisional and orphan HDRs, final metadata replacement, upstream Failed rejection, opaque other statuses, malformed completed HDR retention, busy-HDR deferral including writers that permit readers, and unchanged message bytes |
| Sorter, invocation, and queue lifecycle | 53 | HDR-only routing, unread EML pass-through, ownership ordering, literal basenames, independent singleton locks, sorted scans, native notification arrivals, overflow/error paths, stopping after owned work |
| Published executable processes | 11 | End-to-end sorter/tagger operation, exit status and logging failures, independent watchers, live sorter log flushing, final-EML discovery with orphan HDRs and temporary EMLs, upstream Failed retention with later mail continuing, one-shot readiness deferral, singleton contention, startup-failure isolation, crash/restart, release dependency isolation |
| Standalone executable processes | 20 | Twelve argument-error cases verify concise usage hints, exit status, untouched queues/configuration, and absent locks; relocated EXEs with no adjacent dependencies, byte-exact sorter pass-through, enrolled diversion and two-recipient rewriting, body preservation, From-error retention, optional sorter log append and verbose output, sorter ERROR records, and continued processing after logging failure |
| Deterministic process-crash boundaries | 7 | Forced termination of a separate test worker at exact internal boundaries; production tagger watcher restart, exact retained file inventories/hashes, complete mapping reload/reuse, and successful fresh mail |
| Private actual-server samples | 3 | Five complete captured pairs parsed and classified; authenticated no-match and activated processing checked using temporary copies |
| **Total** | **398** | **All executed; none skipped** |

The three adversarial parser tests run **4,608 deterministic generated cases** internally: 3,072 arbitrary/mutated HDR and EML pairs, 1,024 valid generated EML messages, and 512 valid generated HDR messages. They check controlled contract failures and independently composed expected output bytes. These cases are included in the 87 parsing tests above, not counted as thousands of separate test-runner tests.

The actual-server sample tests use isolated temporary configuration and working copies. Original files stay unchanged. No captured identities, message bodies, or raw message files are included in shared fixtures, this report, or release packages. These captures establish compatibility with their observed byte forms; they do not establish the current server's deployment contract.

The executable crash test forcibly terminates the published tagger during construction of a 1,500-recipient fan-out after a child becomes pending. Restart reloads complete mappings, preserves the retained work without automatic replay, and processes a fresh message. Other tests verify that logging failures still allow the same current child to publish, while real contract or filesystem errors retain the documented evidence.

Seven additional crash tests terminate a separate test-only worker after HDR ownership, before mapping publication, immediately after mapping publication, after first-child readiness, between child EML/HDR publication, after all child HDRs are live but before parent cleanup, and between the two From-contract retention renames. Each restarts the unmodified production tagger assembly in watch mode. Tests check startup mapping-load events, unchanged retained bytes and locations, no automatic mail replay, and fresh delivery using the same recipient mappings. Internal observers and the child-stream failure seam have no selector in production arguments, environment, configuration, or message data; the worker and lab helper are excluded from release packages.

## Published rc.4 baseline packages

- [Standalone sorter EXE](https://github.com/bigjosh/sm-tagger2/releases/download/v1.0.0-rc.4/sm-sorter.exe)
- [Standalone tagger EXE](https://github.com/bigjosh/sm-tagger2/releases/download/v1.0.0-rc.4/sm-tagger.exe)
- [sm-sorter Windows x64 ZIP](https://github.com/bigjosh/sm-tagger2/releases/download/v1.0.0-rc.4/sm-sorter-win-x64.zip)
- [sm-tagger Windows x64 ZIP](https://github.com/bigjosh/sm-tagger2/releases/download/v1.0.0-rc.4/sm-tagger-win-x64.zip)

Both forms include their .NET runtime. The standalone EXEs need no adjacent DLLs; tests relocate only the EXEs before launching them with runtime-root overrides removed. For ZIP deployment, copy each complete application folder. Runtime license and third-party notices accompany both distribution forms. See [deployment.md](deployment.md#release-files) for hosting details.

The standalone tests also capture .NET host diagnostics: these Windows x64 .NET 10.0.11 bundles report self-contained execution with CoreCLR embedded in the native host, and leave their fresh runtime-extraction directories empty. The tests accept either that embedded runtime or extraction into the controlled directory when a future runtime pack requires it. Extraction behavior and the actual process identity remain part of the deployment hosting check.

The [release manifest](https://github.com/bigjosh/sm-tagger2/releases/download/v1.0.0-rc.4/manifest.json) is the authoritative SHA-256 inventory for the EXEs, runtime notices, complete application folders, and ZIPs.

Independent verification matched all **400 deployment files** and all **396 archived file entries** against the manifest and checked both package hashes. The inventory includes four application-folder runtime notice files and the two standalone EXEs with their shared notices. Release checks exclude sample messages, test assemblies, the crash worker, and lab helpers. The sorter carries no tagger-only mail or engine assemblies. The publish script recreates its generated output directories before building.

The exact production executable folders and standalone EXEs tested are the ones represented by this manifest. Republishing creates a new manifest and may change package hashes; record the new hashes when selecting deployment artifacts.

A live sorter trial exposed premature ownership of a provisional HDR before its final EML arrived. The corrected sorter discovers final EML files, reads the matching HDR, rejects upstream Failed status, and preserves EML-first/HDR-last publication. Regression tests reproduce the provisional-HDR and final-EML sequence with synthetic data. Additional isolated checks using private capture copies confirmed that an early HDR remains unopened/unclaimed without its EML, and that a copy deliberately marked Failed is retained and logged once. Hash verification confirmed all four originals unchanged. The later captured HDR has incomplete envelope-routing fields and was not replayed; these local checks do not establish recovery or delivery safety for that failed transaction.

## Remaining deployment evidence

Every full deployment `VERSION-SENSITIVE-*` gate in [assumptions.md](assumptions.md) remains **PENDING**. The [sequencing experiment](live-sequencing-2026-09-16.md) and [broader compatibility suite](live-smartermail-2026-09-16.md) provide scoped Windows 11 evidence, including the preserved rc.4 immediate-API failure. Successful local verification of its rc.5 correction is recorded separately in the [follow-up](bom-compatibility-2026-09-16.md). All fourteen identifiers are also present beside dependent source behavior. The automated validation commands do not connect to or modify the production mail queue, DNS configuration, accounts, or server.

Baseline local live evidence covers enrolled SMTP rewriting and fan-out, 45 concurrent-watcher inputs producing 75 deliveries, stable existing mappings, cryptographically verified real SM DKIM on loopback captures, four admission-rejection probes, and three additional catch-all mailbox deliveries. The catch-all export preserved original X-Rcpt-To and exact bodies while the 14 mappings stayed unchanged; those mailbox deliveries are outside the outgoing SMTP/signature denominator. Those results and the eight separate rc.5 follow-up deliveries do not establish the complete deployment conditions. Outstanding work includes the selected Windows Server and SmarterMail environment, untested queue/submission routes, external SPF/DMARC/provider results, a dedicated tag-domain deployment and external catch-all feedback, supported clients and MDNs, exact accepted size/recipient boundaries and capacity, and the chosen process identity and hosting arrangement. Record exact product builds, configuration, and artifact hashes when those checks run.

Native filesystem notifications and real Windows sharing/move behavior were exercised locally. Watcher overflow/error and periodic-rescan paths were also exercised through narrow internal test seams; this does not claim a reproducible native buffer overflow on the deployment server. Orderly shutdown was tested at the queue boundary, but actual Ctrl+C/Ctrl+Break delivery from the chosen service wrapper or scheduler remains a deployment check. No disk exhaustion, machine reboot, or power-loss experiment was performed. Power-loss durability is outside the accepted version 1 contract.

The final-EML correction passed the baseline checks above and the scoped live sequencing checks. The [EML preamble correction](bom-compatibility-2026-09-16.md) passed automated and fresh scoped local live verification. Repeat the relevant checks on the intended deployment before production enablement, including any producer paths and signing/relay behavior absent from the local experiments.
