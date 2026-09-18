# Positive Written-status gate — 2026-09-17

The approved local **1.0.0-rc.7** contract requires a positive `Written` HDR status before the sorter classifies or routes a message. This replaces the earlier policy that rejected `Failed` and treated every other status as opaque. Final-EML discovery, writer exclusion during the HDR read, and the rc.6 failed-folder retention policy remain in place. The public release remains rc.4.

## Contract

The sorter checks final EML attributes before any matching HDR access and never reads EML contents. It reads HDR with `File.ReadAllBytes`, using read-only access and `FileShare.Read` to exclude active writers. The read handle closes before ownership. Comparison uses the first CRLF-terminated line, case-sensitively, trimming only trailing ASCII SP/HTAB; original bytes remain unchanged.

| Status | Result |
|---|---|
| Exact `Written` | Continue existing HDR classification/enrollment, then claim and route; malformed remaining HDR data still holds with `UNSAFE_HDR`. |
| Exact `Failed` | Unchanged `UPSTREAM_FAILED` ownership and external failed-folder retention; no auth lookup. |
| Exact `Writing` | `Deferred` with `HDR_WRITING`; no watch-mode stderr. |
| Everything else, including `Quarantined`, `Ready`, wrong case, empty status, or missing first CRLF | `Deferred` with `HDR_STATUS_UNEXPECTED`; report unexpected status to stderr on every encounter in either mode, regardless of flags. |

Deferral performs no auth lookup and leaves names and bytes untouched: no `.hdr.sort`, `.sort.err`, or terminal email-log record. With `-v`, it emits `DEFER`. One-shot prints `NOT READY` and returns 1 without waiting; watch mode continues and reconsiders inputs through its normal notifications/rescans. There is no status-warning deduplication state or automatic promotion. Unexpected statuses may remain indefinitely and require manual investigation; the sorter never changes status bytes to make an input eligible.

The tagger's HDR trigger and our EML-first/HDR-last publication remain unchanged. The full authority is [spec.md §7.3.1](spec.md#731-sorter-classification-and-handoff).

## Evidence and limits

Saved successful SmarterMail build 9742 originals from SMTP and immediate, delayed, and scheduled REST paths have `Written ` on the first line. Saved failed SMTP inputs have `Failed `. The historical [sequencing report](live-sequencing-2026-09-16.md), [broader live suite](live-smartermail-2026-09-16.md), and [failed-retention report](failed-retention-2026-09-16.md) retain the original observations and test counts. A [SmarterTools developer explanation](https://portal.smartertools.com/community/a96224/archive-and-spool-overview.aspx) recognizes `Quarantined`; this is not evidence that it occurs in Proc. `Ready` was not observed as a successful final status in these saved examples.

These are bounded observations, not a guarantee for every producer route or future build. A successful read excludes an active incompatible writer but cannot prevent a later producer reopen after the handle closes. `VERSION-SENSITIVE-001` still requires evidence that final EML plus a successfully read `Written` HDR means both files are complete and will not be edited later. Wider Windows Server, client, routing, signing, and deployment gates remain pending.

The new rc.7 standalone sorter passed **seven offline checks using saved synthetic SM-produced captures**: one SMTP control, a traced immediate REST input, an immediate-alias REST input, delayed and scheduled REST inputs, a post-250 disconnect failure, and an aborted DATA failure. Five `Written` cases passed unchanged; both `Failed` cases retained unchanged bytes under the existing policy. The originals and checked outputs matched their byte hashes. This was isolated offline replay, with **no new SM submissions or live deliveries**, and no previously held production/private message was requeued.

The rc.7 Release build passed with zero warnings/errors, formatting verification passed, and the full suite passed **482 tests, zero failures and zero skips**. The new gate tests cover exact status matching, untouched deferrals, later status transitions with fresh routing, diagnostic failure, and continued watcher processing. Real executable checks include unexpected-status reporting without verbose output. See [full automated results](artifacts/test-results/written-readiness/written-readiness-full.trx) and [validation-report.md](validation-report.md).

Independent package checks verified **400 deployment files and 396 ZIP entries** for rc.7. See [capture and artifact audit](artifacts/written-readiness-20260917/capture-and-artifact-audit.json) and the [preserved rc.7 package manifest](artifacts/sender-layout-20260917/prior-rc7/manifest.json). The later [rc.8 sender-layout change](sender-layout-2026-09-17.md) preserves this readiness policy. The earlier rc.6 artifacts are preserved under `artifacts/written-readiness-20260917/prior-rc6/`; its historical 455-test result is kept separately.

After a producer upgrade or unexpected-status diagnostic, retain the observed status, exact build/route, and protected source evidence when the files are quiescent. Investigate the producer contract without changing status bytes or replaying retained failures. Keep the required regression and deployment checks in [testing-plan.md](testing-plan.md) separate from historical evidence.
