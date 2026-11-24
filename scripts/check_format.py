"""Enforce formatting only on changed source; leave untouched history alone."""
import argparse
import os
import tempfile
from pathlib import Path
import subprocess
from check_repository import ROOT, changed_files, git

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--base", default="origin/main")
parser.add_argument("--staged", action="store_true")
args = parser.parse_args()
paths = changed_files(args.base, args.staged)
csharp = [name for name in paths if name.endswith(".cs")]
go = [name for name in paths if name.endswith(".go")]
if csharp:
    # Folder mode needs no private stack, project restore or execution of PR build targets.
    if args.staged:
        # Check exact staged bytes even when the working copy has unstaged edits.
        with tempfile.TemporaryDirectory(prefix="workbench-format-") as temporary:
            root = Path(temporary)
            (root / ".editorconfig").write_bytes(git("show", ":.editorconfig"))
            for name in csharp:
                destination = root / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(git("show", ":" + name))
            subprocess.run(["dotnet", "format", "whitespace", str(root), "--folder", "--verify-no-changes", "--include", *csharp], cwd=root, check=True)
    else:
        subprocess.run(["dotnet", "format", "whitespace", str(ROOT), "--folder", "--verify-no-changes", "--include", *csharp], cwd=ROOT, check=True)
if go:
    # Resolve go.mod's toolchain before choosing gofmt; do not use a stale PATH copy.
    toolchain = Path(subprocess.check_output(["go", "env", "GOROOT"], cwd=ROOT).decode().strip())
    gofmt = toolchain / "bin" / ("gofmt.exe" if os.name == "nt" else "gofmt")
    for name in go:
        content = git("show", ":" + name) if args.staged else (ROOT / name).read_bytes()
        result = subprocess.run([str(gofmt)], input=content, capture_output=True, check=True)
        if result.stdout != content:
            raise SystemExit("Run gofmt on changed file: " + name)
print("PASS: changed C# and Go formatting" if csharp or go else "PASS: no changed C#/Go files to format")
