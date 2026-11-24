# Contribution and Git workflow

This is the authoritative workflow for Go BACnet Workbench, including human and
agent contributions. Keep these rules in Git so every clone and review sees the
same policy. `AGENTS.md` directs coding agents here. GitHub protection and CI
enforce the checks; Markdown alone cannot protect a branch.

## Daily work

1. Start from current `origin/main` and create a short-lived branch. Agents use
   `codex/<topic>`; other contributors may use `feat/<topic>` or `fix/<topic>`.
2. Make small, atomic commits. Explain the reason in the commit body when the
   subject alone is insufficient. Use Conventional Commit subjects, for example
   `feat: compare saved command priorities` or `fix: retain baseline on read failure`.
   Accepted types are `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`,
   `build`, `ci`, `chore` and `revert`; optional scopes and `!` are allowed.
3. Run focused tests and the checks below. Do not add commented-out code, debug
   output, unrelated cleanup or generated build output. Explain useful comments;
   delete obsolete code instead of leaving it commented out.
4. Open a PR with a conventional title, the problem, resulting behavior, why the
   change is needed, validation and material limits. Address review conversations.
5. Keep the branch current by rebasing onto `origin/main`. Coordinate before
   rewriting a shared branch; use `--force-with-lease`, never an unqualified force
   push. Never rewrite `main` or a published tag.
6. Merge through GitHub using squash merge after required checks and an
   independent approval. The squash subject comes from the conventional PR title.
7. Delete merged branches promptly. GitHub deletes merged remote heads
   automatically; fetch with pruning and remove merged local branches yourself.

`main` requires current passing **Repository policy** and **Windows application
checks**, one approval, resolution of conversations and approval after the last
push. Stale approvals are dismissed. Administrators are subject to protection;
force pushes and branch deletion are forbidden. Squash-only merging keeps main
linear. A PR author cannot approve their own PR: an independent reviewer with
the appropriate repository access is required. Do not lower protection or add a
bypass merely to merge an agent-authored change.

## Local setup and checks

Install Python 3 and the .NET 10 SDK. Install the repository's optional local
hooks once per clone:

```powershell
./scripts/install-hooks.ps1
```

The hooks check staged content, conventional commit subjects, changed C#
formatting, direct-main pushes and new release tags. Hooks supplement mandatory
GitHub checks; they can be skipped locally and are not a security boundary.

```powershell
python scripts/check_repository.py
python scripts/check_repository.py --base origin/main
python scripts/check_format.py --base origin/main
python -m unittest discover -s scripts/tests
dotnet run --project desktop/checks/DesktopChecks.csproj -c Release
dotnet run --project desktop/CrossPlatform.Checks/Workbench.CrossPlatform.Checks.csproj -c Release
```

C# formatting follows `.editorconfig` and is enforced on added/modified C# files
with `dotnet format whitespace --folder`. Existing untouched files are not
mass-reformatted. Fix a changed file with
`dotnet format whitespace . --folder --include path/to/file.cs`.
Changed Go files must pass `gofmt -l`; ordinary C# builds treat warnings as errors.
Adapter changes also need focused Go tests/vet with the licensed stack dependency
and the repository-resolved Go toolchain. Public CI never checks out the private
stack or exposes its credentials to pull requests.

## Files, secrets and licensing

Keep the root useful: README with installation/run/build instructions, Apache
license, proprietary engine license and notices, this guide, agent instructions,
changelog and CI/configuration. Put product guides in `docs/` and tools in `scripts/`.

Never commit credentials, private keys, `.env` files, site workspaces/captures,
licensed standards or proprietary stack implementation source. Keep compiled
engines, installers, archives and build output out of Git; upload approved binary
packages as release assets. The repository guard rejects known credential
patterns, forbidden file paths and files larger than 2 MiB. GitHub secret scanning
and push protection supplement this check; no scanner detects every possible
secret. If a real secret is exposed, revoke/rotate it and report it privately;
deleting the current file does not remove it from history.

Do not commit generated build artifacts. Intentional reviewable source assets
such as approved artwork, dependency lockfiles and the existing generated
read-only classification table are exceptions: retain their provenance and
review changes. Do not remove lockfiles under a blanket “generated files” rule.
Discuss a necessary new large asset or exception in a PR before changing the guard.

App source is Apache-2.0. The compiled engine has separate terms. A contribution
does not authorize copying the private stack, bundling site data, modifying a
controller or making new protocol-certification claims.

## Releases

1. Merge the release change through a reviewed PR. Update the application version,
   a dated `CHANGELOG.md` entry, specification and validation evidence as applicable.
2. Build and validate packages from a clean checkout of the **exact merged commit**.
   Record that commit and artifact checksums. Do not build from uncommitted changes
   or tag a different commit afterward. Keep private build provenance private.
3. Create an annotated SemVer tag, for example `git tag -a v0.2.22 <commit> -m
   "Workbench 0.2.22: describe the change"`, and push that tag. Release CI verifies
   annotation, SemVer, main ancestry and the corresponding changelog entry.
4. Publish GitHub Release notes and verified packages with checksums. Keep Windows
   primary and label Mac builds untested until Mac runtime validation exists.
5. **Never move or delete a published tag.** Fix a release with a new version.
   GitHub tag rules prohibit tag updates and deletions. Historical 0.2.20/0.2.21
   releases predate this policy; do not rewrite them to make history look compliant.

Commit/tag signing is encouraged when the project or recipient requires it.
Configure an owner-controlled signing key first, then use `git commit -S` and
`git tag -s`. Never invent a signing identity, commit a private signing key or
enable a required-signatures gate before maintainers have working signing keys.
Signing is currently optional; readable, reviewed linear history is mandatory.

## Administration

[GitHub policy configuration](.github/repository-settings.json) records the desired
repository, main-branch and release-tag settings. GitHub stores and enforces the
live settings independently; committing this file does not apply them. An
authorized maintainer must apply changes through GitHub and verify the result.
Policy/configuration changes follow the same reviewed PR process. Do not silently
weaken checks to obtain a green build.
