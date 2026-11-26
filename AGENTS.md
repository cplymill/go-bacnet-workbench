# Workbench agent instructions

Read and follow [CONTRIBUTING.md](CONTRIBUTING.md) before any changes. It is the
authoritative Git workflow for this repository. Use a short-lived `codex/` branch,
small conventional commits and an owner-approved PR. Never push directly to
`main`, bypass required checks or move/delete published release tags.

This repository owns the desktop interfaces, stdio adapter, tests and packaging
definitions. The Go BACnet Stack implementation is a separate proprietary
dependency. Reuse its public APIs and compiled engine; never copy its source or
private Git history into this public repository. Preserve the Apache application
and proprietary-engine license split.

Read [the product specification](docs/SPECIFICATION.md) and relevant source/tests
before changing behavior. Preserve existing workspace/baseline formats and
unrelated working-tree changes. Use the toolchain directive in `go.mod`, never
`GOTOOLCHAIN=local`. Validate focused affected behavior, formatting and packaging
as appropriate. No device writes, lab changes, app closures or installations are
implied by build/test work. Mac is an untested preview until evidence changes.

Keep secrets, site data, captures, licensed standards and generated build output
out of Git. Record release changes in `CHANGELOG.md` and release notes. Do not
claim a task is merged, protected, signed or tested without checking that state.
