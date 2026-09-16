# sm-tagger

`sm-sorter.exe` and `sm-tagger.exe` provide the two-stage outbound mail flow defined in [`spec.md`](spec.md). The sorter is the small SmarterMail queue-boundary classifier; the tagger owns enrolled mail, persistent recipient mappings, header rewriting, fan-out, and publication.

[`spec.md`](spec.md) defines the behavior and [`implementation.md`](implementation.md) describes the pure managed C# implementation. [`assumptions.md`](assumptions.md) records deployment assumptions and version-sensitive behavior; [`testing-plan.md`](testing-plan.md) defines verification. [`future.md`](future.md) lists deferred work.

## Repository status

The version 1 implementation, synthetic fixtures, automated tests, configuration examples, and release scripts are present. Local validation includes the contract suite, real filesystem operations, and published executable checks. See [validation-report.md](validation-report.md) for the recorded results and limits. A live sorter trial exposed an unfinished plain HDR; the sorter now discovers final EML files before reading their matching HDRs. The corrected producer/readiness contract still needs live verification under [VERSION-SENSITIVE-001](assumptions.md#version-sensitive-001). SmarterMail, Windows Server, SMTP/signing, catch-all, and client deployment gates remain pending.

Target the most recent generally available production versions of SmarterMail and Windows Server at bring-up. Record exact tested builds and servicing levels in `assumptions.md`. `VERSION-SENSITIVE-*` markers identify behavior to recheck after relevant product or platform updates.

## Start here

Download the Windows x64 executables from [GitHub Releases](https://github.com/bigjosh/sm-tagger2/releases). The standalone `sm-sorter.exe` and `sm-tagger.exe` each include their dependencies and .NET runtime; no SDK or runtime installation is required on the mail server. Complete application-folder ZIPs are also provided. See [release files](deployment.md#release-files) for the two layouts, checksums, and runtime hosting checks.

Follow [bring-up.md](bring-up.md) for the required folders and checks at each stage: sorter-only pass-through with an empty `senders` directory, one-message tests, watcher operation, and enrollment of the first tagging account. [deployment.md](deployment.md) covers release packaging and ongoing operation.

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

| Path | Responsibility |
|---|---|
| [src/SmSorter/](src/SmSorter/) | Sorter console and HDR-only routing processor |
| [src/SmTagger/](src/SmTagger/) | Tagger console and startup lifecycle |
| [src/SmTagger.Shared/](src/SmTagger.Shared/) | Shared parsing primitives, queue discovery, invocation, and singletons |
| [src/SmTagger.Mail/](src/SmTagger.Mail/) | Full HDR/EML parsing and byte-preserving edits |
| [src/SmTagger.Engine/](src/SmTagger.Engine/) | Configuration, permanent mappings, diagnostics, and tagger processing |
| [tests/SmTagger.Tests/](tests/SmTagger.Tests/) | Synthetic contracts, filesystem tests, and published executable tests |
| [tests/SmTagger.CrashWorker/](tests/SmTagger.CrashWorker/) | Test-only process for deterministic termination at internal mail-state boundaries; excluded from deployment packages |
| [examples/](examples/) | Synthetic profile and auth-index configuration; operational data layout is in [spec.md §5](spec.md#5-data-directory-layout) |

Private actual-server messages in an optional local `samples/` folder stay outside source control and release artifacts. Their compatibility tests skip explicitly when the folder is absent; synthetic fixtures remain available in the test project.

## Invocation

```text
sm-sorter.exe <datadir> [-l <logfile>] [-v] <spooldir> [<basename>]
sm-tagger.exe <datadir> [-log] [-keep] <spooldir> [<basename>]
```

Omit the basename for watch mode. Supplying it processes only that plain message pair and exits. Each program's options must appear immediately after `datadir`, before `spooldir`; the sorter's `-l` and `-v` may appear in either order, at most once each.

Missing, extra, or invalid arguments print usage hints and examples to standard error, then exit with status 1 before opening either queue. Paths containing spaces must be quoted.

For the sorter, `-l <logfile>` appends one best-effort UTF-8 terminal result line per processed message: `PASS`, `DIVERT`, or `ERROR`, with UTC time, basename, auth when known, reason, and file locations. Relative log paths resolve from the working directory; create the parent directory first. `-v` writes lifecycle, scans, readiness deferrals, routing decisions, and file-operation debugging to standard output. These options are independent; errors still go to standard error without either option. A stale watcher entry that disappears before ownership and an input deferred as not ready get no email-log record.

The sorter discovers `.eml` files in `proc`, then reads each matching same-basename HDR. An HDR alone never starts processing. HDR status `Failed` is rejected and retained as `.hdr.sort` with its EML and best-effort `.sort.err`; other statuses are opaque, with no `Written` requirement. A missing final EML in one-shot mode or a busy HDR is deferred untouched: one-shot mode reports `NOT READY` and exits 1 without waiting, while watch mode reconsiders EML candidates. Vanished watcher inputs are skipped as stale. The tagger still discovers `.hdr` files in `process`. Both programs publish EML first and HDR last, and neither automatically repairs or retries retained artifacts.

Both modes enforce their singleton lock. For the tagger, `-log` requests the best-effort UTF-8 execution trace at `<datadir>\log.txt`; without it the tagger never opens that file. `-keep` retains `.in` and `.out` debugging copies in `<datadir>\process`.

Logging failures print to standard error and mail processing continues, including the current message. Corrupted or missing logs are acceptable. An activated message with the wrong number of `From:` fields or mailboxes is a real contract error: retain its original pair as `.hdr.err` and `.eml.err`, attempt the parent `.err` diagnostic and requested trace, and print the error to standard error. Out-of-band delivery of standard-error messages is deferred to FUT-005.
