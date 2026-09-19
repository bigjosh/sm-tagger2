# sm-tagger

`sm-sorter.exe` and `sm-tagger.exe` provide the two-stage outbound mail flow defined in [`spec.md`](spec.md). The sorter is the small SmarterMail queue-boundary classifier; the tagger owns enrolled mail, persistent recipient mappings, header rewriting, fan-out, and publication.

[`spec.md`](spec.md) defines behavior; [`storage-reference.md`](storage-reference.md) is the authoritative directory and file-format reference. [`implementation.md`](implementation.md) describes the pure managed C# implementation. [`assumptions.md`](assumptions.md) records deployment assumptions and version-sensitive behavior; [`testing-plan.md`](testing-plan.md) defines verification. [`future.md`](future.md) lists deferred work.

## Repository status

The current local candidate is **1.0.0-rc.17**. With `-c`, both programs announce their version, mode, and resolved directories at startup; the tagger also reports sender records and tag mappings loaded from disk. Per-pair concise output is unchanged: sorter `PASS`, `TAKE`, or `FAIL`, and one tagger line per completely moved output pair. The flag remains independent of `-v` and leaves `-l` logging unchanged. See [concise console output](#concise-console-output). Tagged children retain the [child naming contract](storage-reference.md#message-state-suffixes) with a literal lowercase `c` delimiter, such as `42615432c1`; see the [child naming upgrade note](#child-naming-upgrade). Auth and private addresses may coincide within one sender-id, editable sender settings tolerate ordinary editor formatting, and console exceptions use real line breaks. See the [configuration and diagnostics upgrade note](#configuration-and-diagnostics-upgrade). Sender IDs now accept descriptive names under the [identifier rules](storage-reference.md#identifiers-and-addresses), with no UUID requirement; see the [sender-ID upgrade note](#sender-id-upgrade). Tagger logging uses explicit `-l <logfile>` and independent `-v` console tracing; see the [CLI upgrade note](#tagger-cli-upgrade). Mail working queues now live under the spool's `proc/sm-tagger` directory; configuration, permanent mappings, and mapping staging remain in the datadir. The tagger locks both its data and mail queue. Multiple auth accounts still share one sender-id and its policies/tags under the rc.9 model. Installations predating rc.10 require the [offline queue migration](queue-layout-2026-09-17.md), including earlier configuration conversion when applicable; there is no automatic migration or fallback. The owner-confirmed isolation of the Proc subtree is an accepted version-sensitive assumption, not a fresh live-test result. Earlier results below remain historical; current verification is recorded in [validation-report.md](validation-report.md). The public release remains rc.4.

The version 1 implementation, synthetic fixtures, automated tests, configuration examples, and release scripts are present. Local validation includes the contract suite, real filesystem operations, and published executable checks. See [validation-report.md](validation-report.md) for results and limits. [Live SmarterMail build 9742 sequencing tests](live-sequencing-2026-09-16.md) support final-EML discovery followed by a successful HDR read, rejection of `Failed`, and EML-first/HDR-last publication. The tested SMTP and immediate API (`sendImmediately: true`) routes produce their files differently; final EML visibility alone is insufficient. These local Windows 11 observations do not complete the Windows Server, full route coverage, signing, catch-all, or client deployment gates in [assumptions.md](assumptions.md).

The [broader live compatibility suite](live-smartermail-2026-09-16.md) records enrolled SMTP tagging, fan-out, concurrent watchers, local DKIM verification, admission-limit probes, and three local catch-all deliveries with preserved original recipients. It found that the rc.4 tagger holds BOM-prefixed immediate API input. The local **1.0.0-rc.5** correction passed **439 automated tests** and a [fresh compatibility retest](bom-compatibility-2026-09-16.md): five natural API/SMTP cases and one prepared consumer fixture produced eight correctly tagged, independently DKIM-verified deliveries. The tagger preserved the permitted prefix and body bytes; SmarterMail removed the prefix and appended one CRLF on the wire. This correction is not in the published rc.4 executables. Untested deployment routes remain unapproved, and held mail is never automatically replayed.

Target the most recent generally available production versions of SmarterMail and Windows Server at bring-up. Record exact tested builds and servicing levels in `assumptions.md`. `VERSION-SENSITIVE-*` markers identify behavior to recheck after relevant product or platform updates.

The [Failed-spool consumer experiment](failed-spool-2026-09-16.md) confirms that this local SM build removes Failed pairs returned to normal spool, including a specimen accepted with SMTP DATA 250. The local **1.0.0-rc.6** implementation therefore retains confirmed `UPSTREAM_FAILED` pairs in `<datadir>\failed`, outside the entire SM spool tree on the same NTFS volume. The folder is created only when needed; other sorter holds remain in Proc. Build, formatting, publication, and artifact byte checks passed. After the user disabled Smart App Control, the unchanged binaries passed **455 tests, zero failures and zero skips**. The earlier 441-pass/14-failure run remains recorded; Windows blocked those standalone launches before startup. Compatibility with Smart App Control enabled is not established. The [three live SM scenarios](failed-retention-2026-09-16.md) used the application-folder sorter, preserving both failed pairs and delivering the later normal control once; no additional live trial was run for the policy retest. No new public release was created, and this correction is absent from the published rc.4 executables. This paragraph records the historical rc.6 location; rc.10 retains the same outcome under the inert Proc mailroot defined in the storage reference.

## Start here

If delivered mail appears missing from SmarterMail's log viewer, see the [Delivery log lookup instructions](deployment.md#observe-failures) and [outbound logging investigation](outbound-logging-2026-09-18.md). Locally tested build 9742 writes complete raw conversations but fails to correlate the former hyphenated child IDs with **Display Related Traffic**. The new `c` delimiter uses a hexadecimal-compatible character to address that viewer behavior; the child ordinal remains decimal. Verify parent-basename Related Traffic searches on the installed build. For earlier names or a failed lookup, use matching-only search with the actual bracketed session ID, or inspect the raw Delivery log.

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
sm-sorter.exe <datadir> [-l <logfile>] [-v] [-c] <spooldir> [<basename>]
sm-tagger.exe <datadir> [-l <logfile>] [-v] [-c] [-keep] <spooldir> [<basename>]
```

Omit the basename for watch mode. Supplying it processes only that plain message pair and exits. Each program's options must appear immediately after `datadir`, before `spooldir`; `-l`, `-v`, `-c`, and tagger `-keep` may appear in any order there, at most once each.

Missing, extra, or invalid arguments print usage hints and examples to standard error, then exit with status 1 before opening either queue. Paths containing spaces must be quoted.

For the sorter, `-l <logfile>` records one best-effort terminal result per processed message: `PASS`, `DIVERT`, or `ERROR`; the [log reference](storage-reference.md#logs-and-diagnostics) defines its format. Relative log paths resolve from the working directory; create the parent directory first. `-v` writes lifecycle, scans, readiness deferrals, routing decisions, and file-operation debugging to standard output. These options and `-c` are independent; errors still go to standard error without any output option. A stale watcher entry that disappears before ownership and an input deferred as not ready get no email-log record.

The sorter discovers `.eml` files in `proc`, checks the EML before any HDR access, then reads the matching same-basename HDR with read-only access excluding writers. The handle closes before ownership. An HDR alone never starts processing. The [message-file reference](storage-reference.md#message-files) defines how to interpret status bytes. Exact `Written` permits ordinary classification. `Writing` defers with `HDR_WRITING`, quietly on watch-mode stderr. Every other or incomplete status except `Failed` defers with `HDR_STATUS_UNEXPECTED` and reports stderr on every encounter, regardless of flags. An unexpected or incomplete status may require manual investigation indefinitely.

Exact `Failed` keeps the prior outcome and move order at the current `<mailroot>\failed` location: claim `.hdr.sort`, lazily create retention storage, then move EML first and the still-suffixed HDR last without replacement. A best-effort `.sort.err` follows the current HDR. Retention failure preserves and reports actual partial locations; successful retention still attempts `ERROR reason=UPSTREAM_FAILED` in the optional log and returns 1 in one-shot mode. Other holds stay in Proc. Deferrals leave mail untouched and create no ownership suffix, diagnostic, or terminal email-log record; watch mode reconsiders them and one-shot reports `NOT READY` with exit 1. See the [storage reference](storage-reference.md#runtime-directory-trees) for `<mailroot>` and retained paths.

Both modes enforce their required locks: the sorter holds its Proc lock, and the tagger holds data then mail-queue locks throughout the invocation. For the tagger, `-l <logfile>` saves the detailed execution trace to the selected file. Independent `-v` prints the same events with a `DEBUG ` prefix to standard output; without it verbose output stays quiet. Independent `-c` enables concise summaries; without either console flag stdout stays quiet. A file failure does not disable console tracing, and a console failure does not disable the file. Both are best effort, and errors always go to standard error. Use dedicated files outside queues, configuration, and mapping records; create the parent directory first. `-keep` retains input/output debugging copies with the mail queue. See [locks](storage-reference.md#locks), [logs and diagnostics](storage-reference.md#logs-and-diagnostics), and [message-state suffixes](storage-reference.md#message-state-suffixes) for their locations and meanings.

Logging failures print to standard error and mail processing continues, including the current message. Corrupted or missing logs are acceptable. An activated message with the wrong number of `From:` fields or mailboxes is a real contract error: retain its original pair as `.hdr.err` and `.eml.err`, attempt the parent `.err` diagnostic and requested trace, and print the error to standard error. Out-of-band delivery of standard-error messages is deferred to FUT-005.

### Concise console output

For either executable, add `-c` after the datadir to show startup information and one line per complete output-pair move. After acquiring its required locks, each program prints its name/version and watch or one-shot mode, then the resolved data and spool directories. After loading configuration and mappings successfully, the tagger also prints the number of sender records and tag mappings loaded from disk, including individual and group tags. These counts use the startup snapshot, exclude inert staging, and are not updated as new tags are created. The sorter does not load or count sender records. The welcome says `starting`: it does not establish that configuration has loaded or a watcher is ready. A later startup failure still goes to stderr; a failed load has no load summary.

For each completed pair move, the sorter prints the original basename followed by `PASS` for `spool`, `TAKE` for `process`, or `FAIL` for `failed`, with no trailing destination clause. The tagger prints each published child separately with its individual `created tag` or `used tag`, or one unchanged parent for pass-through. Lines use server-local time, original HDR envelope addresses, authentication status, and the sorter outcome or tagger destination; the [concise console reference](storage-reference.md#concise-console-output--c) defines the exact fields and examples.

`-v` and `-c` simply enable their respective outputs: both flags emit both. `-l` file logging and per-mapping tag logs retain their existing formats and UTC timestamps. Apart from startup information, only completed pair moves get concise summaries, so deferrals, stale entries, partial/failed moves, and internal state renames remain absent; errors still go to stderr. A completed move does not prove final delivery. Console failures are best effort and never hold mail or change its outcome.

The rc.17 startup information, rc.16 sorter format change, and rc.15 option need no configuration, mapping, or queue conversion. Stop each program and let its current work finish before replacing its executable. Binaries predating rc.15 reject `-c`; remove that optional flag from a launcher before using one.

### Child naming upgrade

Moving from rc.13 to rc.14 requires no configuration or permanent mapping migration. Stop the tagger and let its current work finish before replacing its executable. New tagged outputs follow the [message naming contract](storage-reference.md#message-state-suffixes): the original basename, literal lowercase `c`, and the decimal child number starting at 1 in canonical recipient order. For example, parent `42615432` produces `42615432c1`, `42615432c2`, and so on. Original spelling and pass-through filenames are unchanged; input names do not acquire a numeric-only requirement.

Do not rename, resubmit, or replay existing hyphenated pending or partially published mail merely to obtain the new names. Preserve its actual filenames and reconcile its existing state under the normal failure procedure; an earlier child may already have delivered. Historical logs and evidence retain their original names. Update any external scripts or monitoring that parse child filenames. See `VERSION-SENSITIVE-003` and `VERSION-SENSITIVE-015` in [assumptions.md](assumptions.md) for namespace and log-viewer limits; shortened Delivery session IDs are not guaranteed globally unique.

### Configuration and diagnostics upgrade

Moving from rc.12 to rc.13 needs no storage conversion. Auth/private equality is now accepted within one sender-id; cross-sender address conflicts remain rejected. The [sender settings formats](storage-reference.md#sender-configuration) accept leading UTF-8 BOM characters and surrounding whitespace, and `allow-mdn.txt` accepts `true`/`false` without regard to case. Sender-ID and permanent mapping identity rules remain exact. Earlier binaries may reject configurations using these allowances. Stop and restart the tagger to reload settings.

Console exception chains and stack traces now print on real lines, with readable Windows paths. Tagger `-v` carries the same event values and identifiers as its file trace but uses readable console formatting; file records keep escaped line breaks. This is a display change, with no queue or mapping migration.

### Sender-ID upgrade

Moving from rc.11 to rc.12 needs no storage conversion: existing UUID sender IDs and mappings remain valid and unchanged. For a new sender, choose a permanent name using the [identifier rules](storage-reference.md#identifiers-and-addresses); the record directory and every pointer must retain its exact spelling. Do not rename established records or rewrite mapping identity files to make existing IDs more descriptive. Earlier tagger binaries require canonical lowercase UUIDs and cannot load new descriptive IDs, so a rollback after adding such records requires an offline review of all affected configuration and permanent mappings. No automatic conversion is performed.

### Tagger CLI upgrade

For rc.11 and later, replace `-log` in every tagger launcher with `-l "<chosen-log-path>"`; the old flag is rejected with usage hints. To continue the earlier file, explicitly select `-l "D:\sm-tagger-data\log.txt"` (substitute the actual path). There is no default log path or automatic log move. Add `-v` when console debugging is wanted; `-keep` is unchanged. A move from rc.10 needs only launcher updates and matching binaries, with no queue/configuration conversion. Keep protected backups of selected log locations too if they sit outside the data/mail roots. Older upgrades still require the linked offline layout/configuration procedures.
