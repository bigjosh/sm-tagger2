# Build and deployment

Version 1 provides two Windows console applications. Local automated tests cover the program contract; an actual SmarterMail deployment remains subject to the evidence gates in [assumptions.md](assumptions.md) and [testing-plan.md](testing-plan.md).

For the required folders and commands at each stage, start with [bring-up.md](bring-up.md). It begins with an empty enrollment index and sorter-only pass-through before introducing tagger profiles.

## Build and verify

Install the .NET 10 SDK selected by [global.json](global.json), then run from the repository root in Windows PowerShell 5.1 or PowerShell 7:

```powershell
dotnet restore SmTagger.slnx --locked-mode
dotnet build SmTagger.slnx -c Release --no-restore
dotnet format SmTagger.slnx --verify-no-changes --no-restore
.\scripts\Publish-Release.ps1
dotnet test SmTagger.slnx -c Release --no-build --no-restore
```

`scripts/Validate.ps1` runs that complete sequence and writes a TRX report under `artifacts/test-results/`. Tests use isolated temporary directories. Three optional sample tests run when the private `samples/` folder exists and otherwise report explicit skips. Release process tests report explicit skips until both production executables have been published. The complete validation script publishes them before testing.

If Windows PowerShell blocks these locally created scripts under its default execution policy, a process-scoped invocation is `powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\Validate.ps1`. This does not change the machine's saved execution policy. Organizational policy still takes precedence.

The test dependency versions are locked in `tests/SmTagger.Tests/packages.lock.json`. Production code uses the .NET base class library. Unit tests may use narrow internal phase, stream, clock, or randomness seams; the production programs provide no command-line, environment, configuration, or message-controlled route to those seams.

## Release files

Download release assets from [GitHub Releases](https://github.com/bigjosh/sm-tagger2/releases), or run `scripts/Publish-Release.ps1` to build them locally. Both distribution forms target Windows x64 and include their .NET runtime, so the mail server needs neither an SDK nor a separate runtime installation.

| Build output under `artifacts/release/` | Deployment |
|---|---|
| `standalone/sm-sorter.exe`, `standalone/sm-tagger.exe` | Copy the two EXEs into the chosen executable directory. They need no companion DLLs. Retain the supplied runtime license/notices with the distribution. |
| `sm-sorter/`, `sm-tagger/` | Copy each complete application folder, including all dependencies and runtime files. The EXE from one of these folders cannot be copied on its own. |
| `sm-sorter-win-x64.zip`, `sm-tagger-win-x64.zip` | Archives of the complete application folders; extract each folder intact. |
| `manifest.json` | SHA-256 hashes of the deployment files and archives; verify the selected assets before running them. |

The tested .NET 10.0.11 standalone builds use the runtime inside the executable; local host tracing and execution observed no extracted runtime files. See [validation-report.md](validation-report.md) for the tested scope. This does not establish the behavior of every future runtime build or deployment host.

Some bundled runtime versions may extract native components before startup under the process account's `%TEMP%\.net`, or under `DOTNET_BUNDLE_EXTRACT_BASE_DIR` when set. If extraction is required, the account needs a writable location protected from other accounts. Verify startup and any extraction under the actual scheduler/wrapper identity for the selected release (`LAB-013`); [Microsoft's single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview#native-libraries) describes the extraction behavior when used.

The publish script recreates generated output directories. Keep configuration, mail, and operator evidence outside them. The sorter includes no tagger-only parsing or mapping assemblies in either distribution. Republishing with a supported SDK servicing update also updates the bundled runtime.

The local build host is Windows 11, not the target Windows Server installation. Test the selected release assets and record their manifest hashes on the actual supported Windows Server and SmarterMail builds before enabling production mail flow. Release candidates remain subject to the pending deployment gates in [assumptions.md](assumptions.md).

## Prepare the data and spool trees

For sorter-only pass-through, create an empty, accessible `<datadir>\senders` directory. The existing spool root and its `proc` directory must be ready before startup. `profiles`, `process`, and mapping directories are not needed for that stage. Unfinished input stays untouched; malformed or ambiguous completed HDRs still fail closed.

For tagger startup, both `senders/` and `profiles/` must exist and enumerate successfully. Use [examples/README.md](examples/README.md) and its synthetic `datadir/` tree to understand the required profile and index files. Replace the example identities and UUID before real use. The tagger rejects incomplete or inconsistent identities and mappings at startup. [bring-up.md](bring-up.md) identifies which working directories the programs create automatically.

The operator must configure every intended SmarterMail submission path to enter the selected `<spooldir>\proc` queue. SmarterMail invokes neither program. The spool root, its `proc` directory, and the datadir/process/staging/mapping trees must share the required ordinary local NTFS volume; the programs rely on that installation condition. Apply the intended filesystem permissions to mail, permanent mappings, and unredacted operational diagnostics.

Verify SmarterMail's producer readiness behavior separately from our EML-first/HDR-last publication: the HDR is written first and can exist for an attempt that never produces an EML. The sorter discovers final EML files, then reads their matching HDRs and rejects `Failed`. The installed server must establish that final EML publication means completed EML bytes and final HDR metadata (`LAB-001`); there is no `Written` whitelist. Also verify admission limits, basename namespace, arbitrary new tag acceptance, post-rewrite aligned DKIM/DMARC, SPF for changed envelope domains, and catch-all delivery. Keep MDNs disabled until every permitted sender/client path has its required evidence. The `VERSION-SENSITIVE-*` markers beside dependent source code identify checks to repeat after updates.

## Run

Start each watcher independently, supplying your approved paths. These examples place the standalone EXEs together in `D:\sm-tagger-bin`; for application-folder deployments, include the corresponding `sm-sorter` or `sm-tagger` subdirectory:

```powershell
& 'D:\sm-tagger-bin\sm-sorter.exe' 'D:\sm-tagger-data' -l 'D:\sm-tagger-data\sorter-mail.log' -v 'D:\SmarterMail\Spool'
& 'D:\sm-tagger-bin\sm-tagger.exe' 'D:\sm-tagger-data' -log 'D:\SmarterMail\Spool'
```

Each command is a long-running console process; use separate consoles or independently supervised jobs. These are not native Windows services. A host must capture standard error and use a supported Ctrl+C/Ctrl+Break console event for orderly shutdown; terminating the process otherwise has crash semantics. Capture sorter standard output too when `-v` is enabled. Task Scheduler/service-wrapper behavior still needs verification on the chosen deployment host.

Sorter `-l <logfile>` appends one UTF-8 terminal result line per processed message, including UTC time, basename, auth when known, reason, and known file locations. `PASS` means unchanged publication to the spool completed; `DIVERT` means handoff to the tagger queue completed; `ERROR` means sorting failed. These are sorter outcomes, not delivery receipts. The log contains no startup, shutdown, scan, or readiness-deferral records, and a stale watcher HDR that vanishes before ownership gets no record. An explicitly requested missing one-shot HDR does get an `ERROR` record.

Sorter `-v` independently writes lifecycle, scan, routing, and file-operation debugging to standard output. Errors always go to standard error. Put sorter options after the datadir and before the spooldir, in either order and at most once each. Prefer an absolute log path; relative paths resolve against the process working directory. Create the log's parent directory yourself, use a dedicated file separate from mail and the tagger's `log.txt`, and protect the unredacted output. There is no automatic directory creation, rotation, truncation, repair, or retry for the email log.

For a controlled one-shot test, append the exact plain-message basename. Tagger options appear immediately after the datadir. A leading dash in the basename is allowed. One-shot execution cannot overlap a watcher of the same role. `-keep` retains parent `.in` and final `.out` evidence. The source EML body and unsupported headers are always preserved.

The sorter scans final `.eml` files and checks the EML before any HDR access. An HDR-only attempt remains untouched; a selected EML that disappears is stale in watch mode. A missing EML in one-shot mode or an HDR sharing/lock conflict produces `DEFER` with `-v`; watch mode continues and reconsiders EML candidates, while one-shot mode reports `NOT READY` and returns 1 without waiting. After final EML discovery, exact case-sensitive `Failed` on the HDR's first CRLF-terminated line, allowing trailing SP/HTAB, produces a retained `UPSTREAM_FAILED` error: `.hdr.sort`, plain EML, best-effort `.sort.err`, stderr, and terminal `ERROR`. Other statuses stay opaque and normal framing/auth validation still applies. The tagger scans `.hdr` files; both roles move EML first and HDR last into their destination. Incomplete producer attempts can remain in `proc`; observe queue age and investigate them manually.

## Observe failures

Logging is best effort. A sorter email-log, console-debug, tagger trace, or tag-log failure does not change the mail outcome. File logging failures print to standard error and the same current message continues. A failed sorter email log or tagger execution trace is disabled for the rest of that invocation; a later invocation may try again. Corrupt and missing logs are accepted. A successful operation may therefore have no log record, and even a successful sorter result does not prove delivery.

Mail-contract and processing errors retain inert files and attempt a parent diagnostic. Sorter failures after ownership retain `.hdr.sort` plus the EML at its actual location and attempt a `.sort.err` diagnostic. Activated `From:` count failures retain original `.hdr.err`/`.eml.err` pairs when both retention moves succeed. Other tagger failures retain the actual `.start`, `.break`, `.process`, or `.pend` states. Edited/synthesized header lines above 998 bytes reject the parent before any child publication. Publication errors may leave earlier children live and later children pending; use the parent diagnostic and exact paths to reconcile that state.

The programs never retry or repair retained mail or staging entries automatically. Restart loads complete permanent mappings and reports inert leftovers; the sorter considers final EML candidates and the tagger considers plain HDR candidates. A plain EML beside only `.hdr.sort` has no matching plain HDR and is skipped as stale. A new matching plain pair alongside an old `.hdr.sort` is not a recovery path: the non-replacing claim still fails on that collision. Stop the relevant owners before administrative changes or disposition. Automated notification, inbound processing, and recovery remain deferred in [future.md](future.md).
