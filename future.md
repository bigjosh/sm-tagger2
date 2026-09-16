# Future Enhancements

Features in this document are intentionally excluded from the current implementation specification. They require a separate design decision before implementation.

## FUT-001 - Email-based control and status interface

Allow specially formated emails that have AUTH from the private sender to reply back with eg what address is assigned to a tag or a tags log file.

## FUT-002 — Automatic inbound tag processing

Recognize inbound messages addressed to allocated individual or group tag-addresses and automatically route, annotate, filter, or reject them according to sender policy. Version 1 deliberately relies on a dedicated per-user catch-all tag domain and performs no inbound transformation.

A future design must address at least:

- Exact lookup of SMTP envelope recipients against allocated tag records without trusting visible EML `To:` or `Cc:` headers.
- Routing both individual and group Reply-To tags back to the current mailbox for the owning sender-id; group tags must not fan out to the original outbound recipient group.
- Deterministic splitting of inbound messages containing mixtures of known tags, unallocated addresses, ordinary recipients, and tags owned by different sender-ids.
- Preservation of the received tag identity and its recipient-id mapping as reliable association metadata without exposing private-addresses or treating the mapping as proof of human culpability.
- Policies for active, leaked, blocked, retired, malformed, and unknown tags.
- DSNs, null reverse-path messages, repeated recipient metadata, delivery notifications, and routing-loop prevention.
- Atomic spool publication, partial-failure behavior, recovery, logging, monitoring, and tests based on byte-accurate inbound HDR/EML fixtures.
- Migration from manual catch-all collection without interrupting replies to previously allocated tags.

## FUT-003 — MIME-aware MDN rewriting

Parse and safely rewrite the MIME body and embedded content of outbound message-disposition notifications so a supported MDN may remove current/retired private-address occurrences instead of relying on client-specific evidence and `allow-mdn.txt` approval.

A future design must address at least:

- Complete standards-aware MIME parsing across boundaries, nesting, quoted parameters, transfer encodings, character sets, and malformed/ambiguous structures without parse-and-reserialize damage.
- The human-readable MDN part, every `message/disposition-notification` address field, and optional returned `message/rfc822` or header content.
- Exact distinctions among MDNs, DSNs, ordinary `multipart/report` messages, receipt requests, and nonstandard client/server responses.
- Byte-preserving edits where possible and explicit canonical serialization rules where decoded content must change.
- Recalculation/removal of affected MIME transfer metadata, content hashes, signatures, and lengths.
- Behavior for S/MIME, PGP/MIME, protected headers, DKIM-like pre-signing, nested signed content, and content that cannot be safely rewritten.
- Recipient-specific fan-out semantics, null reverse-paths, group tags, and prevention of private-address leakage through embedded original messages.
- A safe migration from the version 1 default-deny/evidence gate, including whether `allow-mdn.txt` remains an override or becomes a per-sender rewrite policy.

## FUT-004 — Native Windows service integration

Run `sm-sorter.exe` and `sm-tagger.exe` as coordinated but independently supervised Windows services instead of hosting the version 1 console watchers with Task Scheduler or external service wrappers.

A future design must address at least:

- Managed .NET Windows-service lifecycle integration consistent with the pure-C# implementation, including accurate pending/running/stopped status and exit-code reporting.
- Installation, upgrade, uninstall, recovery/restart, delayed-auto-start, service account, dependencies, and command-line/configuration storage.
- Stop, shutdown, preshutdown, pause/continue, and system-session behavior, including realistic SCM wait hints and forced-termination boundaries.
- Coordination between service controls, each watcher wait, in-flight sorter/tagger messages, their independent singleton handles, tagger log shutdown, restart ordering, and the no-automatic-recovery contract.
- Reliable capture and forwarding of the executable's existing standard-error and exit-status diagnostics when no interactive console is present, without requiring a duplicate Event Log or application notification channel.
- Least-privilege service-account setup and installation smoke tests for direct pass-through, sorter-to-tagger diversion, queued-mail isolation, and tagged fan-out.
- Compatibility with one-shot console mode for development and operator-directed intervention without allowing it to overlap the running service.

Until this feature is implemented, version 1 remains two console executables. Production may host each watch mode with Task Scheduler or an external service wrapper that supplies the configured arguments and restarts that process after a fatal exit according to local policy. A host capable of delivering `CTRL_C_EVENT` or `CTRL_BREAK_EVENT` can request orderly shutdown and must allow the current message time to finish; any other stop mechanism is a forced-termination crash under version 1.

## FUT-005 — External monitoring and operator notification

Build a separate monitoring/notification facility for both sorter and tagger. Version 1 deliberately ships no monitor or management endpoint and may be observed only through their exit statuses, standard error, and filesystem evidence.

Capture and deliver their standard-error messages through an out-of-band operational channel. Include log creation/open/write/flush/close failures even when the process continues and mail is delivered successfully, plus runtime contract errors such as rejected `From:` counts and their `.hdr.err`/`.eml.err` pairs. Version 1 logging failures do not hold mail or cause a failing exit status, so process-exit monitoring alone cannot report them. Failure of this future notification channel must not become a dependency for mail publication.

Prefer a read-only out-of-process monitor over adding a management surface to either mail process. It should supervise both process states and captured standard error; distinguish sorter failure (all new selected-proc flow at risk) from tagger failure (enrolled queue growth); inspect `.sort.err`, `.err`, staging, suffixed `proc\`/`process\` work, persistent half-pairs, both queue ages/counts, and free space without modifying them; distinguish expected `.in`/`.out` evidence from failures; deduplicate persistent conditions without hiding them; and deliver end-to-end-tested notification through the deployment's chosen operational system. It must report failure of its own collection or notification path.

A management port is a candidate only if it provides a material capability that cannot be obtained safely from those existing signals. Any endpoint design must define localhost versus remote binding, Windows identity/ACL or cryptographic authentication, confidentiality for protected addresses and paths, protocol/version compatibility, request and connection bounds, service lifecycle, firewall/installation behavior, and a strictly read-only authorization model. It must not add message retry, deletion, repair, or other recovery commands by accident. A local named pipe has a smaller network surface but still requires authenticated access, a client/monitor, protocol semantics, and lifecycle handling.

The future work must include the condition/severity matrix, liveness/readiness semantics, operational thresholds, alert reminder policy, credential/secret handling, failure injection, and proof that a real operator receives and can act on each notification. Until then, timely detection is not a version 1 guarantee.

## FUT-006 — Leaked private-address handling

Allow an operator to mark a private-address as leaked and identify inbound mail sent to it so the user's mail client can filter or quarantine those messages. A future design should define the persistent leaked/retired state, the exact inbound envelope-recipient source, the added header and its trust boundary, interaction with catch-all and automatic inbound tag processing, false-positive and recovery behavior, and migration for already queued mail.

Maybe this is via a interfce where the auth user can forward an email to a leaked tag to a special address an our system decodes it and marks the tag as leaked.
