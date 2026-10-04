import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import canary_state


class StateStorageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="canary-git-state-")
        self.root = Path(self.temporary.name)
        self.remote = self.root / "remote.git"
        self.repo = self.root / "runner"
        self.run_git("init", "--bare", "--quiet", str(self.remote))
        self.run_git("init", "--quiet", str(self.repo))
        self.run_git("-C", str(self.repo), "config", "user.name", "Fixture")
        self.run_git("-C", str(self.repo), "config", "user.email", "fixture@example.invalid")
        self.run_git("-C", str(self.repo), "remote", "add", "origin", str(self.remote))
        self.input = self.root / "input"
        self.output = self.root / "output"
        self.output.mkdir()
        self.lease = self.root / "lease.json"
        self.status = self.root / "status.json"
        self.make_snapshot()
        self.make_status("SKIP")

    def tearDown(self):
        self.temporary.cleanup()

    @staticmethod
    def run_git(*args):
        return subprocess.run(["git", *args], capture_output=True, check=True).stdout.decode().strip()

    def make_snapshot(self, value="first"):
        database = self.output / "canary.db"
        database.unlink(missing_ok=True)
        with sqlite3.connect(database) as connection:
            for table in canary_state.ALLOWED_TABLES:
                connection.execute(f'CREATE TABLE "{table}" (value TEXT)')
                connection.execute(f'INSERT INTO "{table}" VALUES (?)', (value,))
            connection.execute('CREATE TABLE "AppConfigurations" (value TEXT)')
        self.update_hash()

    def update_hash(self):
        (self.output / "manifest.json").write_text(json.dumps({"FormatVersion": 1,
            "Profile": {"RadikoStationId": "TOKYO", "RadiruAreaId": "130", "RadiruStationId": "r1"},
            "DatabaseSha256": hashlib.sha256((self.output / "canary.db").read_bytes()).hexdigest().upper()}))

    def make_status(self, incremental, result="PASS"):
        self.status.write_text(json.dumps({"Result": result, "Checks": [
            {"CheckId": "C020_INITIAL_DATABASE_SYNC", "Result": "PASS"},
            {"CheckId": "C021_INCREMENTAL_DATABASE_SYNC", "Result": incremental}]}))

    def restore(self, reset=False):
        canary_state.restore(self.repo, self.input, self.lease, reset)

    def publish(self):
        canary_state.publish(self.repo, self.output, self.lease, self.status)

    def state_head(self):
        return self.run_git("--git-dir", str(self.remote), "rev-parse", "refs/heads/canary-state")

    def test_初回保存と復元でDBと日本語コミットと作者を保持する(self):
        self.restore()
        self.assertEqual(json.loads(self.lease.read_text())["previous"], "")
        self.publish()
        self.restore()
        self.assertEqual((self.input / "canary.db").read_bytes(), (self.output / "canary.db").read_bytes())
        identity = self.run_git("--git-dir", str(self.remote), "show", "-s", "--format=%an <%ae> %cn <%ce>", self.state_head())
        self.assertEqual(identity, "Ovis <3971624+Ovis@users.noreply.github.com> Ovis <3971624+Ovis@users.noreply.github.com>")
        self.assertEqual(self.run_git("--git-dir", str(self.remote), "show", "-s", "--format=%s", self.state_head()), "Canaryの正常な局定義と番組表を保存する")

    def test_更新後も参照可能なGit履歴を一コミットに限定する(self):
        self.restore()
        self.publish()
        first = self.state_head()
        self.restore()
        self.make_snapshot("second")
        self.make_status("PASS")
        self.publish()
        self.assertNotEqual(first, self.state_head())
        self.assertEqual(self.run_git("--git-dir", str(self.remote), "rev-list", "--count", "canary-state"), "1")
        self.assertEqual(set(self.run_git("--git-dir", str(self.remote), "ls-tree", "--name-only", self.state_head()).splitlines()), canary_state.FILES)

    def test_競合した更新を拒否して他の実行のDBを上書きしない(self):
        self.restore()
        stale_lease = self.root / "stale.json"
        shutil.copyfile(self.lease, stale_lease)
        self.publish()
        successful_head = self.state_head()
        self.make_snapshot("concurrent-second")
        with self.assertRaises(RuntimeError):
            canary_state.publish(self.repo, self.output, stale_lease, self.status)
        self.assertEqual(self.state_head(), successful_head)

    def test_前回が存在するのに増分確認をスキップした実行を保存しない(self):
        self.restore()
        self.publish()
        previous = self.state_head()
        self.restore()
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual(self.state_head(), previous)

    def test_通常の失敗実行を保存しない(self):
        self.restore()
        self.make_status("FAIL", result="FAIL")
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual(self.run_git("--git-dir", str(self.remote), "for-each-ref", "refs/heads/canary-state"), "")

    def test_チェックサム不一致や資格情報を含むDBを保存しない(self):
        self.restore()
        with sqlite3.connect(self.output / "canary.db") as connection:
            connection.execute('INSERT INTO "AppConfigurations" VALUES (?)', ("offline-password",))
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.publish()
        self.update_hash()
        with self.assertRaisesRegex(ValueError, "outside"):
            self.publish()

    def test_リセットは取得時に前回を削除せず成功時だけ置き換える(self):
        self.restore()
        self.publish()
        previous = self.state_head()
        self.restore(reset=True)
        self.assertEqual(list(self.input.iterdir()), [])
        self.assertEqual(self.state_head(), previous)
        self.make_snapshot("reset")
        self.publish()
        self.assertNotEqual(self.state_head(), previous)

    def test_復元時の破損を初回扱いにせずleaseを発行しない(self):
        self.restore()
        self.publish()
        self.lease.unlink()
        manifest = json.loads((self.output / "manifest.json").read_text())
        manifest["DatabaseSha256"] = "invalid"
        blobs = []
        for name in sorted(canary_state.FILES):
            data = json.dumps(manifest).encode() if name == "manifest.json" else (self.output / name).read_bytes()
            blob = canary_state.git(self.repo, "hash-object", "-w", "--stdin", data=data).stdout.decode().strip()
            blobs.append(f"100644 blob {blob}\t{name}\n")
        tree = canary_state.git(self.repo, "mktree", data="".join(blobs).encode()).stdout.decode().strip()
        commit = canary_state.git(self.repo, "commit-tree", tree, data=b"corrupt fixture\n").stdout.decode().strip()
        self.run_git("-C", str(self.repo), "push", "origin", f"--force-with-lease=refs/heads/canary-state:{self.state_head()}", f"{commit}:refs/heads/canary-state")
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.restore()
        self.assertFalse(self.lease.exists())

    def test_通常の親ありブランチを状態保存先として上書きしない(self):
        self.restore()
        self.publish()
        previous = self.state_head()
        tree = self.run_git("-C", str(self.repo), "show", "-s", "--format=%T", previous)
        commit = canary_state.git(self.repo, "commit-tree", tree, "-p", previous, data=b"ordinary fixture\n").stdout.decode().strip()
        self.run_git("-C", str(self.repo), "push", "origin", f"{commit}:refs/heads/canary-state")
        with self.assertRaisesRegex(ValueError, "parentless"):
            self.restore()
        self.assertEqual(self.state_head(), commit)

    def test_公開失敗をstatusとログに反映する(self):
        canary_state.record_failure(self.status, self.root / "logs", "publish", "git push failed (exit 1)")
        status = json.loads(self.status.read_text())
        self.assertEqual(status["Result"], "FAIL")
        self.assertEqual(status["Checks"][-1]["ErrorCode"], "E-C023-PUBLISH")
        self.assertTrue((self.root / "logs" / "C023_STATE_STORAGE.log").exists())

    def test_main以外からの公開をCLIで拒否する(self):
        env = os.environ.copy()
        env.update(GITHUB_ACTIONS="true", GITHUB_REF="refs/heads/feature/test")
        process = subprocess.run([sys.executable, str(Path(canary_state.__file__)), "publish",
            "--repo-dir", str(self.repo), "--status-file", str(self.status), "--log-dir", str(self.root / "logs")],
            capture_output=True, env=env)
        self.assertEqual(process.returncode, 1)
        self.assertIn(b"only main", process.stderr)


if __name__ == "__main__":
    unittest.main()
