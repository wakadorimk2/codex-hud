from __future__ import annotations

import io
import json
import tempfile
import unittest
from pathlib import Path

from probe import (
    JsonlLine,
    JsonlTail,
    ProbeContext,
    SOURCE_FIXTURE,
    observe_line,
)


def _json_line(line_number: int, value: dict[str, object]) -> JsonlLine:
    return JsonlLine(
        line_number=line_number,
        content=json.dumps(value, ensure_ascii=False).encode("utf-8"),
    )


class ProbeObservationTests(unittest.TestCase):
    def test_task_events_are_provisional_and_tool_completion_is_unknown(self) -> None:
        context = ProbeContext()

        started = observe_line(
            _json_line(
                1,
                {
                    "type": "event_msg",
                    "payload": {
                        "type": "task_started",
                        "session_id": "session-example",
                    },
                },
            ),
            context,
            source_kind=SOURCE_FIXTURE,
        )
        tool_completed = observe_line(
            _json_line(
                2,
                {
                    "type": "response_item",
                    "payload": {
                        "type": "custom_tool_call",
                        "status": "completed",
                        "arguments": {"private": "do-not-emit"},
                    },
                },
            ),
            context,
            source_kind=SOURCE_FIXTURE,
        )

        self.assertIsNotNone(started)
        self.assertEqual(started.state, "Running")
        self.assertEqual(started.confidence, "provisional")
        self.assertIsNotNone(tool_completed)
        self.assertEqual(tool_completed.state, "Unknown")
        self.assertEqual(tool_completed.error_kind, None)

    def test_sensitive_values_and_identifiers_are_not_emitted(self) -> None:
        context = ProbeContext()
        observation = observe_line(
            _json_line(
                1,
                {
                    "type": "response_item",
                    "payload": {
                        "type": "user_message",
                        "session_id": "session-secret",
                        "turn_id": "turn-secret",
                        "message": "private message body",
                        "input": "private input body",
                        "cwd": "C:/private/project",
                    },
                },
            ),
            context,
            source_kind=SOURCE_FIXTURE,
        )

        self.assertIsNotNone(observation)
        encoded = json.dumps(observation.to_dict(), ensure_ascii=False)
        self.assertNotIn("session-secret", encoded)
        self.assertNotIn("turn-secret", encoded)
        self.assertNotIn("private message body", encoded)
        self.assertNotIn("private input body", encoded)
        self.assertNotIn("C:/private/project", encoded)
        self.assertIn("message", observation.redactions)
        self.assertIn("input", observation.redactions)
        self.assertIn("cwd", observation.redactions)

    def test_invalid_json_is_an_observation_error(self) -> None:
        observation = observe_line(
            JsonlLine(line_number=4, content=b"not-json"),
            ProbeContext(),
            source_kind=SOURCE_FIXTURE,
        )

        self.assertIsNotNone(observation)
        self.assertEqual(observation.state, "Unknown")
        self.assertEqual(observation.error_kind, "invalid_json")


class JsonlTailTests(unittest.TestCase):
    def test_partial_line_is_held_until_newline(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "probe.jsonl"
            path.write_bytes(b'{"type":"event_msg"')
            tail = JsonlTail(path, start_at_end=False)

            self.assertEqual(tail.poll(), [])

            with path.open("ab") as stream:
                stream.write(b',"payload":{"type":"task_started"}}\n')

            lines = tail.poll()
            self.assertEqual(len(lines), 1)
            self.assertEqual(lines[0].line_number, 1)

    def test_follow_starts_at_current_end(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "probe.jsonl"
            path.write_bytes(b'{"type":"old"}\n')
            tail = JsonlTail(path, start_at_end=True)

            self.assertEqual(tail.poll(), [])

            with path.open("ab") as stream:
                stream.write(b'{"type":"new"}\n')

            lines = tail.poll()
            self.assertEqual(len(lines), 1)
            self.assertEqual(json.loads(lines[0].content)["type"], "new")

    def test_truncation_resets_the_read_offset(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "probe.jsonl"
            path.write_bytes(b'{"type":"old"}\n')
            tail = JsonlTail(path, start_at_end=False)
            self.assertEqual(len(tail.poll()), 1)

            path.write_bytes(b'{"x":1}\n')
            lines = tail.poll()
            self.assertEqual(len(lines), 1)
            self.assertEqual(json.loads(lines[0].content)["x"], 1)


if __name__ == "__main__":
    unittest.main()
