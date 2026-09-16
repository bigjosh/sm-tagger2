# Bring-up

Bring the system up in stages: first prove unchanged delivery through the sorter with an empty `senders` directory, then start the tagger, then enroll one controlled sender. Use a short, attended maintenance window for the initial sorter trial; enable tagging for ordinary users only after the controlled sender tests pass. Record the exact release, server builds, paths, process identities, and results in [assumptions.md](assumptions.md); the complete checks are in [testing-plan.md](testing-plan.md).

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
```

For application-folder ZIPs, deploy each complete folder and change `$sorter` to `Join-Path $release 'sm-sorter\sm-sorter.exe'` and `$tagger` to `Join-Path $release 'sm-tagger\sm-tagger.exe'`.

| Location | Required preparation |
|---|---|
| `$release\` | Operator deploys both standalone EXEs or the two complete application folders; keep configuration and mail elsewhere. |
| Runtime hosting | The tested .NET 10.0.11 standalone builds ran without extracting runtime files. Verify the selected release under the intended process identity; if its runtime requires extraction, provide writable, account-protected storage as described in [release files](deployment.md#release-files). This is separate from the mail data layout. |
| `$spool\`, `$spool\proc\` | Identify and prepare the actual SmarterMail queue. Neither executable creates these directories. Both must exist before the sorter starts. |
| `$data\` | Operator selects a fresh data root for this bring-up. It and all queue, mapping, and staging paths must share the spool's ordinary local NTFS volume. |
| `$evidence\` | Operator creates a protected location for captures, the optional sorter email log, and stderr; this is outside the runtime data layout. |

Check the intended process identities can enumerate/read their inputs, create locks and working files, and perform non-replacing moves and cleanup on the selected roots. Protect mail, mappings, and diagnostics; diagnostics can contain private identities. Verify actual volume topology and permissions, including any mounted paths, under `LAB-004`.

Leave both executables stopped. Identify the actual flat `proc` directory and its publication destination; neither executable searches alternate queues or nested subspools. SmarterMail does not launch either executable, so leave its separate command-line processing hook unused. Before enabling `Enable spool proc folder`, prepare the one-shot and watcher commands, set a short observation deadline, and review [how to stop or investigate a failed stage](#stop-or-investigate-a-failed-stage). With the sorter stopped, all mail routed into this `proc` waits there, including ordinary inbound and outbound traffic. Controlled probe messages do not isolate the setting to those messages.

Use a fresh protected evidence directory for each test window so repeating the commands does not overwrite a previous window's stderr files.

## 1. Prepare sorter-only pass-through

Create the fresh data root and its empty `senders` directory:

```powershell
[System.IO.Directory]::CreateDirectory((Join-Path $data 'senders')) | Out-Null
[System.IO.Directory]::CreateDirectory($evidence) | Out-Null
Get-ChildItem -LiteralPath (Join-Path $data 'senders') -Force
```

Required layout at this stage:

```text
<spool>\                  existing SmarterMail publication destination
    proc\                 existing sorter input; sorter creates sm-sorter.lock here
<data>\
    senders\              operator-created, accessible, empty
```

The tagger stays stopped. `profiles`, `process`, `staging`, and `tag-addresses` are unnecessary for this stage. Do not copy the example sender configuration yet.

**An empty, accessible `<data>\senders` enables pass-through for every ready message the sorter can safely classify.** A valid non-enrolled authenticated message, or a safely classified no-auth message, moves unchanged from `proc` to the spool root. Unfinished SmarterMail candidates wait untouched. This is not a bypass for malformed or ambiguous completed HDRs: those are held. For an enrollable auth-address, a missing/inaccessible `senders` root is also a hold condition. A missing canonical auth-address child under the accessible root means non-enrollment.

## 2. Prove one controlled pair

With no sorter watcher running, enable the selected SmarterMail `proc` route during the attended window and submit one controlled test message. Keep the observation interval short: other queued mail waits too. A plain HDR is written first and may exist for an attempt that never produces an EML; do not use it as the readiness trigger or rename it while SmarterMail is working. Observe the final EML appearing, then read its matching same-basename HDR. Status `Failed` must be rejected; there is no `Written` whitelist. Retain protected completed original bytes and the actual basename before processing. Use the basename without `.hdr` or `.eml`:

```powershell
$basename = 'replace-with-observed-basename'
& $sorter $data -l (Join-Path $evidence 'sorter-mail.log') -v $spool $basename 2> (Join-Path $evidence 'sorter-one-shot.stderr.txt')
$sorterExit = $LASTEXITCODE
$sorterExit
```

There are no additional directory requirements: stage 1 already created the email log's parent directory. `-l` appends a terminal `PASS`, `DIVERT`, or `ERROR` line for a processed message to `sorter-mail.log`; `-v` shows debugging on the console's standard output. They are optional and can be used independently. Without `-l`, the sorter opens no email log; without `-v`, it omits debug output. Errors still go to stderr. The sorter does not create a missing log parent directory.

A successful one-shot returns `0`; the pair leaves `proc` and is published EML-first/HDR-last into `$spool`, where SmarterMail may consume it immediately. Use controlled queue observation to compare the unchanged pair bytes, and confirm delivery at the independent test receiver. Repeat for authenticated non-enrolled and no-auth paths. Establish readiness and route coverage under `LAB-001` and `LAB-002`. The email-log result describes the sorter's operation, not final delivery.

If it reports `NOT READY` and returns 1, a missing final EML or HDR sharing/lock conflict left the input untouched: there is no new `.hdr.sort`, `.sort.err`, or terminal email-log record. It does not wait in one-shot mode. Observe the producer completing the input before trying the plain pair again. The sorter checks for the EML before any HDR access, even in one-shot mode. With a final EML present, an exact case-sensitive `Failed` status after trailing spaces/tabs are removed is a real rejection: retain `.hdr.sort` and the EML, attempt `.sort.err`, report the failure to stderr, and record `UPSTREAM_FAILED` in the email log. No message files are deleted.

For a processing error, inspect stderr, `proc\<basename>.hdr.sort`, any `<basename>.sort.err`, and the EML's actual location. A publication failure can move the EML before the HDR fails. One-shot mode processes a plain pair only; it never resumes retained files. Before another live trial, manually resolve earlier retained collisions with the relevant owners stopped; the corrected readiness gate does not repair them.

## 3. Prove the sorter watcher

Keep `senders` empty and the tagger stopped. In its own console, using the stage 0 variables:

```powershell
& $sorter $data -l (Join-Path $evidence 'sorter-mail.log') -v $spool 2> (Join-Path $evidence 'sorter-watch.stderr.txt')
$LASTEXITCODE
```

The same folders suffice. The sorter creates/opens `proc\sm-sorter.lock`; do not create or delete lock files manually. Ownership comes from the open handle, so a lock filename can remain after a clean exit.

Submit fresh controlled messages while the watcher is running and verify delivery. Also verify plain backlog discovery at startup, independent console shutdown/restart, and correct behavior under the intended process host (`LAB-013`). Neither program is a native Windows service. A scheduler or wrapper must preserve arguments, capture stderr and exit status, capture stdout when using `-v`, and provide the tested Ctrl+C/Ctrl+Break shutdown behavior. Use an absolute `-l` path in a job so its working directory cannot change the log location.

Check that HDR-only arrivals remain untouched and never start processing, then final EML arrivals with valid matching HDRs leave `proc` exactly once; verify the final HDR metadata and EML bytes, retained suffixes, stderr, and delivery at the receiver. Check upstream `Failed` mail is retained and reported. The optional email log contains terminal results only; startup, shutdown, scans, readiness deferrals, and stale watcher entries appear only in `-v` debugging. A deferred input gets no email-log record until a later completed processing attempt. An email-log failure reports to stderr and disables that file for the invocation while mail processing continues. A running process alone does not prove successful processing: message-local failures leave inert files, and incomplete producer attempts can remain in `proc`. The sorter reconsiders final EML candidates within 30 seconds while otherwise idle; the tagger uses plain HDR candidates in its separate queue. A stopped sorter stops all mail through this `proc`.

## 4. Start the tagger with no enrolled senders

Add an empty `profiles` root; keep `senders` empty. Use a fresh data set with no existing mappings or queued mail for this stage:

```powershell
[System.IO.Directory]::CreateDirectory((Join-Path $data 'profiles')) | Out-Null
& $tagger $data -log $spool 2> (Join-Path $evidence 'tagger-empty.stderr.txt')
$LASTEXITCODE
```

Run the tagger in a separate console from the sorter. The minimum startup layout and generated directories are:

| Path under `<data>` | Who creates it and when |
|---|---|
| `senders\`, `profiles\` | Operator must create both. Empty roots are a valid empty configuration; absent or inaccessible roots fail startup. |
| `sm-tagger.lock` | Tagger opens/creates this in the existing data root when acquiring ownership. |
| `process\` | Tagger creates it after successful configuration and mapping validation; sorter also creates it on first diversion. Operator may create it earlier to set permissions. |
| `tag-addresses\` | Optional when genuinely absent on a fresh installation; the first allocation creates it. Existing records are validated at every tagger startup. |
| `staging\` | Optional at startup; the first allocation creates it. Existing entries remain inert and are reported. |
| `senders\.staging\` | Optional operator-created directory for preparing complete auth indexes; distinct from mapping `staging`. |
| `log.txt` | Tagger attempts to create/open it only with `-log`; failures are reported and do not stop mail processing. |

Check startup stderr and the created `process` directory, then request an orderly stop and inspect its exit status. This establishes empty-configuration startup. It does not yet establish rewriting. Do not place non-enrolled messages directly into `process`: the tagger requires a consistent enrolled auth in its startup configuration.

Missing optional roots are acceptable only as described above. Do not remove an existing mapping tree to make validation pass; permanent mappings preserve already issued addresses.

## 5. Prepare and publish one sender

Keep the tagger stopped throughout configuration changes. Keep this sender's submissions paused until configuration has loaded successfully, and do not configure the client to use its private identity yet. The sorter may continue passing unrelated mail.

First provision a private alias generated with cryptographically secure randomness, a dedicated tag domain reserved for this sender, and its catch-all collection mailbox. Establish controlled catch-all delivery, original envelope-recipient visibility, and absence of loops. Check the intended route accepts new tag addresses without individual alias registration and is configured to sign final rewritten mail. Keep client/server read receipts disabled. Record the provisioning and transport assumptions before ordinary use.

Create a fresh permanent lowercase sender UUID, then prepare this complete layout using [the configuration example](examples/README.md) for file formats. Replace every synthetic identity and the example UUID; write UTF-8 without a BOM. Both retired-address files must exist even when empty.

```text
<data>\
    profiles\<sender-id>\
        auth-address.txt                 current authenticated account address
        retired-auth-addresses.txt       empty for a new sender
        private-address.txt              provisioned non-public alias
        retired-private-addresses.txt    empty for a new sender
        from-template.txt                e.g. tag-%@reply.example.com; use approved domain
        allow-mdn.txt                    exactly false
    senders\
        .staging\<staging-id>.authtmp\
            sender-id.txt                exact same permanent sender UUID
    process\                             empty before this controlled test
```

The profile must be complete before enrollment is published. The template's first whitespace-delimited token contains exactly one `%`. Current/retired auth and private identities must be unique across all roles and profiles. No mapping directories or tag records need to be populated by hand.

After closing the index pointer file, publish the prepared directory with a same-volume, non-replacing directory move. Substitute the actual canonical lowercase auth-address and the prepared staging directory name:

```powershell
$auth = 'auth@example.com'
$stagedIndex = Join-Path $data 'senders\.staging\replace-with-staging-id.authtmp'
$publishedIndex = Join-Path (Join-Path $data 'senders') $auth
[System.IO.Directory]::Move($stagedIndex, $publishedIndex)
```

The resulting `senders\<canonical-auth-address>\sender-id.txt` points to `profiles\<sender-id>`. **Publishing that folder immediately makes the sorter divert this auth-address.** It does not update a running tagger's configuration. Current and retired indexes are permanent; do not delete them to disable tagging.

Start the tagger once with this complete profile/index set and an empty `process` queue, using the watcher command from stage 4. Confirm successful startup, then stop it orderly for the one-shot tests below. If startup fails, leave sender submissions paused and correct the configuration while the tagger is stopped. Once a sender is enrolled, a stopped tagger leaves its mail waiting in `process` while unrelated mail continues through the sorter.

## 6. Prove rewriting and enable the approved scope

No further operator-created directories are required. First allocation creates `staging\` and `tag-addresses\`; each published tag directory contains authoritative `sender-id.txt` and `recipient-id.txt`, plus a best-effort `tag-log.txt`. Keep these permanent mappings for future messages and restarts.

With the sorter watcher running and tagger watcher stopped, send one controlled message from the enrolled account using a supported ordinary identity that matches neither its current nor retired private-address. Confirm the pair arrives in `process`. Process its observed basename:

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
| MDNs | Keep `allow-mdn.txt=false` and receipt generation disabled until every capable path has the required separate evidence (`LAB-012`). Changing this flag requires a tagger restart. |

Keep the sorter watcher running and start the tagger watcher in its separate console, then repeat fresh-message delivery through the intended production host. Record queue readiness, storage, shutdown, restart, stderr capture, and the deployed executable hashes. Record only the paths actually tested as supported; leave incomplete Windows Server, SmarterMail, SMTP, signing, catch-all, and client checks `PENDING` in [assumptions.md](assumptions.md). Local fixtures do not complete those gates.

## Stop or investigate a failed stage

Pause new affected submissions first. Stop the relevant watcher with Ctrl+C/Ctrl+Break and wait for owned work to finish. Read-only monitoring can continue while mail is being processed. Before manually moving, renaming, or otherwise changing queue files, stop every relevant owner, including SmarterMail where it can consume the affected files. Forced process termination can leave partial states.

Disabling the SmarterMail `proc` route does not establish that already queued mail was drained, delivered, or safe to resend. Inventory `proc`, `process`, and the spool root. Preserve diagnostics and original HDR evidence, locate the EML, and reconcile any published children with receiver evidence before manually deciding the disposition of each message. Earlier children may already have been sent when a later publication fails.

Suffixed files (`.sort`, `.start`, `.break`, `.process`, `.pend`, `.err`, `.in`, `.out`) and staging leftovers stay inert on restart. Do not bulk-rename them to plain `.hdr`/`.eml`, replay parent mail, or remove permanent indexes/mappings as a reset. After correcting the cause, restart with the verified configuration and prove processing with a fresh controlled message. Retained mail requires a separate manual disposition; neither executable retries or repairs it automatically.
