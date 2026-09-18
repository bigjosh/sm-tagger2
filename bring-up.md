# Bring-up

Bring the system up in stages: first prove unchanged delivery through the sorter with an empty `senders\auth-addresses` directory, then start the tagger, then enroll one controlled sender. Use a short, attended maintenance window for the initial sorter trial; enable tagging for ordinary users only after the controlled sender tests pass. Record the exact release, server builds, paths, process identities, and results in [assumptions.md](assumptions.md); the complete checks are in [testing-plan.md](testing-plan.md).

Keep the authoritative [storage reference](storage-reference.md) alongside this workflow for directory requirements, file contents, and state suffixes. [spec.md](spec.md) remains the behavioral authority; the commands below apply those definitions to each bring-up stage.

These commands use the rc.10 mail workspace and rc.9 sender configuration. Before using an existing installation, complete the [offline queue migration and rollback procedure](queue-layout-2026-09-17.md), including earlier configuration conversions as needed, with both programs stopped and upstream processing paused. Do not leave mail in an old datadir queue or former retired indexes unreviewed: there is no fallback, and every published index is active. The steps below otherwise assume fresh storage.

**Immediate API compatibility check:** the [original live suite](live-smartermail-2026-09-16.md) found that SmarterMail build 9742 can put `EF BB BF` before the first Proc EML field, causing the rc.4 tagger to report `Invalid EML field name`. The local 1.0.0-rc.5 correction passed 439 automated tests and the [scoped local live retest](bom-compatibility-2026-09-16.md), including fresh immediate API messages. The published rc.4 executables do not contain this correction. Verify the selected executable and intended routes on the deployment machine; sorter pass-through success alone does not establish tagger compatibility. Check actual Reply-To presence because an empty API value may cause SmarterMail to insert one. Also check the actual From display name: the tagger preserves it, including any private alias text a client or server inserts there.

After SmarterMail or tagger upgrades, retain a fresh controlled input and tagged output and inspect the final received From and DKIM. Confirm the allowed prefix is preserved once in our output and handled correctly by SmarterMail. If field-name errors or first-header changes recur, inspect exact initial bytes only when the relevant files are quiescent, record the route/build, and follow `VERSION-SENSITIVE-005` in [assumptions.md](assumptions.md). Do not strip unfamiliar prefixes or replay retained failures to make a test pass.

## 0. Select the installation and queue roots

Download the self-contained Windows x64 `sm-sorter.exe` and `sm-tagger.exe` from [GitHub Releases](https://github.com/bigjosh/sm-tagger2/releases), and place them together in your executable directory. Verify the release manifest hashes and retain the supplied runtime license/notices. These standalone EXEs need no companion DLLs, installed .NET runtime, or SDK on the mail server. Complete application-folder ZIPs are an alternative; see [release files](deployment.md#release-files).

Run the commands below in PowerShell on the SmarterMail server, under the intended process account. The following paths are examples, not discovered installation paths. Replace them with the approved paths for this server, and set the same variables in every console and scheduled job:

```powershell
$release = 'D:\sm-tagger-bin'
$data = 'D:\sm-tagger-data'
$spool = 'D:\SmarterMail\Spool'
$evidence = 'D:\sm-tagger-evidence'
$sorter = Join-Path $release 'sm-sorter.exe'
$tagger = Join-Path $release 'sm-tagger.exe'
$proc = Join-Path $spool 'proc'
$mailroot = Join-Path $proc 'sm-tagger'
```

For application-folder ZIPs, deploy each complete folder and change `$sorter` to `Join-Path $release 'sm-sorter\sm-sorter.exe'` and `$tagger` to `Join-Path $release 'sm-tagger\sm-tagger.exe'`.

Check the selected executable actually starts under the intended account before enabling Proc flow. On the local rc.6 host, Windows initially blocked the standalone sorter before startup. After the user disabled Smart App Control, the unchanged binaries passed all 455 automated tests; compatibility with that policy enabled remains unverified. The three live SM scenarios used the complete application-folder distribution and were not repeated during this policy retest. See [validation status](failed-retention-2026-09-16.md). Verify the distribution under the intended deployment policy and identity; no security-policy or certificate changes were made by the testing agent.

Identify the existing SM spool/Proc roots and select a separate fresh data root. Use the [runtime trees](storage-reference.md#runtime-directory-trees) and [required-directory checklist](storage-reference.md#required-directories-and-creation): mail stays on the spool's ordinary local NTFS volume without junctions; the data root may use another NTFS volume, with mapping staging and final mappings together. Keep executable and evidence locations separate. The owner-confirmed isolation of the Proc subtree is accepted for rc.10; this guide does not claim a new live isolation test. Verify runtime hosting under the intended identity as described in [deployment.md](deployment.md#release-files).

Check the intended process identities can enumerate/read their inputs, create locks and working files, and perform non-replacing moves and cleanup on the selected roots. Protect mail, mappings, and diagnostics; diagnostics can contain private identities. Verify actual volume topology and permissions, including any mounted paths, under `LAB-004`.

Leave both executables stopped. Identify the actual flat `proc` directory and its publication destination; neither executable searches alternate queues or nested subspools. SmarterMail does not launch either executable, so leave its separate command-line processing hook unused. Before enabling `Enable spool proc folder`, prepare the one-shot and watcher commands, set a short observation deadline, and review [how to stop or investigate a failed stage](#stop-or-investigate-a-failed-stage). With the sorter stopped, all mail routed into this `proc` waits there, including ordinary inbound and outbound traffic. Controlled probe messages do not isolate the setting to those messages.

Use a fresh protected evidence directory for each test window so repeating the commands does not overwrite a previous window's stderr files.

[Local build 9742 sequencing tests](live-sequencing-2026-09-16.md) observed different SMTP and direct immediate API (`sendImmediately: true`) file-creation orders. Ordinary browser Send and delayed-send publication still require separate sequencing evidence. The rc.7 change, retained in subsequent candidates, added the positive `Written` status gate described in the [contract note](written-readiness-2026-09-17.md); historical rc.6 tests do not by themselves verify it. Keep final-EML discovery, read-only HDR writer exclusion, status deferral, `Failed` retention, and EML-first/HDR-last publication together. While a watcher is active, use logs, directory attributes, and downstream captures to observe it. Opening or copying active queue mail can hold a Windows handle that blocks its move; take content snapshots only after the relevant producer and processor are quiescent. The live-test monitor encountered this interference, so it is a tested operational concern.

## 1. Prepare sorter-only pass-through

Create the fresh data root and its empty `senders\auth-addresses` directory:

```powershell
[System.IO.Directory]::CreateDirectory((Join-Path $data 'senders\auth-addresses')) | Out-Null
[System.IO.Directory]::CreateDirectory($evidence) | Out-Null
Get-ChildItem -LiteralPath (Join-Path $data 'senders\auth-addresses') -Force
```

This prepares the minimum sorter-only data layout; compare the selected spool and data roots with the [required-directory checklist](storage-reference.md#required-directories-and-creation).

The tagger stays stopped. `senders\sender-ids`, `process`, `staging`, and `tag-addresses` are unnecessary for this stage. Do not copy the example sender configuration yet.

**An empty, accessible `<data>\senders\auth-addresses` enables pass-through for every ready message the sorter can safely classify.** A valid non-enrolled authenticated message, or a safely classified no-auth message, moves unchanged from `proc` to the spool root. Unfinished SmarterMail candidates wait untouched. This is not a bypass for malformed or ambiguous completed HDRs: those are held. For an enrollable auth-address, a missing/inaccessible `senders\auth-addresses` root is also a hold condition. A missing canonical auth-address child under the accessible root means non-enrollment.

## 2. Prove one controlled pair

With no sorter watcher running, enable the selected SmarterMail `proc` route during the attended window and submit one controlled test message. Keep the observation interval short: other queued mail waits too. A plain HDR can appear before the EML and may exist for an attempt that never produces one; do not use it as the readiness trigger or rename it while SmarterMail is working. Observe final EML, then wait for its matching same-basename HDR to be present and readable without an active writer. The tested direct API route with `sendImmediately: true` created that HDR after EML. Only `Written`, interpreted under the [HDR format](storage-reference.md#message-files), permits normal processing; `Failed` takes the retention path. Preserve protected completed original bytes and the actual basename before processing. Use the basename without `.hdr` or `.eml`:

```powershell
$basename = 'replace-with-observed-basename'
& $sorter $data -l (Join-Path $evidence 'sorter-mail.log') -v $spool $basename 2> (Join-Path $evidence 'sorter-one-shot.stderr.txt')
$sorterExit = $LASTEXITCODE
$sorterExit
```

There are no additional directory requirements: stage 1 already created the email log's parent directory. `-l` appends a terminal `PASS`, `DIVERT`, or `ERROR` line for a processed message to `sorter-mail.log`; `-v` shows debugging on the console's standard output. They are optional and can be used independently. Without `-l`, the sorter opens no email log; without `-v`, it omits debug output. Errors still go to stderr. The sorter does not create a missing log parent directory.

A successful one-shot returns `0`; the pair leaves `proc` and is published EML-first/HDR-last into `$spool`, where SmarterMail may consume it immediately. Use controlled queue observation to compare the unchanged pair bytes, and confirm delivery at the independent test receiver. Repeat for authenticated non-enrolled and no-auth paths. Establish readiness and route coverage under `LAB-001` and `LAB-002`. The email-log result describes the sorter's operation, not final delivery.

If it reports `NOT READY` and returns 1, the input was deferred untouched: there is no new `.hdr.sort`, `.sort.err`, or terminal email-log record. Causes include missing final EML, HDR sharing/lock conflict, `Writing` (`HDR_WRITING`), or any other/incomplete status (`HDR_STATUS_UNEXPECTED`). Unexpected or incomplete statuses also print stderr on every encounter regardless of flags. Consult the [HDR format](storage-reference.md#message-files) and investigate them manually; do not change status bytes to force processing. One-shot does not wait. The sorter checks EML before HDR and closes its read-only HDR handle before claiming; status deferrals perform no auth lookup or EML-content read. With final EML, exact `Failed` retains the existing behavior: claim `.hdr.sort`, create `$mailroot\failed` if needed, and move EML there before the still-suffixed HDR. This requires no sender-id records. The sorter attempts `.sort.err` beside the current HDR, reports stderr, and attempts one `ERROR reason=UPSTREAM_FAILED` in the optional email log. Even complete retention returns 1; no message files are deleted or released to the spool. See the [retention follow-up](failed-retention-2026-09-16.md) for historical rc.6 evidence.

For a processing error, inspect stderr and the recorded current paths in `proc` and `$mailroot\failed`. The best-effort `<basename>.sort.err` is beside the current `.hdr.sort`. A failed upstream-retention directory creation or first move leaves the owned pair in Proc; a failed second move leaves EML in `failed` and HDR in Proc. The diagnostic includes the failed target and I/O error. Other sorter holds stay in Proc, and publication failure can likewise move EML before HDR fails. One-shot mode processes a plain pair only; it never resumes retained files. Before another live trial, manually resolve earlier retained collisions with the relevant owners stopped; upgrading does not migrate or repair old residuals.

## 3. Prove the sorter watcher

Keep `senders\auth-addresses` empty and the tagger stopped. In its own console, using the stage 0 variables:

```powershell
& $sorter $data -l (Join-Path $evidence 'sorter-mail.log') -v $spool 2> (Join-Path $evidence 'sorter-watch.stderr.txt')
$LASTEXITCODE
```

The same folders suffice. The sorter creates/opens `proc\sm-sorter.lock`; do not create or delete lock files manually. Ownership comes from the open handle, so a lock filename can remain after a clean exit.

Submit fresh controlled messages while the watcher is running and verify delivery. Also verify plain backlog discovery at startup, independent console shutdown/restart, and correct behavior under the intended process host (`LAB-013`). Neither program is a native Windows service. A scheduler or wrapper must preserve arguments, capture stderr and exit status, capture stdout when using `-v`, and provide the tested Ctrl+C/Ctrl+Break shutdown behavior. Use an absolute `-l` path in a job so its working directory cannot change the log location.

Check that HDR-only arrivals remain untouched and never start processing, then final EML arrivals with valid `Written` HDRs leave `proc` exactly once; verify metadata, EML bytes, retained suffixes, stderr, and delivery at the receiver. Check upstream `Failed` mail is retained and reported. `Writing` must defer without mail changes or watch-mode stderr; unexpected/incomplete statuses must defer and report stderr every time, even without `-v`. These reports are deliberately not deduplicated, and unresolved inputs may remain indefinitely. The optional email log contains terminal results only. With `-v`, debugging also reports startup, shutdown, scans, deferrals, and stale entries. A deferred input gets no email-log record until a later completed processing attempt. An email-log failure reports to stderr and disables that file for the invocation while mail processing continues. A running process alone does not prove successful processing: message-local failures leave inert files, and incomplete producer attempts can remain in `proc`. The sorter reconsiders final EML candidates within 30 seconds while otherwise idle; the tagger uses plain HDR candidates in its separate queue. A stopped sorter stops all mail through this `proc`.

## 4. Start the tagger with no enrolled senders

Add an empty `senders\sender-ids` root; keep `senders\auth-addresses` empty. Use a fresh data set with no existing mappings and confirm the selected mailroot's process queue is empty. A fresh datadir does not select or isolate a different mail queue; spooldir selects it. Never point this empty configuration at an existing enrolled backlog. For this stage:

```powershell
[System.IO.Directory]::CreateDirectory((Join-Path $data 'senders\sender-ids')) | Out-Null
& $tagger $data -log $spool 2> (Join-Path $evidence 'tagger-empty.stderr.txt')
$LASTEXITCODE
```

Run the tagger in a separate console from the sorter. Confirm its required roots using [required directories and creation](storage-reference.md#required-directories-and-creation); that reference also identifies generated queues, staging, locks, and logs.

Check startup stderr, both held tagger locks, and the created `$mailroot\process` directory, then request an orderly stop and inspect its exit status. This establishes empty-configuration startup. It does not yet establish rewriting. Do not place non-enrolled messages directly into `process`: the tagger requires a consistent enrolled auth in its startup configuration.

Missing optional roots are acceptable only as specified in the reference. Do not remove an existing mapping tree to make validation pass; permanent mappings preserve already issued addresses.

## 5. Prepare and publish one sender

Keep the tagger stopped throughout configuration changes. Keep this sender's submissions paused until configuration has loaded successfully, and do not configure the client to use its private identity yet. The sorter may continue passing unrelated mail.

First provision a private alias generated with cryptographically secure randomness, a dedicated tag domain reserved for this sender, and its catch-all collection mailbox. Establish controlled catch-all delivery, original envelope-recipient visibility, and absence of loops. Check the intended route accepts new tag addresses without individual alias registration and is configured to sign final rewritten mail. Keep client/server read receipts disabled. Record the provisioning and transport assumptions before ordinary use.

Create a fresh permanent sender UUID and prepare its complete record and staged auth index using the authoritative [sender-configuration files and formats](storage-reference.md#sender-configuration). [The configuration example](examples/README.md) supplies synthetic contents to adapt; replace every example identity and UUID. Keep MDNs disabled for this initial trial and leave the process queue empty.

Check record completeness, identity uniqueness, and the template against that reference before publishing enrollment. No mapping directories or tag records need to be populated by hand.

After closing the index pointer file, publish the prepared directory with a same-volume, non-replacing directory move. Substitute the actual canonical lowercase auth-address and the prepared staging directory name:

```powershell
$auth = 'auth@example.com'
$stagedIndex = Join-Path $data 'senders\auth-addresses\.staging\replace-with-staging-id.authtmp'
$publishedIndex = Join-Path (Join-Path $data 'senders\auth-addresses') $auth
[System.IO.Directory]::Move($stagedIndex, $publishedIndex)
```

The resulting `senders\auth-addresses\<canonical-auth-address>\sender-id.txt` points to `senders\sender-ids\<sender-id>`. **Publishing that folder immediately makes the sorter divert this auth-address.** It does not update a running tagger's configuration. To enroll another account with the same private identity, policies, and tags, publish another complete auth index pointing to the same sender-id, then restart the tagger before using it. Prove identity authorization and all route/MDN prerequisites for that account too; a shared `allow-mdn=true` is not approval for an untested account.

To unenroll an account later, pause its submissions, stop the tagger, reconcile already queued mail, remove its index under the offline update procedure, and restart. Subsequent sorter lookups pass that auth as unenrolled; this does not block sending. An already queued auth absent from the new tagger snapshot fails closed. Keep the sender-id record and permanent mappings even if no indexes remain. Replacing the private-address also requires coordinating all clients: the former value becomes an ordinary non-match and may pass unchanged.

Start the tagger once with this complete sender-id record/index set and an empty `process` queue, using the watcher command from stage 4. Confirm successful startup, then stop it orderly for the one-shot tests below. If startup fails, leave sender submissions paused and correct the configuration while the tagger is stopped. Once a sender is enrolled, a stopped tagger leaves its mail waiting in `process` while unrelated mail continues through the sorter.

## 6. Prove rewriting and enable the approved scope

No further operator-created directories are required; allocation creates the mapping storage defined in the [runtime trees](storage-reference.md#runtime-directory-trees). Keep those permanent mappings for future messages and restarts.

With the sorter watcher running and tagger watcher stopped, send one controlled message from the enrolled account using a supported ordinary identity that does not match its configured private-address. Confirm the pair arrives in `process`. Process its observed basename:

```powershell
$basename = 'replace-with-observed-process-basename'
& $tagger $data -log -keep $spool $basename 2> (Join-Path $evidence ($basename + '.tagger.stderr.txt'))
$taggerExit = $LASTEXITCODE
$taggerExit
```

Confirm unchanged pass-through, exit `0`, and no allocation. Then configure only the controlled test client to use the provisioned private identity and repeat with a fresh message/basename. Inspect retained `.in`/`.out` evidence and the receiver's final message. `-keep` copies accumulate in `process`; plan protected evidence retention. A one-shot cannot run alongside the same role's watcher.

Before enabling ordinary use, complete the applicable checks for this sender and every enabled client/route:

| Check | Required evidence |
|---|---|
| Single recipient and repeat send | Supported private identity occurrences are replaced; other bytes remain unchanged; the same recipient reuses its tag after restart. |
| Multiple recipients | One output per canonical recipient, correct individual tags, group `Reply-To` behavior, and recipient-associated notifications (`LAB-005`, `LAB-006`). |
| Final authentication and new tags | Independent receiver reports `dkim=pass` and `dmarc=pass`, with final `From` covered by DKIM whose `d=` exactly matches its domain; verify SPF for changed envelope domains and fresh individual/group tags accepted without per-tag provisioning (`LAB-008`, `LAB-009`). |
| Replies, DSNs, catch-all | Delivery to the intended mailbox, inspectable original envelope recipient, no loop, and exact manual lookup in `tag-addresses` (`LAB-010`). |
| Clients and limits | Supported compose/reply/forward behavior, content outside the rewrite surface, effective admission limits, capacity, and basename uniqueness (`LAB-003`, `LAB-007`, `LAB-011`, `LAB-014`). |
| Shared accounts and MDNs | Repeat identity and route checks for every auth account sharing the sender-id; verify the same recipient reuses its tag across accounts. Keep `allow-mdn.txt=false` and receipt generation disabled until every capable account/path has the required separate evidence (`LAB-012`). Adding an account requires extending that evidence; changing the flag requires a tagger restart. |

Keep the sorter watcher running and start the tagger watcher in its separate console, then repeat fresh-message delivery through the intended production host. Record queue readiness, storage, shutdown, restart, stderr capture, and the deployed executable hashes. Record only the paths actually tested as supported; leave incomplete Windows Server, SmarterMail, SMTP, signing, catch-all, and client checks `PENDING` in [assumptions.md](assumptions.md). Local fixtures do not complete those gates.

## Stop or investigate a failed stage

Pause new affected submissions first. Stop the relevant watcher with Ctrl+C/Ctrl+Break and wait for owned work to finish. Read-only monitoring can continue while mail is being processed. Before manually moving, renaming, or otherwise changing queue files, stop every relevant owner, including SmarterMail where it can consume the affected files. Forced process termination can leave partial states.

Disabling the SmarterMail `proc` route does not establish that already queued mail was drained, delivered, or safe to resend. Inventory `proc`, `process`, `$mailroot\failed`, and the spool root. Preserve diagnostics and original HDR evidence, locate the EML, and reconcile any published children with receiver evidence before manually deciding the disposition of each message. Earlier children may already have been sent when a later publication fails. The failed directory requires manual inspection and cleanup; it has no watcher or automatic residual scan.

Use the [message-state suffix reference](storage-reference.md#message-state-suffixes) and [diagnostic reference](storage-reference.md#logs-and-diagnostics) to interpret retained files. They and staging leftovers stay inert on restart. Do not bulk-rename them to plain `.hdr`/`.eml`, replay parent mail, or remove enrollment indexes or permanent mappings as a troubleshooting reset. Deliberate account unenrollment follows the coordinated procedure above. After correcting the cause, restart with the verified configuration and prove processing with a fresh controlled message. Retained mail requires a separate manual disposition; neither executable retries or repairs it automatically.
