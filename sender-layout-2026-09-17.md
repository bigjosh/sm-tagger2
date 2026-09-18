# Sender-directory layout — rc.8

Current rc.10 additionally requires [offline queue relocation](queue-layout-2026-09-17.md). When upgrading from an earlier version, perform the relevant directory/configuration conversions below while stopped, then complete that relocation before starting current binaries. The original rc.8 evidence and paths below remain historical.

**Historical procedure: rc.7 to rc.8 only.** The path moves and evidence below retain their original scope. Current rc.9 additionally removes former auth/retirement metadata and treats every published index as active; follow [rc.9 configuration conversion](sender-configuration-2026-09-17.md) before running current binaries. When starting from rc.7 or earlier, perform the directory moves below offline, then complete the rc.9 review without starting the intermediate configuration. Instructions below to preserve retired indexes and start rc.8 are not the final rc.9 cutover procedure.

The current directory trees, record formats, and program ownership are defined in [storage-reference.md](storage-reference.md). This document describes only the offline move from the earlier layout and its rollback.

Local candidate **1.0.0-rc.8** groups sender configuration under two directories. The change affects paths only; stable sender IDs, canonical auth addresses, record contents, retirement rules, tag mappings, and message handling are unchanged.

| Record | Through rc.7 | From rc.8 |
|---|---|---|
| Permanent current/retired auth index | `<data>\senders\<canonical-auth-address>\sender-id.txt` | `<data>\senders\auth-addresses\<canonical-auth-address>\sender-id.txt` |
| Stable sender-id record and its configuration files | `<data>\profiles\<sender-id>\...` | `<data>\senders\sender-ids\<sender-id>\...` |
| Inert auth-index staging | `<data>\senders\.staging\...` | `<data>\senders\auth-addresses\.staging\...` |
| Tag-mapping staging | `<data>\staging\...` | Unchanged |

Sorter-only pass-through needs an empty, accessible `senders\auth-addresses` root and does not need `senders\sender-ids`. Tagger startup requires both nested roots, even when empty. Each canonical auth directory still points to exactly the same permanent sender-id; all retired auth indexes remain present. The internal `SenderProfile` type continues to represent a sender-id record.

Neither executable detects or migrates an earlier layout, reads both layouts, or falls back to earlier paths. Do not create empty new roots over existing enrollment: that would hide old indexes from the sorter. Fresh installations should use [bring-up.md](bring-up.md) and [the configuration example](examples/README.md). Existing installations must complete the offline procedure below before either new binary uses their datadir.

## Before moving anything

1. Pause affected submissions, SmarterMail processing, and administrative updates. Stop both sorter and tagger orderly and verify they have exited. Keep upstream processing paused for the entire migration. Do not rely on merely disabling Proc routing to drain or freeze existing work.
2. Record the resolved datadir, spool/Proc roots, process identities, launch arguments, and hashes of both deployed binaries. Verify the data root and all relevant paths are on the required ordinary local NTFS volume, with the intended permissions. Inventory live queues and retained work. Do not rename or replay message-state files during this layout change.
3. Copy the **complete datadir** to a new protected backup location outside the working trees while all writers remain stopped. Preserve and verify its complete file inventory, bytes, and required permissions. Include every current and retired auth index, sender-id record, retired-identity file, `tag-addresses`, mapping `staging`, auth `.staging`, `process`, `failed`, logs, and retained artifacts. Proc and normal spool are outside this backup: record and preserve their state separately. Keep the prior binaries and their hashes with the recovery record.
4. Inspect the source trees and the verified backup. Confirm the prior layout is the one in the table, that no destination or temporary directory already exists, and that all index pointers/records agree. Stop on a mixed or partially migrated layout, unexpected collision, or uncertain backup; do not merge, overwrite, delete, or guess which copy is authoritative.

## Move the existing trees offline

The following PowerShell example performs only non-replacing directory moves. Run it only after the preceding preparation, with the actual resolved datadir substituted. It parks the old index root at the concrete temporary path `<data>\senders-before-rc8`, creates the new `senders` container, and moves the complete old index root—including `.staging`—inside it. It then moves the complete old sender-id record root.

An earlier sorter-only installation may have no `profiles` directory. The example permits that only when there are no permanent auth-index directories; it then leaves `senders\sender-ids` absent. Create that empty root later before starting a tagger, following bring-up stage 4. It does not invent missing sender records for an enrolled installation.

```powershell
$data = (Resolve-Path -LiteralPath 'D:\sm-tagger-data' -ErrorAction Stop).ProviderPath
if (-not [System.IO.Directory]::Exists($data)) { throw 'The data root must be an existing directory.' }
$oldAuthRoot = Join-Path $data 'senders'
$oldRecordRoot = Join-Path $data 'profiles'
$parkedAuthRoot = Join-Path $data 'senders-before-rc8'
$newAuthRoot = Join-Path $oldAuthRoot 'auth-addresses'
$newRecordRoot = Join-Path $oldAuthRoot 'sender-ids'

# Verify every move path is inside the explicitly selected data root.
$dataPrefix = [System.IO.Path]::GetFullPath($data).TrimEnd('\') + '\'
foreach ($movePath in @($oldAuthRoot, $oldRecordRoot, $parkedAuthRoot, $newAuthRoot, $newRecordRoot)) {
    $resolvedMovePath = [System.IO.Path]::GetFullPath($movePath)
    if (-not $resolvedMovePath.StartsWith($dataPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the selected data root: $resolvedMovePath"
    }
}
if (-not [System.IO.Directory]::Exists($oldAuthRoot)) { throw 'The prior senders root is missing or not a directory.' }
foreach ($newPath in @($parkedAuthRoot, $newAuthRoot, $newRecordRoot)) {
    if (Test-Path -LiteralPath $newPath) { throw "Destination already exists: $newPath" }
}
$hasOldRecords = [System.IO.Directory]::Exists($oldRecordRoot)
if ((Test-Path -LiteralPath $oldRecordRoot) -and -not $hasOldRecords) {
    throw 'The prior profiles path exists but is not a directory.'
}
if (-not $hasOldRecords) {
    $permanentIndexes = @(Get-ChildItem -LiteralPath $oldAuthRoot -Directory -Force -ErrorAction Stop |
        Where-Object Name -ne '.staging')
    if ($permanentIndexes.Count -ne 0) { throw 'Auth indexes exist without the prior sender-id record root.' }
}

try {
    [System.IO.Directory]::Move($oldAuthRoot, $parkedAuthRoot)
    [System.IO.Directory]::CreateDirectory($oldAuthRoot) | Out-Null
    [System.IO.Directory]::Move($parkedAuthRoot, $newAuthRoot)
    if ($hasOldRecords) { [System.IO.Directory]::Move($oldRecordRoot, $newRecordRoot) }
} catch {
    throw "Migration stopped; preserve the partial state and backup: $($_.Exception.Message)"
}
```

The fixed path components stay within the selected root; the trusted ordinary-directory/NTFS placement condition still applies. These are operator migration checks, not new runtime preflight. If any operation fails, stop with both programs and upstream processing still stopped. Preserve the actual partial state and backup. Do not rerun blindly, delete a collision, or automatically reverse completed moves. The parked tree or partially constructed destination is evidence to reconcile.

Compare the resulting file inventory and byte hashes with the backup using the path mapping above. Confirm every stable UUID, index pointer, current/retired address file, template, MDN flag, and mapping is unchanged. Verify `.staging` moved with the auth root while top-level mapping `staging`, tag records, mail, and logs stayed at their existing paths. Inspect permissions at the new roots. Nothing should remain at the old permanent paths or temporary parking path after a complete migration; if it does, investigate without deleting it.

Deploy **both rc.8 binaries together**, using the chosen complete distribution and verified hashes, and update all scheduled/wrapper/manual launch paths together. Do not run an older sorter against the nested layout or a new sorter against the earlier layout. Before restarting, retain the migration inventory and a post-move checkpoint. Starting a watcher can immediately process an existing backlog; it is not a read-only configuration check. Start the matching tagger/sorter under the approved operating procedure, then resume upstream processing and verify fresh controlled mail, unchanged mapping identities, and recorded outcomes.

## Rollback

Keep all owners and upstream processing stopped while deciding rollback. If no new message, mapping, identity/configuration, log, or retained state has been created since the cutover checkpoint, preserve the partial/new-layout tree separately and restore the **complete verified prior-layout backup** into an empty working location, together with both prior binaries and their recorded launch configuration. Do not overwrite or merge the failed attempt, and do not remove its evidence to make a restore fit. Verify the restored inventory, hashes, permissions, queue reconciliation, and binary/layout pairing before resuming.

If any processing or other write occurred after cutover, a blind backup restore is unsafe: it could lose an issued tag mapping or reintroduce uncertain mail. Preserve both current and backup states and reconcile every post-checkpoint mapping, append, configuration change, queue transition, and delivery with external records under [spec.md §5.3](spec.md#53-backup-restore-layout-changes-and-retention). Restore or resume only after that reconciliation. There is no automatic rollback, migration recovery, or message replay.

## Verification scope

The procedure documents an operator action; preparing this change did not migrate actual server data. Historical rc.7 and earlier test reports retain the layouts they tested. The local rc.8 Release build completed with zero warnings/errors, and independent checks verified 400 deployment files and 396 ZIP entries. All seven configuration-example files moved without byte changes; see [example relocation evidence](artifacts/sender-layout-20260917/example-relocation.json).

An isolated offline cross-version check also passed: the preserved rc.7 executables generated a synthetic mapping using the prior layout, the configuration trees were moved to the nested layout without byte changes, and the rc.8 sorter/tagger processed the same synthetic sender and recipient. They reused the exact existing tag; mapping count stayed one, identity-file bytes remained unchanged, and the message body was preserved. See [migration and artifact audit](artifacts/sender-layout-20260917/migration-and-artifact-audit.json). This was not a live SmarterMail submission or a migration of an actual server datadir; no SM settings were changed.

The full rc.8 suite passed **489 tests, zero failures and zero skips**, and formatting verification passed. Seven added layout cases exercise required roots, no fallback to earlier paths, empty-index sorter independence, consistent sender pointers, and a locked/unreadable pointer that the sorter must never read. Existing executable and identity tests use the new layout. See [full results](artifacts/test-results/sender-layout/sender-layout-full.trx) and [validation-report.md](validation-report.md).

Prior rc.7 artifacts are preserved under `artifacts/sender-layout-20260917/prior-rc7/`. Full deployment gates remain defined in [assumptions.md](assumptions.md).
