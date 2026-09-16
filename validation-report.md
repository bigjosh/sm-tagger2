# Local implementation validation

Version 1 is implemented and passes the local validation scope below. The release-candidate validation on **2026-09-16** passed **396 tests, with zero failures and zero skips**. The Release build completed with **zero warnings and zero errors**, and formatting verification passed. Both application-folder and standalone EXE distributions are ready for the controlled deployment tests in [testing-plan.md](testing-plan.md). Earlier implementation review and fixes are recorded in [audit-2026-09-07.md](audit-2026-09-07.md).

## Reproduce and inspect

Run [scripts/Validate.ps1](scripts/Validate.ps1). It performs a locked restore, Release build, formatting verification, self-contained folder and standalone publication, and the complete test suite. The release-candidate run used Windows PowerShell 5.1. From the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate.ps1
```

The execution policy applies only to that process. The saved machine policy was not changed. See [deployment.md](deployment.md) for ordinary build and setup instructions.

Evidence from this run:

- [Test results](artifacts/test-results/local-contracts.trx).
- [Validation command output](artifacts/test-results/validation-v1.0.0-rc.4.txt).
- [Release file and package SHA-256 manifest](artifacts/release/manifest.json).
- [Independent artifact verification](artifacts/test-results/artifact-verification-v1.0.0-rc.4.json).
- [Isolated private-capture readiness checks](artifacts/test-results/private-readiness-summary-v1.0.0-rc.4.json); the summary contains no captured identities or message contents.
- Seven deterministic crash-boundary tests, included in the complete test results above.
- [Dependency advisory audit](artifacts/test-results/dependency-audit-2026-09-16.json).

Generated evidence and packages live under ignored `artifacts/`. The validation script recreates the packages and complete test results; independent artifact verification, advisory audits, and the isolated capture checks are supplemental local evidence. The private sample tests skip explicitly if `samples/` is absent. Release executable tests skip explicitly if the applications have not been published; the validation script publishes them first.

The current release folders, standalone EXEs, ZIPs, and manifest represent **v1.0.0-rc.4**. Public assets and their manifest are available from [the GitHub release](https://github.com/bigjosh/sm-tagger2/releases/tag/v1.0.0-rc.4). Raw local evidence and private messages are not published.

## Tested environment

| Item | Recorded value |
|---|---|
| Build/test host | Windows 11 x64, OS version 10.0.26200 |
| Filesystems | Local C: and D: volumes reported NTFS and healthy; isolated test trees used the local temporary directory |
| SDK | 10.0.400, pinned by `global.json` |
| Target / bundled runtime | `net10.0` / Microsoft.NETCore.App 10.0.11 |
| Publication | `1.0.0-rc.4`, Release, self-contained `win-x64`; application folders and single-file EXEs; no trimming |
| Dependency audit | `dotnet list SmTagger.slnx package --vulnerable --include-transitive --format json` reported no vulnerable package entries on this date |

Production projects use the .NET base class library. Test package versions are locked. The dependency audit is a dated advisory check, not a guarantee that future vulnerabilities will not be discovered.

## Automated coverage

| Area | Passing tests | Evidence exercised |
|---|---:|---|
| Mail parsing and identities | 87 | Bounded ASCII syntax, canonicalization, folding/display tokens, HDR framing, MDN classification, recipient codec, exact preservation outside permitted edits, edited/synthesized continuation lines at 998/999 bytes |
| Configuration and permanent mappings | 71 | Required files and cross-profile uniqueness, current/retired identities, complete checked-in example, startup validation, ignored template annotations, stable mapping reuse, token collisions, atomic directory publication, fatal post-publication cache failure |
| Tagger processing and failure states | 80 | Current/retired matches in every supported sender location, fan-out and group replies, `.err` retention for From counts, real-file partial-write/close failure retention, complete stderr failure state, all-child readiness and keep-copy gates, sequential publication, cleanup, header length rejection and new mapping reuse after restart |
| Best-effort diagnostics | 24 | Missing/corrupt logs, initialization/append/encoding/flush/disposal failures, trace disablement, stderr reporting, preservation of the underlying processing result |
| Sorter diagnostics | 23 | One result per selected message, all route types, retained error state, stale watcher suppression, escaped append-only UTF-8 records, unread EML contents, and independent file/console failures without changed mail outcomes |
| Sorter input readiness | 17 | Final EML checked before HDR access, untouched provisional and orphan HDRs, final metadata replacement, upstream Failed rejection, opaque other statuses, malformed completed HDR retention, busy-HDR deferral, and unchanged message bytes |
| Sorter, invocation, and queue lifecycle | 53 | HDR-only routing, unread EML pass-through, ownership ordering, literal basenames, independent singleton locks, sorted scans, native notification arrivals, overflow/error paths, stopping after owned work |
| Published executable processes | 11 | End-to-end sorter/tagger operation, exit status and logging failures, independent watchers, live sorter log flushing, final-EML discovery with orphan HDRs and temporary EMLs, upstream Failed retention with later mail continuing, one-shot readiness deferral, singleton contention, startup-failure isolation, crash/restart, release dependency isolation |
| Standalone executable processes | 20 | Twelve argument-error cases verify concise usage hints, exit status, untouched queues/configuration, and absent locks; relocated EXEs with no adjacent dependencies, byte-exact sorter pass-through, enrolled diversion and two-recipient rewriting, body preservation, From-error retention, optional sorter log append and verbose output, sorter ERROR records, and continued processing after logging failure |
| Deterministic process-crash boundaries | 7 | Forced termination of a separate test worker at exact internal boundaries; production tagger watcher restart, exact retained file inventories/hashes, complete mapping reload/reuse, and successful fresh mail |
| Private actual-server samples | 3 | Five complete captured pairs parsed and classified; authenticated no-match and activated processing checked using temporary copies |
| **Total** | **396** | **All executed; none skipped** |

The three adversarial parser tests run **4,608 deterministic generated cases** internally: 3,072 arbitrary/mutated HDR and EML pairs, 1,024 valid generated EML messages, and 512 valid generated HDR messages. They check controlled contract failures and independently composed expected output bytes. These cases are included in the 87 parsing tests above, not counted as thousands of separate test-runner tests.

The actual-server sample tests use isolated temporary configuration and working copies. Original files stay unchanged. No captured identities, message bodies, or raw message files are included in shared fixtures, this report, or release packages. These captures establish compatibility with their observed byte forms; they do not establish the current server's deployment contract.

The executable crash test forcibly terminates the published tagger during construction of a 1,500-recipient fan-out after a child becomes pending. Restart reloads complete mappings, preserves the retained work without automatic replay, and processes a fresh message. Other tests verify that logging failures still allow the same current child to publish, while real contract or filesystem errors retain the documented evidence.

Seven additional crash tests terminate a separate test-only worker after HDR ownership, before mapping publication, immediately after mapping publication, after first-child readiness, between child EML/HDR publication, after all child HDRs are live but before parent cleanup, and between the two From-contract retention renames. Each restarts the unmodified production tagger assembly in watch mode. Tests check startup mapping-load events, unchanged retained bytes and locations, no automatic mail replay, and fresh delivery using the same recipient mappings. Internal observers and the child-stream failure seam have no selector in production arguments, environment, configuration, or message data; the worker and lab helper are excluded from release packages.

## Release packages

- [Standalone sorter EXE](https://github.com/bigjosh/sm-tagger2/releases/download/v1.0.0-rc.4/sm-sorter.exe)
- [Standalone tagger EXE](https://github.com/bigjosh/sm-tagger2/releases/download/v1.0.0-rc.4/sm-tagger.exe)
- [sm-sorter Windows x64 ZIP](artifacts/release/sm-sorter-win-x64.zip)
- [sm-tagger Windows x64 ZIP](artifacts/release/sm-tagger-win-x64.zip)

Both forms include their .NET runtime. The standalone EXEs need no adjacent DLLs; tests relocate only the EXEs before launching them with runtime-root overrides removed. For ZIP deployment, copy each complete application folder. Runtime license and third-party notices accompany both distribution forms. See [deployment.md](deployment.md#release-files) for hosting details.

The standalone tests also capture .NET host diagnostics: these Windows x64 .NET 10.0.11 bundles report self-contained execution with CoreCLR embedded in the native host, and leave their fresh runtime-extraction directories empty. The tests accept either that embedded runtime or extraction into the controlled directory when a future runtime pack requires it. Extraction behavior and the actual process identity remain part of the deployment hosting check.

The [release manifest](https://github.com/bigjosh/sm-tagger2/releases/download/v1.0.0-rc.4/manifest.json) is the authoritative SHA-256 inventory for the EXEs, runtime notices, complete application folders, and ZIPs.

Independent verification matched all **400 deployment files** and all **396 archived file entries** against the manifest and checked both package hashes. The inventory includes four application-folder runtime notice files and the two standalone EXEs with their shared notices. Release checks exclude sample messages, test assemblies, the crash worker, and lab helpers. The sorter carries no tagger-only mail or engine assemblies. The publish script recreates its generated output directories before building.

The exact production executable folders and standalone EXEs tested are the ones represented by this manifest. Republishing creates a new manifest and may change package hashes; record the new hashes when selecting deployment artifacts.

A live sorter trial exposed premature ownership of a provisional HDR before its final EML arrived. The corrected sorter discovers final EML files, reads the matching HDR, rejects upstream Failed status, and preserves EML-first/HDR-last publication. Regression tests reproduce the provisional-HDR and final-EML sequence with synthetic data. Additional isolated checks using private capture copies confirmed that an early HDR remains unopened/unclaimed without its EML, and that a copy deliberately marked Failed is retained and logged once. Hash verification confirmed all four originals unchanged. The later captured HDR has incomplete envelope-routing fields and was not replayed; these local checks do not establish recovery or delivery safety for that failed transaction.

## Remaining deployment evidence

Every external `VERSION-SENSITIVE-*` gate in [assumptions.md](assumptions.md) remains **PENDING**. All fourteen identifiers are also present beside dependent source behavior. The local validation commands do not connect to or modify the production mail queue, DNS configuration, accounts, or server.

The outstanding work requires the selected Windows Server and SmarterMail environment: queue readiness and spool admission, supported submission routes, newly generated tag acceptance, envelope/SPF and post-rewrite DKIM/DMARC behavior, catch-all receipt, supported clients and MDNs, resource limits, and the chosen process identity and hosting arrangement. Record exact product builds, configuration, and artifact hashes when those checks run.

Native filesystem notifications and real Windows sharing/move behavior were exercised locally. Watcher overflow/error and periodic-rescan paths were also exercised through narrow internal test seams; this does not claim a reproducible native buffer overflow on the deployment server. Orderly shutdown was tested at the queue boundary, but actual Ctrl+C/Ctrl+Break delivery from the chosen service wrapper or scheduler remains a deployment check. No disk exhaustion, machine reboot, or power-loss experiment was performed. Power-loss durability is outside the accepted version 1 contract.

The final-EML correction passes the local checks above. A corrected live trial is still required before production enablement, along with the remaining recorded deployment gates.
