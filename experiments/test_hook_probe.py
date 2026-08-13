from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from hook_probe import main, sanitize_hook_payload


class HookPayloadTests(unittest.TestCase):
    def test_user_prompt_payload_is_redacted(self) -> None:
        raw = json.dumps(
            {
                "session_id": "session-secret",
                "turn_id": "turn-secret",
                "cwd": r"C:\private\project",
                "hook_event_name": "UserPromptSubmit",
                "prompt": "private prompt body",
                "model": "private-model",
            }
        )

        record = sanitize_hook_payload(raw)
        encoded = json.dumps(record, ensure_ascii=False)

        self.assertEqual(record["event_type"], "UserPromptSubmit")
        self.assertTrue(record["session_key"].startswith("sha256:"))
        self.assertTrue(record["turn_key"].startswith("sha256:"))
        self.assertTrue(record["project_key"].startswith("sha256:"))
        self.assertIn("prompt", record["top_level_keys"])
        self.assertIn("prompt", record["redacted_field_names"])
        self.assertNotIn("session-secret", encoded)
        self.assertNotIn("turn-secret", encoded)
        self.assertNotIn("C:\\private\\project", encoded)
        self.assertNotIn("private prompt body", encoded)
        self.assertNotIn("private-model", encoded)

    def test_permission_payload_keeps_only_safe_metadata(self) -> None:
        record = sanitize_hook_payload(
            json.dumps(
                {
                    "session_id": "session-1",
                    "turn_id": "turn-1",
                    "hook_event_name": "PermissionRequest",
                    "tool_name": "shell",
                    "tool_input": {"command": "Get-Content secret.txt"},
                }
            )
        )

        encoded = json.dumps(record, ensure_ascii=False)
        self.assertEqual(record["event_type"], "PermissionRequest")
        self.assertIn("tool_input", record["redacted_field_names"])
        self.assertNotIn("Get-Content secret.txt", encoded)

    def test_stop_and_unknown_events_do_not_crash(self) -> None:
        stop_record = sanitize_hook_payload(
            json.dumps(
                {
                    "hook_event_name": "Stop",
                    "last_assistant_message": "private answer",
                    "permission_mode": "plan",
                }
            )
        )
        unknown_record = sanitize_hook_payload(
            json.dumps({"hook_event_name": "FutureEvent", "extra": {"value": 1}})
        )

        self.assertEqual(stop_record["event_type"], "Stop")
        self.assertEqual(stop_record["permission_mode"], "plan")
        self.assertNotIn("private answer", json.dumps(stop_record))
        self.assertEqual(unknown_record["event_type"], "FutureEvent")
        self.assertIsNone(unknown_record["error_kind"])

    def test_unknown_permission_mode_is_hashed(self) -> None:
        record = sanitize_hook_payload(
            json.dumps(
                {
                    "hook_event_name": "Stop",
                    "permission_mode": "future-mode",
                }
            )
        )

        self.assertTrue(record["permission_mode"].startswith("sha256:"))
        self.assertNotIn("future-mode", json.dumps(record))

    def test_malformed_payload_is_safe(self) -> None:
        invalid_json = sanitize_hook_payload(b"not-json")
        invalid_utf8 = sanitize_hook_payload(b"\xff")
        non_object = sanitize_hook_payload("[]")

        self.assertEqual(invalid_json["error_kind"], "invalid_json")
        self.assertEqual(invalid_utf8["error_kind"], "invalid_utf8")
        self.assertEqual(non_object["error_kind"], "payload_not_object")

    def test_missing_event_is_recorded_without_raw_input(self) -> None:
        record = sanitize_hook_payload(json.dumps({"session_id": "session-1"}))

        self.assertIsNone(record["event_type"])
        self.assertEqual(record["error_kind"], "missing_hook_event_name")


class HookProbeCliTests(unittest.TestCase):
    def test_cli_appends_one_record_and_returns_zero(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "nested" / "hook.jsonl"
            original_stdin = __import__("sys").stdin
            try:
                import io

                __import__("sys").stdin = io.TextIOWrapper(
                    __import__("io").BytesIO(
                        json.dumps(
                            {
                                "hook_event_name": "Stop",
                                "session_id": "session-1",
                            }
                        ).encode("utf-8")
                    ),
                    encoding="utf-8",
                )
                result = main(["--output", str(output)])
            finally:
                __import__("sys").stdin = original_stdin

            self.assertEqual(result, 0)
            lines = output.read_text(encoding="utf-8").splitlines()
            self.assertEqual(len(lines), 1)
            self.assertEqual(json.loads(lines[0])["event_type"], "Stop")


if __name__ == "__main__":
    unittest.main()
