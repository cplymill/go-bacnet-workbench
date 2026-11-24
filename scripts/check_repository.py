"""Check the Git index, conventional subjects and immutable annotated releases."""
import argparse
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
MAXIMUM_BYTES = 2 * 1024 * 1024
ROOT_FILES = {
    "README.md", "CONTRIBUTING.md", "AGENTS.md", "CHANGELOG.md", "LICENSE.txt",
    "ENGINE-LICENSE.txt", "NOTICE", ".gitattributes", ".gitignore", ".editorconfig",
    "go.mod", "go.sum",
}
ROOT_DIRECTORIES = {".github", ".githooks", "cmd", "desktop", "docs", "scripts"}
FORBIDDEN_SUFFIXES = {
    ".exe", ".dll", ".so", ".dylib", ".a", ".lib", ".pdb", ".msi", ".zip",
    ".7z", ".tar", ".gz", ".pem", ".key", ".pfx", ".p12", ".pcap", ".pcapng",
}
SUBJECT = re.compile(r"^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-zA-Z0-9_.-]+\))?!?: \S.*$")
SEMVER = re.compile(r"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")
SECRET_PATTERNS = [
    re.compile(rb"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----"),
    re.compile(rb"\bgh[pousr]_[A-Za-z0-9]{30,}\b"),
    re.compile(rb"\bgithub_pat_[A-Za-z0-9_]{30,}\b"),
    re.compile(rb"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
]


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


def subject_error(subject):
    if not SUBJECT.fullmatch(subject) or len(subject) > 100:
        return "Use a conventional subject of at most 100 characters, e.g. 'fix: retain canceled baseline'."
    return None


def file_error(name, content):
    path = PurePosixPath(name)
    parts = path.parts
    if not parts or (len(parts) == 1 and name not in ROOT_FILES) or (len(parts) > 1 and parts[0] not in ROOT_DIRECTORIES):
        return "Unapproved repository root entry; discuss and update the policy in the PR."
    if any(part.lower() in {"bin", "obj", ".tools", ".vs", "__pycache__"} or part.endswith((".app", ".dSYM")) for part in parts):
        return "Generated/build output belongs outside Git."
    if path.suffix.lower() in FORBIDDEN_SUFFIXES or name.endswith(".bacnet.json"):
        return "Binary, credential, capture or site workspace file is forbidden."
    if path.name.startswith(".env") and path.name != ".env.example":
        return "Environment secrets must not be tracked."
    if len(content) > MAXIMUM_BYTES:
        return "File exceeds the 2 MiB source asset limit; use release assets."
    if any(pattern.search(content) for pattern in SECRET_PATTERNS):
        return "Possible credential/private key detected; remove and revoke any real secret."
    return None


def changed_files(base=None, staged=False):
    args = ["diff", "--name-only", "--diff-filter=ACMR", "-z"]
    if staged:
        args.append("--cached")
    else:
        reference = base if base and set(base) != {"0"} else "origin/main"
        args.append(reference + "...HEAD")
    return [path.decode("utf-8") for path in git(*args).split(b"\0") if path]


def check_index():
    errors = []
    for entry in git("ls-files", "--stage", "-z").split(b"\0"):
        if not entry:
            continue
        metadata, encoded_name = entry.split(b"\t", 1)
        mode, oid, stage = metadata.decode().split()
        name = encoded_name.decode("utf-8")
        if stage != "0" or mode not in {"100644", "100755"}:
            errors.append(f"{name}: unresolved index entry or unsupported file mode")
            continue
        size = int(git("cat-file", "-s", oid).strip())
        if size > MAXIMUM_BYTES:
            errors.append(f"{name}: exceeds 2 MiB; use release assets")
            continue
        error = file_error(name, git("cat-file", "blob", oid))
        if error:
            errors.append(f"{name}: {error}")
    return errors


def verify_release_tag(tag):
    if not SEMVER.fullmatch(tag):
        raise ValueError("Release tags must be SemVer, e.g. v0.2.22.")
    version = tag[1:]
    if "-" in version:
        prerelease = version.split("-", 1)[1].split("+", 1)[0]
        if any(part.isdigit() and len(part) > 1 and part.startswith("0") for part in prerelease.split(".")):
            raise ValueError("Numeric prerelease identifiers cannot have leading zeroes.")
    reference = "refs/tags/" + tag
    if git("cat-file", "-t", reference).strip() != b"tag":
        raise ValueError("Release tags must be annotated (signing is optional).")
    commit = git("rev-parse", reference + "^{commit}").decode().strip()
    subprocess.run(["git", "merge-base", "--is-ancestor", commit, "origin/main"], cwd=ROOT, check=True)
    changelog = git("show", commit + ":CHANGELOG.md").decode("utf-8")
    if not re.search(r"^## \[" + re.escape(version) + r"\] - \d{4}-\d{2}-\d{2}$", changelog, re.MULTILINE):
        raise ValueError("The tagged commit needs a dated changelog entry for this exact version.")


def check_push(lines):
    for line in lines:
        local_ref, local_oid, remote_ref, remote_oid = line.split()
        if remote_ref == "refs/heads/main":
            raise ValueError("Direct main pushes are forbidden. Push a feature branch and open a reviewed PR.")
        if remote_ref.startswith("refs/tags/v"):
            if set(local_oid) == {"0"} or set(remote_oid) != {"0"}:
                raise ValueError("Published release tags must not be moved or deleted.")
            verify_release_tag(remote_ref.removeprefix("refs/tags/"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base")
    parser.add_argument("--staged", action="store_true")
    parser.add_argument("--commit-message", type=Path)
    parser.add_argument("--pre-push", action="store_true")
    parser.add_argument("--release-tag")
    args = parser.parse_args()
    if args.commit_message:
        error = subject_error(args.commit_message.read_text(encoding="utf-8-sig").splitlines()[0])
        if error:
            raise ValueError(error)
        return
    if args.pre_push:
        check_push(sys.stdin)
        return
    if args.release_tag:
        verify_release_tag(args.release_tag)
        print("PASS: annotated SemVer release tag, main ancestry and changelog")
        return
    errors = check_index()
    title = os.environ.get("PR_TITLE", "")
    if title and subject_error(title):
        errors.append("PR title: " + subject_error(title))
    if args.base:
        reference = args.base if set(args.base) != {"0"} else "origin/main"
        # GitHub's PR checkout can add a synthetic merge commit; validate authored commits.
        for subject in git("log", reference + "..HEAD", "--no-merges", "--format=%s").decode().splitlines():
            if subject_error(subject):
                errors.append("New commit subject: " + subject_error(subject))
        subprocess.run(["git", "diff", "--check", reference + "...HEAD"], cwd=ROOT, check=True)
    if args.staged:
        subprocess.run(["git", "diff", "--cached", "--check"], cwd=ROOT, check=True)
    if errors:
        raise ValueError("\n".join(errors))
    print("PASS: tracked/index content, asset limits, credential patterns and applicable subjects/whitespace")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
