# Implementation working rules

Read `spec.md` as the behavioral authority, `implementation.md` as the implementation guide, and `testing-plan.md` for verification. `assumptions.md` separates local verification from deployment evidence; `future.md` remains out of scope.

Use pure managed C# targeting `net10.0`. Preserve message bytes outside the specified edits. Keep the sorter independent of tagger-only parsing, configuration, mapping, and logging code. Add a short responsibility comment before every method. Test real failure and retention boundaries without adding runtime-selectable fault controls or automatic recovery. Narrow internal test seams may exercise otherwise unreachable failures; production CLI, environment, configuration, and message data must never select them.

`samples/` contains private messages captured from an actual server. Keep originals unchanged, use them only locally, do not echo their identities or bodies into reports, and do not include them in source control or release artifacts. Shared fixtures must be separately synthesized using reserved example domains.

The approved SDK is pinned in `global.json`. Build with `dotnet build SmTagger.slnx -c Release`, test with `dotnet test SmTagger.slnx -c Release --no-build`, and check formatting with `dotnet format SmTagger.slnx --verify-no-changes --no-restore`. If the worker environment lacks `dotnet` on PATH, check the per-user installation at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` before installing another SDK.

Do not mark live SmarterMail, Windows Server, signing, SMTP, catch-all, or client compatibility gates verified from local fixtures alone. Record remaining deployment tests explicitly.
