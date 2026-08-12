"""Read-only Codex session JSONL probe.

The probe emits redacted observations. It never emits Codex message bodies,
tool arguments, or source paths.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, TextIO


SCHEMA_VERSION = 1
SOURCE_SESSION_JSONL = "session_jsonl"
SOURCE_FIXTURE = "fixture"
DEFAULT_POLL_MS = 250
MIN_POLL_MS = 50
MAX_POLL_MS = 5_000
SAFE_IDENTIFIER = re.compile(r"^[A-Za-z0-9_.:-]{1,80}$")

SENSITIVE_PAYLOAD_FIELDS = frozenset(
    {
        "arguments",
        "base_instructions",
        "content",
        "cwd",
        "encrypted_content",
        "git",
        "history",
        "input",
        "local_images",
        "message",
        "output",
        "result",
        "state",
        "summary",
        "text",
        "workspace_roots",
    }
)


@dataclass(frozen=True)
class JsonlLine:
    """One complete newline-terminated line from the input file."""

    line_number: int
    content: bytes


@dataclass
class ProbeContext:
    """Identifiers learned from the current input stream."""

    session_key: str | None = None


@dataclass(frozen=True)
class Observation:
    """A redacted observation for one input line."""

    line_number: int
    observed_at: str
    source_kind: str
    session_key: str | None
    turn_key: str | None
    top_type: str | None
    payload_type: str | None
    payload_name: str | None
    state: str
    confidence: str
    reason: str
    redactions: tuple[str, ...]
    error_kind: str | None = None

    def to_dict(self) -> dict[str, Any]:
        """Return the public, JSON-safe observation shape."""

        data: dict[str, Any] = {
            "schema_version": SCHEMA_VERSION,
            "line_number": self.line_number,
            "observed_at": self.observed_at,
            "source_kind": self.source_kind,
            "session_key": self.session_key,
            "turn_key": self.turn_key,
            "top_type": self.top_type,
            "payload_type": self.payload_type,
            "payload_name": self.payload_name,
            "state": self.state,
            "confidence": self.confidence,
            "reason": self.reason,
            "redactions": list(self.redactions),
        }
        if self.error_kind is not None:
            data["error_kind"] = self.error_kind
        return data


class JsonlTail:
    """Read complete JSONL lines and hold a trailing partial line."""

    def __init__(self, path: Path, *, start_at_end: bool) -> None:
        self.path = path
        self._offset = path.stat().st_size if start_at_end else 0
        self._buffer = bytearray()
        self._line_number = 0

    def poll(self) -> list[JsonlLine]:
        """Read new complete lines without interpreting their contents."""

        size = self.path.stat().st_size
        if size < self._offset:
            self._offset = 0
            self._buffer.clear()
            self._line_number = 0

        with self.path.open("rb") as stream:
            stream.seek(self._offset)
            chunk = stream.read()
        self._offset += len(chunk)
        self._buffer.extend(chunk)

        lines: list[JsonlLine] = []
        while True:
            newline_index = self._buffer.find(b"\n")
            if newline_index < 0:
                break
            raw_line = bytes(self._buffer[:newline_index])
            del self._buffer[: newline_index + 1]
            self._line_number += 1
            lines.append(
                JsonlLine(
                    line_number=self._line_number,
                    content=raw_line.rstrip(b"\r"),
                )
            )
        return lines


def _now_utc() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


def _hash_identifier(value: str) -> str:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]
    return f"sha256:{digest}"


def _safe_identifier(value: object) -> str | None:
    if not isinstance(value, str) or not value:
        return None
    if SAFE_IDENTIFIER.fullmatch(value):
        return value
    return _hash_identifier(value)


def _string_value(value: object) -> str | None:
    if isinstance(value, str) and value:
        return value
    return None


def _nested_string(mapping: dict[str, Any], *keys: str) -> str | None:
    current: object = mapping
    for key in keys:
        if not isinstance(current, dict):
            return None
        current = current.get(key)
    return _string_value(current)


def _session_identifier(payload: dict[str, Any]) -> str | None:
    session_id = _string_value(payload.get("session_id"))
    if session_id is not None:
        return session_id
    if payload.get("type") == "session_meta":
        return _string_value(payload.get("id"))
    return None


def _turn_identifier(payload: dict[str, Any]) -> str | None:
    turn_id = _string_value(payload.get("turn_id"))
    if turn_id is not None:
        return turn_id
    return _nested_string(
        payload,
        "internal_chat_message_metadata_passthrough",
        "turn_id",
    )


def _redacted_fields(payload: dict[str, Any]) -> tuple[str, ...]:
    return tuple(sorted(SENSITIVE_PAYLOAD_FIELDS.intersection(payload)))


def _error_observation(
    line: JsonlLine,
    source_kind: str,
    context: ProbeContext,
    reason: str,
    error_kind: str,
) -> Observation:
    return Observation(
        line_number=line.line_number,
        observed_at=_now_utc(),
        source_kind=source_kind,
        session_key=context.session_key,
        turn_key=None,
        top_type=None,
        payload_type=None,
        payload_name=None,
        state="Unknown",
        confidence="unverified",
        reason=reason,
        redactions=(),
        error_kind=error_kind,
    )


def _classify(payload_type: str | None) -> tuple[str, str, str]:
    if payload_type == "task_started":
        return "Running", "provisional", "Observed task_started event."
    if payload_type == "task_complete":
        return "Completed", "provisional", "Observed task_complete event."
    return (
        "Unknown",
        "unverified",
        "No confirmed state mapping exists for this event type.",
    )


def observe_line(
    line: JsonlLine,
    context: ProbeContext,
    *,
    source_kind: str,
) -> Observation | None:
    """Parse one complete line and return only redacted information."""

    if not line.content.strip():
        return None

    try:
        decoded = line.content.decode("utf-8")
    except UnicodeDecodeError:
        return _error_observation(
            line,
            source_kind,
            context,
            "The line is not valid UTF-8.",
            "invalid_utf8",
        )

    try:
        record = json.loads(decoded)
    except json.JSONDecodeError:
        return _error_observation(
            line,
            source_kind,
            context,
            "The complete line is not valid JSON.",
            "invalid_json",
        )

    if not isinstance(record, dict):
        return _error_observation(
            line,
            source_kind,
            context,
            "The JSON record is not an object.",
            "record_not_object",
        )

    top_type = _safe_identifier(record.get("type"))
    payload = record.get("payload")
    if not isinstance(payload, dict):
        return Observation(
            line_number=line.line_number,
            observed_at=_now_utc(),
            source_kind=source_kind,
            session_key=context.session_key,
            turn_key=None,
            top_type=top_type,
            payload_type=None,
            payload_name=None,
            state="Unknown",
            confidence="unverified",
            reason="The record has no object payload.",
            redactions=(),
            error_kind="payload_not_object",
        )

    session_id = _session_identifier(payload)
    if session_id is not None:
        context.session_key = _hash_identifier(session_id)

    turn_id = _turn_identifier(payload)
    payload_type = _safe_identifier(payload.get("type"))
    payload_name = _safe_identifier(payload.get("name"))
    state, confidence, reason = _classify(payload_type)

    return Observation(
        line_number=line.line_number,
        observed_at=_now_utc(),
        source_kind=source_kind,
        session_key=context.session_key,
        turn_key=_hash_identifier(turn_id) if turn_id else None,
        top_type=top_type,
        payload_type=payload_type,
        payload_name=payload_name,
        state=state,
        confidence=confidence,
        reason=reason,
        redactions=_redacted_fields(payload),
    )


def observations_from_lines(
    lines: Iterable[JsonlLine],
    context: ProbeContext,
    *,
    source_kind: str,
) -> Iterable[Observation]:
    for line in lines:
        observation = observe_line(line, context, source_kind=source_kind)
        if observation is not None:
            yield observation


def _poll_ms(value: str) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("poll-ms must be an integer") from exc
    if not MIN_POLL_MS <= parsed <= MAX_POLL_MS:
        raise argparse.ArgumentTypeError(
            f"poll-ms must be between {MIN_POLL_MS} and {MAX_POLL_MS}"
        )
    return parsed


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument(
        "--session-file",
        type=Path,
        help="Read one Codex session JSONL file.",
    )
    source.add_argument(
        "--fixture",
        type=Path,
        help="Read one local JSONL fixture.",
    )

    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--once",
        action="store_true",
        help="Read complete lines once. This is the default.",
    )
    mode.add_argument(
        "--follow",
        action="store_true",
        help="Follow appended complete lines from the current end of the file.",
    )
    parser.add_argument(
        "--poll-ms",
        type=_poll_ms,
        default=DEFAULT_POLL_MS,
        help=f"Follow polling interval in milliseconds ({DEFAULT_POLL_MS} default).",
    )
    return parser


def _emit(observations: Iterable[Observation], output: TextIO) -> None:
    for observation in observations:
        json.dump(
            observation.to_dict(),
            output,
            ensure_ascii=False,
            separators=(",", ":"),
        )
        output.write("\n")
        output.flush()


def _validate_source(path: Path) -> None:
    if not path.is_file():
        raise ValueError("The input file does not exist or is not a file.")


def _run_once(path: Path, source_kind: str, output: TextIO) -> int:
    tail = JsonlTail(path, start_at_end=False)
    context = ProbeContext()
    _emit(
        observations_from_lines(
            tail.poll(),
            context,
            source_kind=source_kind,
        ),
        output,
    )
    return 0


def _run_follow(path: Path, source_kind: str, poll_ms: int, output: TextIO) -> int:
    tail = JsonlTail(path, start_at_end=True)
    context = ProbeContext()
    delay = poll_ms / 1_000
    while True:
        _emit(
            observations_from_lines(
                tail.poll(),
                context,
                source_kind=source_kind,
            ),
            output,
        )
        time.sleep(delay)


def main(argv: list[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)
    path: Path = args.session_file or args.fixture
    source_kind = SOURCE_FIXTURE if args.fixture is not None else SOURCE_SESSION_JSONL

    try:
        _validate_source(path)
        if args.follow:
            return _run_follow(path, source_kind, args.poll_ms, sys.stdout)
        return _run_once(path, source_kind, sys.stdout)
    except KeyboardInterrupt:
        return 0
    except (OSError, ValueError) as exc:
        print(f"Probe error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
