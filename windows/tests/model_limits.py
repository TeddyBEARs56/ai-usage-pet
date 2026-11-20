"""Usage: python3 windows/tests/model_limits.py <AIUsageStatus executable or DLL>."""
import datetime as dt
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


if len(sys.argv) < 2:
    raise SystemExit(__doc__)
binary = Path(sys.argv.pop(1)).resolve()
command = ["dotnet", str(binary)] if binary.suffix == ".dll" else [str(binary)]
hour = 3_600_000
week = 168 * hour
start = 1_794_960_000_000
owner = "user|org"


def iso(ms):
    return dt.datetime.fromtimestamp(ms / 1000, dt.timezone.utc).isoformat()


def limit(used, reset=None):
    return {"used_percentage": used, "resets_at": reset / 1000 if reset else 0}


class ModelLimits(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.home = Path(temp.name)
        self.cache = self.home / ".cache" / "ai-usage"
        (self.cache / "claude").mkdir(parents=True)
        (self.home / ".claude.json").write_text(json.dumps(
            {"oauthAccount": {"accountUuid": "user", "organizationUuid": "org"}}))

    def cache_file(self, content, at=start, name="usage-command.json"):
        path = self.cache / "claude" / name
        path.write_text(json.dumps(content))
        os.utime(path, ns=(at * 1_000_000, at * 1_000_000))
        return path

    def cli(self, *args, now=start):
        result = subprocess.check_output([*command, *args, "--now", iso(now)],
            env={**os.environ, "AIUSAGE_HOME": str(self.home), "AIUSAGE_LANG": "ko"}, encoding="utf-8")
        return json.loads(result)

    def alerts(self, models=True, enabled=True, now=start):
        return self.cli("--alerts-once", "--limit-alerts", str(int(enabled)),
                        "--model-alerts", str(int(models)), now=now)

    def history(self):
        return [json.loads(line) for line in (self.cache / "history.jsonl").read_text().splitlines()]

    def test_enable_restart_history_and_trend(self):
        self.cache_file({"account": owner, "seven_day": limit(20, start + week),
                         "models": {"Fable": limit(95, start + week)}})
        self.assertEqual(self.cli("--alerts-once"), [])
        self.assertFalse((self.cache / "history.jsonl").exists())
        events = self.alerts()
        self.assertEqual([e["title"] for e in events], ["Claude Fable 한도 5% 남음"])
        self.assertEqual(self.alerts(), [])
        states = json.loads((self.cache / "alerts.json").read_text())["windows"]
        self.assertEqual(len(states), 2)
        self.assertEqual(next(v["low"] for k, v in states.items() if k.endswith("/model/RmFibGU")), "sent")
        # SourceScan first, then persisted live history plus a second scan.
        self.assertEqual(self.cli("--trend-json")["claude"]["week"]["used"], 20)
        self.assertEqual(self.cli("--record-once"), 1)
        self.assertEqual(self.cli("--record-once"), 0)
        self.assertEqual([(r["source"], r["used"]) for r in self.history()], [("claude-usage", 20)])
        self.assertEqual(self.cli("--trend-json")["claude"]["week"]["used"], 20)

    def test_cancellation_and_deterministic_order(self):
        reset = start + 3 * hour
        self.cache_file({"account": owner, "five_hour": limit(91, reset), "seven_day": limit(92, reset),
                         "models": {"Z/2": limit(96, reset), "Fable": limit(95, reset)}})
        events = self.alerts()
        self.assertEqual([(e["minutes"], e["model"]) for e in events],
                         [(300, None), (10080, None), (10080, "Fable"), (10080, "Z/2")])
        self.assertEqual(self.alerts(models=False), [])
        self.assertEqual(self.alerts(), [])
        states = json.loads((self.cache / "alerts.json").read_text())["windows"]
        self.assertTrue(all(v["pending"] == ("/model/" not in k) for k, v in states.items()))
        self.assertEqual([(e["kind"], e["model"]) for e in self.alerts(now=reset)],
                         [("reset", None), ("reset", None)])

    def test_hidden_or_missing_model_reset(self):
        reset = start + 3 * hour
        content = {"account": owner, "models": {"Fable": limit(95, reset)}}
        path = self.cache_file(content)
        self.assertEqual(len(self.alerts()), 1)
        self.assertEqual(self.cli("--record-once"), 0)
        self.assertEqual(self.history(), [])
        self.assertIsNone(self.cli("--trend-json")["claude"]["week"])
        self.assertEqual(self.alerts(now=start + hour + 1), [])
        path.unlink()
        self.assertEqual(self.alerts(now=start + 2 * hour), [])
        self.cache_file(content, start + 2 * hour)
        self.assertEqual(self.alerts(now=start + 2 * hour), [])
        path.unlink()
        self.assertEqual([e["title"] for e in self.alerts(now=reset + hour)],
                         ["Claude Fable 한도가 초기화됐습니다"])
        self.assertEqual(self.alerts(now=reset + hour), [])

    def test_late_reset(self):
        self.cache_file({"account": owner, "models": {"Fable": limit(95, start + hour)}})
        self.assertEqual(len(self.alerts()), 1)
        self.assertEqual(self.alerts(now=start + 2 * hour + 1), [])
        states = json.loads((self.cache / "alerts.json").read_text())["windows"]
        self.assertEqual(next(iter(states.values()))["reset"], "skipped")

    def test_parent_off_cancels_all_pending(self):
        self.cache_file({"account": owner, "seven_day": limit(92, start + hour),
                         "models": {"Fable": limit(95, start + hour)}})
        self.assertEqual(len(self.alerts()), 2)
        self.assertEqual(self.alerts(enabled=False), [])
        self.assertEqual(self.alerts(), [])
        self.assertEqual(self.alerts(now=start + hour), [])
        states = json.loads((self.cache / "alerts.json").read_text())["windows"]
        self.assertTrue(all(not w["pending"] and w["reset"] == "cancelled" for w in states.values()))

    def test_command_reset_range_and_unknown_rearm(self):
        too_far = start + week + 1
        self.cache_file({"account": owner, "seven_day": limit(93, too_far),
                         "models": {"Fable": limit(95, too_far), "Exact": limit(95, too_far - 1)}})
        notices = self.alerts()
        self.assertEqual([(e["model"], e["body"] is None) for e in notices],
                         [(None, True), ("Exact", False), ("Fable", True)])
        self.assertTrue(self.cli("--trend-json")["claude"]["days"][-1]["rough"])
        self.assertEqual(self.cli("--record-once"), 1)
        row, = self.history()
        self.assertEqual((row["resetMs"], row["quality"]), (None, "rough"))
        content = {"account": owner, "models": {"Fable": limit(95)}}
        self.cache_file(content, start + week - 1)
        self.assertEqual(self.alerts(now=start + week - 1), [])
        self.cache_file(content, start + week)
        self.assertEqual([(e["kind"], e["model"]) for e in self.alerts(now=start + week)],
                         [("reset", "Exact"), ("low", "Fable")])

    def test_account_filename_and_future_observation(self):
        content = {"seven_day": limit(95, start + week), "models": {"Fable": limit(95)}}
        for tag in ({}, {"account": "other|org"}, {"account": owner}):
            self.cache_file({**content, **tag}, start + 1 if tag.get("account") == owner else start)
            self.assertEqual(self.alerts(), [])
            self.assertEqual(self.cli("--record-once"), 0)
            self.assertIsNone(self.cli("--trend-json")["claude"]["week"])
        (self.home / ".claude.json").unlink()
        self.assertEqual(self.alerts(now=start + 1), [])

    def test_session_ignores_models_and_keeps_long_reset(self):
        reset = start + week + 1
        self.cache_file({"source": "usage", "account": owner, "seven_day": limit(94, reset),
                         "models": {"Fable": limit(99)}}, name="session.json")
        self.assertEqual([(e["scope"], e["body"] is None) for e in self.alerts()], [("overall", False)])
        self.assertEqual(self.cli("--record-once"), 1)
        row, = self.history()
        self.assertEqual((row["source"], row["resetMs"], row["quality"]), ("claude-session", reset, "observed"))

    def test_model_learns_reset_after_display_expires(self):
        self.cache_file({"account": owner, "models": {"Fable": limit(95, start + week + 1)}})
        event, = self.alerts(now=start + hour + 1)
        self.assertIsNone(event["body"])
        reset = start + 3 * hour
        self.cache_file({"account": owner, "models": {"Fable": limit(95, reset)}}, start + 2 * hour)
        self.assertEqual(self.alerts(now=start + 2 * hour), [])
        self.assertEqual([(e["kind"], e["model"]) for e in self.alerts(now=reset)], [("reset", "Fable")])

    def test_delayed_model_cannot_restore_an_older_generation(self):
        self.cache_file({"account": owner, "models": {"Fable": limit(95, start + week)}})
        self.assertEqual(len(self.alerts()), 1)
        before = (self.cache / "alerts.json").read_bytes()
        self.cache_file({"account": owner, "models": {"Fable": limit(98, start + hour)}}, start - hour)
        self.assertEqual(self.alerts(), [])
        self.assertEqual((self.cache / "alerts.json").read_bytes(), before)

    def test_desktop_and_codex_rollout(self):
        desktop = self.home / "AppData/Local/Claude-Data/claude_test"
        desktop.mkdir(parents=True)
        (desktop / "plan-usage-history.json").write_text(json.dumps({"samples": [
            {"org": "org", "t": start, "u": {"sd": 95}}, {"org": "other", "t": start, "u": {"sd": 100}}]}))
        codex = self.home / ".codex"
        sessions = codex / "sessions" / dt.datetime.fromtimestamp(start / 1000).strftime("%Y/%m/%d")
        sessions.mkdir(parents=True)
        (codex / "auth.json").write_text('{"tokens":{"account_id":"test"}}')
        header = {"type": "session_meta", "payload": {"creator_account_id": "test"}}
        rollout = sessions / "rollout-test.jsonl"
        def write(slot, at):
            line = {"timestamp": iso(at), "payload": {"rate_limits": {slot: {
                "used_percent": 96, "window_minutes": 10080, "resets_at": (start + week) // 1000}}}}
            rollout.write_text(json.dumps(header) + "\n" + json.dumps(line) + "\n")
        write("primary", start)
        self.assertEqual([e["provider"] for e in self.alerts()], ["claude", "codex"])
        write("secondary", start + 1)
        self.assertEqual(self.alerts(now=start + 1), [])
        self.assertEqual(self.cli("--record-once", now=start + 1), 2)
        self.assertEqual({r["source"] for r in self.history()}, {"claude-desktop", "codex-rollout"})

    def test_storage_failure(self):
        self.cache_file({"account": owner, "models": {"Fable": limit(95)}})
        target = self.cache / "alerts.json"
        target.mkdir()
        with self.assertRaises(subprocess.CalledProcessError) as failure:
            self.alerts()
        self.assertEqual(failure.exception.stdout, "")
        target.rmdir()
        self.assertEqual(len(self.alerts()), 1)

    @unittest.skipIf(os.name == "nt" or getattr(os, "geteuid", lambda: 0)() == 0, "requires POSIX permission checks")
    def test_failed_state_write_emits_nothing_and_retries(self):
        self.cache_file({"account": owner, "models": {"Fable": limit(95)}})
        self.cache.chmod(0o555)
        try:
            with self.assertRaises(subprocess.CalledProcessError) as failure:
                self.alerts()
            self.assertEqual(failure.exception.stdout, "")
            self.assertFalse((self.cache / "alerts.json").exists())
        finally:
            self.cache.chmod(0o755)
        self.assertEqual(len(self.alerts()), 1)
        self.assertEqual(self.alerts(), [])

    @unittest.skipIf(os.name == "nt", "creating symlinks may require elevated privileges")
    def test_link_escape_rejected_before_read(self):
        (self.cache / "alerts.json").symlink_to(self.home.parent / "outside-test-alerts.json")
        with self.assertRaises(subprocess.CalledProcessError):
            self.alerts()
        self.assertFalse((self.cache / "accounts.json").exists())


if __name__ == "__main__":
    unittest.main()
