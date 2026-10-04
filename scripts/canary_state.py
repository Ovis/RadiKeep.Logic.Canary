#!/usr/bin/env python3
"""局・番組表専用SQLiteを、最新スナップショットだけのOrphan Branchで受け渡す。"""

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys

BRANCH = "canary-state"
FILES = {"canary.db", "manifest.json"}
DATA_TABLES = {"RadikoStations", "NhkRadiruAreas", "NhkRadiruAreaServices", "RadikoPrograms", "NhkRadiruPrograms"}
ALLOWED_TABLES = DATA_TABLES | {"__EFMigrationsHistory"}


def git(repo, *args, data=None, env=None, allow_missing=False):
    result = subprocess.run(["git", "-C", str(repo), *args], input=data, capture_output=True, env=env)
    if result.returncode and not (allow_missing and result.returncode == 2):
        raise RuntimeError(f"git {args[0]} failed (exit {result.returncode})")
    return result


def validate_snapshot(directory):
    directory = Path(directory)
    if {file.name for file in directory.iterdir()} != FILES:
        raise ValueError("snapshot must contain only canary.db and manifest.json")
    database = directory / "canary.db"
    manifest = json.loads((directory / "manifest.json").read_text())
    if manifest.get("FormatVersion") != 1:
        raise ValueError("unsupported snapshot format")
    if hashlib.sha256(database.read_bytes()).hexdigest().upper() != manifest.get("DatabaseSha256"):
        raise ValueError("snapshot checksum mismatch")
    with sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True) as connection:
        if connection.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
            raise ValueError("snapshot integrity check failed")
        if connection.execute("PRAGMA foreign_key_check").fetchall():
            raise ValueError("snapshot foreign key check failed")
        tables = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")}
        if not ALLOWED_TABLES <= tables:
            raise ValueError("snapshot tables are missing")
        for table in tables:
            quoted = '"' + table.replace('"', '""') + '"'
            count = connection.execute(f"SELECT COUNT(*) FROM {quoted}").fetchone()[0]
            if table not in ALLOWED_TABLES and count:
                raise ValueError("snapshot contains data outside station definitions and programs")
            if table in DATA_TABLES and not count:
                raise ValueError("snapshot contains an empty data table")
    return manifest


def restore(repo, input_dir, lease_file, reset=False):
    input_dir = Path(input_dir)
    input_dir.mkdir(parents=True, exist_ok=True)
    if any(input_dir.iterdir()):
        raise ValueError("state input directory must be empty before restore")
    observed = git(repo, "ls-remote", "--exit-code", "--heads", "origin", f"refs/heads/{BRANCH}", allow_missing=True)
    previous = ""
    if observed.returncode == 0:
        git(repo, "fetch", "--no-tags", "--depth=1", "origin", f"refs/heads/{BRANCH}")
        previous = git(repo, "rev-parse", "FETCH_HEAD").stdout.decode().strip()
        header = git(repo, "cat-file", "-p", previous).stdout.split(b"\n\n", 1)[0]
        if any(line.startswith(b"parent ") for line in header.splitlines()):
            raise ValueError("state branch must contain a parentless commit")
        names = set(git(repo, "ls-tree", "--name-only", previous).stdout.decode().splitlines())
        if names != FILES:
            raise ValueError("state branch has unexpected files")
        if not reset:
            for name in FILES:
                (input_dir / name).write_bytes(git(repo, "show", f"{previous}:{name}").stdout)
            validate_snapshot(input_dir)
    lease_file = Path(lease_file)
    lease_file.parent.mkdir(parents=True, exist_ok=True)
    lease_file.write_text(json.dumps({"branch": BRANCH, "previous": previous, "reset": reset}))
    print("state baseline restored" if previous and not reset else "state baseline initialization requested")


def publish(repo, output_dir, lease_file, status_file):
    status = json.loads(Path(status_file).read_text())
    if status.get("Result") != "PASS":
        raise ValueError("only a successful canary run may publish a snapshot")
    lease = json.loads(Path(lease_file).read_text())
    if lease.get("branch") != BRANCH:
        raise ValueError("state lease branch mismatch")
    checks = status.get("Checks", [])
    initial = next((check for check in checks if check.get("CheckId") == "C020_INITIAL_DATABASE_SYNC"), {})
    incremental = next((check for check in checks if check.get("CheckId") == "C021_INCREMENTAL_DATABASE_SYNC"), {})
    if initial.get("Result") != "PASS" or any(check.get("Result") not in {"PASS", "SKIP"} for check in checks):
        raise ValueError("database synchronization did not pass")
    bootstrap = not lease["previous"] or lease.get("reset")
    if incremental.get("Result") != "PASS" and not (bootstrap and incremental.get("Result") == "SKIP"):
        raise ValueError("incremental synchronization did not pass")
    validate_snapshot(output_dir)
    entries = []
    for name in sorted(FILES):
        blob = git(repo, "hash-object", "-w", "--stdin", data=(Path(output_dir) / name).read_bytes()).stdout.decode().strip()
        entries.append(f"100644 blob {blob}\t{name}\n")
    tree = git(repo, "mktree", data="".join(entries).encode()).stdout.decode().strip()
    env = os.environ.copy()
    env.update(GIT_AUTHOR_NAME="Ovis", GIT_COMMITTER_NAME="Ovis",
               GIT_AUTHOR_EMAIL="3971624+Ovis@users.noreply.github.com",
               GIT_COMMITTER_EMAIL="3971624+Ovis@users.noreply.github.com")
    # 親を指定せず、SQLiteの旧版が参照可能なGit履歴として積み重ならないようにする。
    commit = git(repo, "commit-tree", tree, data="Canaryの正常な局定義と番組表を保存する\n".encode(), env=env).stdout.decode().strip()
    git(repo, "push", "origin", f"--force-with-lease=refs/heads/{BRANCH}:{lease['previous']}", f"{commit}:refs/heads/{BRANCH}")
    print(f"state snapshot published: {commit}")


def record_failure(status_file, log_dir, stage, error):
    path = Path(status_file)
    try:
        status = json.loads(path.read_text())
    except (OSError, ValueError):
        status = {"TimestampJst": datetime.now(timezone(timedelta(hours=9))).isoformat(), "Checks": []}
    status["Result"] = "FAIL"
    status["Message"] = f"Canary state {stage} failed; previous snapshot was not overwritten."
    status.setdefault("Checks", []).append({"CheckId": "C023_STATE_STORAGE", "Result": "FAIL",
        "Message": f"State {stage} failed: {error}", "ErrorCode": f"E-C023-{stage.upper()}"})
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(status, ensure_ascii=True, indent=2))
    directory = Path(log_dir)
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "C023_STATE_STORAGE.log").write_text(f"stage={stage}\nerror={error}\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=["restore", "publish"])
    parser.add_argument("--repo-dir", default=".")
    parser.add_argument("--input-dir", default="state/input")
    parser.add_argument("--output-dir", default="state/output")
    parser.add_argument("--lease-file", default="state/lease.json")
    parser.add_argument("--status-file", default="results/status.json")
    parser.add_argument("--log-dir", default="logs")
    parser.add_argument("--reset", action="store_true")
    args = parser.parse_args()
    try:
        if args.operation == "restore":
            restore(args.repo_dir, args.input_dir, args.lease_file, args.reset)
        else:
            if os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("GITHUB_REF") != "refs/heads/main":
                raise ValueError("only main may publish canary state")
            publish(args.repo_dir, args.output_dir, args.lease_file, args.status_file)
    except (OSError, ValueError, RuntimeError, sqlite3.Error) as error:
        record_failure(args.status_file, args.log_dir, args.operation, str(error))
        print(f"Canary state {args.operation} failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
