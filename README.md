# sm-tagger

`sm-sorter.exe` and `sm-tagger.exe` provide the two-stage outbound mail flow defined in [`spec.md`](spec.md). The sorter is the small SmarterMail queue-boundary classifier; the tagger owns enrolled mail, persistent recipient mappings, header rewriting, fan-out, and publication.

[`spec.md`](spec.md) defines behavior; [`storage-reference.md`](storage-reference.md) is the authoritative directory and file-format reference. [`implementation.md`](implementation.md) describes the pure managed C# implementation. [`assumptions.md`](assumptions.md) records deployment assumptions and version-sensitive behavior; [`testing-plan.md`](testing-plan.md) defines verification. [`future.md`](future.md) lists deferred work.

## Repository status

The current local candidate is **1.0.0-rc.10**. Mail working queues now live under the spool's `proc/sm-tagger` directory; configuration, permanent mappings, mapping staging, and the tagger trace remain in the datadir. The tagger locks both its data and mail queue. Multiple auth accounts still share one sender-id and its policies/tags under the rc.9 model. Existing installations require the [offline queue migration](queue-layout-2026-09-17.md), including earlier configuration conversion when applicable; there is no automatic migration or fallback. The owner-confirmed isolation of the Proc subtree is an accepted version-sensitive assumption, not a fresh live-test result. Earlier results below remain historical; current verification is recorded in [validation-report.md](validation-report.md). The public release remains rc.4.

The version 1 implementation, synthetic fixtures, automated tests, configuration examples, and release scripts are present. Local validation includes the contract suite, real filesystem operations, and published executable checks. See [validation-report.md](validation-report.md) for results and limits. [Live SmarterMail build 9742 sequencing tests](live-sequencing-2026-09-16.md) support final-EML discovery followed by a successful HDR read, rejection of `Failed`, and EML-first/HDR-last publication. The tested SMTP and immediate API (`sendImmediately: true`) routes produce their files differently; final EML visibility alone is insufficient. These local Windows 11 observations do not complete the Windows Server, full route coverage, signing, catch-all, or client deployment gates in [assumptions.md](assumptions.md).

The [broader live compatibility suite](live-smartermail-2026-09-16.md) records enrolled SMTP tagging, fan-out, concurrent watchers, local DKIM verification, admission-limit probes, and three local catch-all deliveries with preserved original recipients. It found that the rc.4 tagger holds BOM-prefixed immediate API input. The local **1.0.0-rc.5** correction passed **439 automated tests** and a [fresh compatibility retest](bom-compatibility-2026-09-16.md): five natural API/SMTP cases and one prepared consumer fixture produced eight correctly tagged, independently DKIM-verified deliveries. The tagger preserved the permitted prefix and body bytes; SmarterMail removed the prefix and appended one CRLF on the wire. This correction is not in the published rc.4 executables. Untested deployment routes remain unapproved, and held mail is never automatically replayed.

Target the most recent generally available production versions of SmarterMail and Windows Server at bring-up. Record exact tested builds and servicing levels in `assumptions.md`. `VERSION-SENSITIVE-*` markers identify behavior to recheck after relevant product or platform updates.

The [Failed-spool consumer experiment](failed-spool-2026-09-16.md) confirms that this local SM build removes Failed pairs returned to normal spool, including a specimen accepted with SMTP DATA 250. The local **1.0.0-rc.6** implementation therefore retains confirmed `UPSTREAM_FAILED` pairs in `<datadir>\failed`, outside the entire SM spool tree on the same NTFS volume. The folder is created only when needed; other sorter holds remain in Proc. Build, formatting, publication, and artifact byte checks passed. After the user disabled Smart App Control, the unchanged binaries passed **455 tests, zero failures and zero skips**. The earlier 441-pass/14-failure run remains recorded; Windows blocked those standalone launches before startup. Compatibility with Smart App Control enabled is not established. The [three live SM scenarios](failed-retention-2026-09-16.md) used the application-folder sorter, preserving both failed pairs and delivering the later normal control once; no additional live trial was run for the policy retest. No new public release was created, and this correction is absent from the published rc.4 executables. This paragraph records the historical rc.6 location; rc.10 retains the same outcome under the inert Proc mailroot defined in the storage reference.

## Start here

Download the Windows x64 executables from [GitHub Releases](https://github.com/bigjosh/sm-tagger2/releases). Both distribution forms include their dependencies and .NET runtime; no SDK or runtime installation is required on the mail server. Use the [release inventory](storage-reference.md#release-and-repository-trees) to select a distribution, then follow the [deployment instructions](deployment.md#release-files).

Follow [bring-up.md](bring-up.md) for the required folders and checks at each stage: sorter-only pass-through with empty `senders/auth-addresses`, one-message tests, watcher operation, and enrollment of the first tagging account. The sorter does not need `senders/sender-ids`; the tagger requires both nested roots, even when empty. [deployment.md](deployment.md) covers release packaging and ongoing operation.

## Build and validate

[SmTagger.slnx](SmTagger.slnx) targets `net10.0` with the .NET 10 SDK selected by [global.json](global.json). From the repository root in PowerShell, run the complete restore, Release build, formatting, publish, and test sequence:

```powershell
.\scripts\Validate.ps1
```

To produce release artifacts without running the complete validation sequence:

```powershell
.\scripts\Publish-Release.ps1
```

The two standalone EXEs are under `artifacts/release/standalone/`. Complete application folders, ZIP archives, and the SHA-256 manifest are under `artifacts/release/`; validation writes its TRX report under `artifacts/test-results/`. See [deployment.md](deployment.md) for setup and operating instructions.

## Repository layout

See the authoritative [repository and release trees](storage-reference.md#release-and-repository-trees) for source, test, example, and generated-artifact locations, and the [runtime trees](storage-reference.md#runtime-directory-trees) for deployed mail/configuration storage.

Private actual-server messages in an optional local `samples/` folder stay outside source control and release artifacts. Their compatibility tests skip explicitly when the folder is absent; synthetic fixtures remain available in the test project.

## Invocation

```text
sm-sorter.exe <datadir> [-l <logfile>] [-v] <spooldir> [<basename>]
sm-tagger.exe <datadir> [-log] [-keep] <spooldir> [<basename>]
```

Omit the basename for watch mode. Supplying it processes only that plain message pair and exits. Each program's options must appear immediately after `datadir`, before `spooldir`; the sorter's `-l` and `-v` may appear in either order, at most once each.

Missing, extra, or invalid arguments print usage hints and examples to standard error, then exit with status 1 before opening either queue. Paths containing spaces must be quoted.

For the sorter, `-l <logfile>` records one best-effort terminal result per processed message: `PASS`, `DIVERT`, or `ERROR`; the [log reference](storage-reference.md#logs-and-diagnostics) defines its format. Relative log paths resolve from the working directory; create the parent directory first. `-v` writes lifecycle, scans, readiness deferrals, routing decisions, and file-operation debugging to standard output. These options are independent; errors still go to standard error without either option. A stale watcher entry that disappears before ownership and an input deferred as not ready get no email-log record.

The sorter discovers `.eml` files in `proc`, checks the EML before any HDR access, then reads the matching same-basename HDR with read-only access excluding writers. The handle closes before ownership. An HDR alone never starts processing. The [message-file reference](storage-reference.md#message-files) defines how to interpret status bytes. Exact `Written` permits ordinary classification. `Writing` defers with `HDR_WRITING`, quietly on watch-mode stderr. Every other or incomplete status except `Failed` defers with `HDR_STATUS_UNEXPECTED` and reports stderr on every encounter, regardless of flags. An unexpected or incomplete status may require manual investigation indefinitely.

Exact `Failed` keeps the prior outcome and move order at the current `<mailroot>\failed` location: claim `.hdr.sort`, lazily create retention storage, then move EML first and the still-suffixed HDR last without replacement. A best-effort `.sort.err` follows the current HDR. Retention failure preserves and reports actual partial locations; successful retention still attempts `ERROR reason=UPSTREAM_FAILED` in the optional log and returns 1 in one-shot mode. Other holds stay in Proc. Deferrals leave mail untouched and create no ownership suffix, diagnostic, or terminal email-log record; watch mode reconsiders them and one-shot reports `NOT READY` with exit 1. See the [storage reference](storage-reference.md#runtime-directory-trees) for `<mailroot>` and retained paths.

Both modes enforce their required locks: the sorter holds its Proc lock, and the tagger holds data then mail-queue locks throughout the invocation. For the tagger, `-log` requests the best-effort execution trace in the datadir; without it the tagger never opens that file. `-keep` retains input/output debugging copies with the mail queue. See [locks](storage-reference.md#locks), [logs and diagnostics](storage-reference.md#logs-and-diagnostics), and [message-state suffixes](storage-reference.md#message-state-suffixes) for their locations and meanings.

Logging failures print to standard error and mail processing continues, including the current message. Corrupted or missing logs are acceptable. An activated message with the wrong number of `From:` fields or mailboxes is a real contract error: retain its original pair as `.hdr.err` and `.eml.err`, attempt the parent `.err` diagnostic and requested trace, and print the error to standard error. Out-of-band delivery of standard-error messages is deferred to FUT-005.
