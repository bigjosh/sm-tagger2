# Configuration example

`datadir/` is a complete synthetic profile and auth index using reserved example domains. It contains no actual-server sample data and cannot be used as a production account configuration.

Before enabling a real sender, create a fresh stable lowercase UUID, replace the example profile directory and index pointer with that UUID, and set the authenticated address and a non-public private alias generated with cryptographically secure randomness. Rename the auth-index directory to the canonical lowercase authenticated address. Keep all current and retired addresses unique across every identity role and profile. Required retired-address files may be empty.

Provision the private alias as an authorized sending identity, choose a dedicated reserved tag domain with the required catch-all, and replace `from-template.txt`. The first whitespace-delimited token is the pattern and contains exactly one `%`; following notes are ignored. Templates are trusted to generate addresses within the deployed transport/provider limits. Keep `allow-mdn.txt` at `false` until its separate evidence gate passes.

Perform all profile/index edits while the tagger is stopped. Build an auth-index directory completely under `senders/.staging/` and publish it with a non-replacing same-volume directory move. Never delete an old auth index during rotation; record the old address as retired and retain the same sender UUID and mappings.

The tagger creates its `process/` queue when needed after startup validation. Mapping allocation creates `staging/` and `tag-addresses/`. Queue placement, same-volume NTFS transitions, SmarterMail routes, signing, catch-all delivery, and client compatibility must pass the deployment tests before these executables handle real mail. See [deployment.md](../deployment.md), [spec.md](../spec.md), and [assumptions.md](../assumptions.md).
