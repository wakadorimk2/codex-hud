"""Observe Codex command-hook input without retaining sensitive values.

Codex starts this probe as a short-lived command hook. The probe reads one
JSON object from standard input, writes one redacted JSONL record, and exits.
It never writes the prompt, tool input, path, model, or message value.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 1
DEFAULT_OUTPUT_NAME = "codex-hud-hook-probe.jsonl"
SAFE_IDENTIFIER = re.compile(r"^[A-Za-z0-9_.:-]{1,80}$")

SENSITIVE_FIELDS = frozenset(
    {
        "command",
        "cwd",
        "file_path",
        "last_assistant_message",
        "model",
        "prompt",
        "tool_input",
        "transcript_path",
    }
)


def _now_utc() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


def _hash_identifier(value: str) -> str:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]
    return f"sha256:{digest}"


def _string_value(value: object) -> str | None:
    if isinstance(value, str) and value:
        return value
    return None


def _safe_event_type(value: object) -> str | None:
    value = _string_value(value)
    if value is None:
        return None
    if SAFE_IDENTIFIER.fullmatch(value):
        return value
    return _hash_identifier(value)


def _hashed_field(payload: dict[str, Any], field_name: str) -> str | None:
    value = _string_value(payload.get(field_name))
    return _hash_identifier(value) if value is not None else None


def _safe_key_list(payload: dict[str, Any]) -> list[str]:
    keys: list[str] = []
    for key in sorted(payload):
        if SAFE_IDENTIFIER.fullmatch(key):
            keys.append(key)
        else:
            keys.append(_hash_identifier(key))
    return keys


def _base_record() -> dict[str, Any]:
    return {
        "schema_version": SCHEMA_VERSION,
        "observed_at": _now_utc(),
        "event_type": None,
        "session_key": None,
        "turn_key": None,
        "project_key": None,
        "top_level_keys": [],
        "redacted_field_names": [],
        "error_kind": None,
    }


def sanitize_hook_payload(
    raw_payload: bytes | str,
    *,
    configured_event: str | None = None,
) -> dict[str, Any]:
    """Return a safe observation record for one Hook payload."""

    record = _base_record()
    try:
        if isinstance(raw_payload, bytes):
            decoded = raw_payload.decode("utf-8")
        else:
            decoded = raw_payload
        payload = json.loads(decoded)
    except UnicodeDecodeError:
        record["error_kind"] = "invalid_utf8"
        return record
    except json.JSONDecodeError:
        record["error_kind"] = "invalid_json"
        return record

    if not isinstance(payload, dict):
        record["error_kind"] = "payload_not_object"
        return record

    record["event_type"] = _safe_event_type(payload.get("hook_event_name"))
    record["session_key"] = _hashed_field(payload, "session_id")
    record["turn_key"] = _hashed_field(payload, "turn_id")
    record["project_key"] = _hashed_field(payload, "cwd")
    record["top_level_keys"] = _safe_key_list(payload)
    record["redacted_field_names"] = sorted(SENSITIVE_FIELDS.intersection(payload))

    if configured_event is not None and record["event_type"] != configured_event:
        record["configured_event"] = configured_event
        record["event_mismatch"] = True

    if record["event_type"] is None:
        record["error_kind"] = "missing_hook_event_name"
    return record


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(tempfile.gettempdir()) / DEFAULT_OUTPUT_NAME,
        help="Append redacted JSONL records to this path.",
    )
    parser.add_argument(
        "--configured-event",
        help="Optional event name used to flag a configuration mismatch.",
    )
    return parser


def _append_record(path: Path, record: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8", newline="\n") as output:
        json.dump(record, output, ensure_ascii=False, separators=(",", ":"))
        output.write("\n")
        output.flush()


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        raw_payload = sys.stdin.buffer.read()
        record = sanitize_hook_payload(
            raw_payload,
            configured_event=args.configured_event,
        )
        _append_record(args.output, record)
    except (OSError, TypeError, ValueError, UnicodeError):
        # A diagnostic hook must not block or fail the Codex turn.
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
