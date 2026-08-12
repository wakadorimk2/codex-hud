from __future__ import annotations

import json
import unittest

from app_server_probe import sanitize_message


class AppServerSanitizationTests(unittest.TestCase):
    def test_initialize_response_emits_shape_without_sensitive_values(self) -> None:
        message = {
            "jsonrpc": "2.0",
            "id": 1,
            "result": {
                "codexHome": "C:/Users/private/.codex",
                "platformOs": "windows",
                "userAgent": "private-agent-version",
            },
        }

        record = sanitize_message(message, "inbound")
        encoded = json.dumps(record)
        self.assertEqual(record["message_kind"], "response")
        self.assertTrue(record["id_present"])
        self.assertNotIn("C:/Users/private", encoded)
        self.assertNotIn("private-agent-version", encoded)

    def test_turn_notification_exposes_event_name_and_hashed_ids(self) -> None:
        message = {
            "jsonrpc": "2.0",
            "method": "turn/completed",
            "params": {
                "threadId": "thread-secret",
                "turn": {"id": "turn-secret", "status": "completed", "items": []},
            },
        }

        record = sanitize_message(message, "inbound")
        self.assertEqual(record["message_kind"], "notification")
        self.assertEqual(record["event_name"], "turn/completed")
        self.assertEqual(record["turn_status"], "completed")
        self.assertTrue(record["session_key"].startswith("sha256:"))
        self.assertTrue(record["turn_key"].startswith("sha256:"))
        self.assertNotIn("thread-secret", json.dumps(record))
        self.assertNotIn("turn-secret", json.dumps(record))

    def test_unknown_message_is_retained_without_body(self) -> None:
        record = sanitize_message("not-json-object", "inbound")
        self.assertEqual(record["message_kind"], "unknown_message")
        self.assertNotIn("not-json-object", json.dumps(record))

    def test_server_error_is_shape_only(self) -> None:
        record = sanitize_message(
            {
                "jsonrpc": "2.0",
                "id": 2,
                "error": {"code": -32000, "message": "private error body"},
            },
            "inbound",
        )
        self.assertEqual(record["message_kind"], "error_response")
        self.assertEqual(record["error_code"], -32000)
        self.assertNotIn("private error body", json.dumps(record))

    def test_status_change_exposes_only_safe_enum_values(self) -> None:
        record = sanitize_message(
            {
                "jsonrpc": "2.0",
                "method": "thread/status/changed",
                "params": {
                    "threadId": "thread-secret",
                    "status": {
                        "type": "active",
                        "activeFlags": ["waitingOnApproval"],
                        "private": "do-not-emit",
                    },
                },
            },
            "inbound",
        )
        self.assertEqual(record["status_type"], "active")
        self.assertEqual(record["active_flags"], ["waitingOnApproval"])
        self.assertNotIn("do-not-emit", json.dumps(record))


if __name__ == "__main__":
    unittest.main()
