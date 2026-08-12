"""Probe the local Codex app-server over stdio without controlling Desktop."""

from __future__ import annotations

import argparse
import ctypes
import csv
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, TextIO


DEFAULT_TIMEOUT_SECONDS = 90.0
SCHEMA_VERSION = 1
SENSITIVE_KEYS = {
    "arguments",
    "baseInstructions",
    "content",
    "cwd",
    "developerInstructions",
    "error",
    "input",
    "items",
    "message",
    "output",
    "path",
    "preview",
    "result",
    "text",
    "url",
}
EVENT_METHODS = {
    "thread/started",
    "thread/status/changed",
    "turn/started",
    "turn/completed",
    "error",
}


def now_utc() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


def hash_value(value: object) -> str | None:
    if not isinstance(value, (str, int)):
        return None
    return "sha256:" + hashlib.sha256(str(value).encode("utf-8")).hexdigest()[:16]


def safe_name(value: object) -> str | None:
    if not isinstance(value, str) or not value or len(value) > 120:
        return None
    if all(character.isalnum() or character in "_/-:." for character in value):
        return value
    return "redacted-name"


def key_names(value: object) -> list[str]:
    if not isinstance(value, dict):
        return []
    return sorted(str(key) for key in value if str(key) not in SENSITIVE_KEYS)


def find_identifier(value: object, names: set[str]) -> str | None:
    if isinstance(value, dict):
        for key, child in value.items():
            if key in names:
                identifier = hash_value(child)
                if identifier:
                    return identifier
            identifier = find_identifier(child, names)
            if identifier:
                return identifier
    elif isinstance(value, list):
        for child in value:
            identifier = find_identifier(child, names)
            if identifier:
                return identifier
    return None


def find_turn_identifier(value: object) -> str | None:
    if isinstance(value, dict):
        for key, child in value.items():
            if key in {"turnId", "turn_id"}:
                identifier = hash_value(child)
                if identifier:
                    return identifier
            if key == "turn" and isinstance(child, dict):
                identifier = hash_value(child.get("id"))
                if identifier:
                    return identifier
            identifier = find_turn_identifier(child)
            if identifier:
                return identifier
    elif isinstance(value, list):
        for child in value:
            identifier = find_turn_identifier(child)
            if identifier:
                return identifier
    return None


def safe_status_fields(message: object) -> dict[str, Any]:
    if not isinstance(message, dict):
        return {}
    params = message.get("params")
    if not isinstance(params, dict):
        return {}
    fields: dict[str, Any] = {}
    status = params.get("status")
    if isinstance(status, dict):
        status_type = status.get("type")
        if isinstance(status_type, str):
            fields["status_type"] = status_type
        flags = status.get("activeFlags")
        if isinstance(flags, list):
            fields["active_flags"] = sorted(
                flag for flag in flags if isinstance(flag, str) and len(flag) <= 80
            )
    turn = params.get("turn")
    if isinstance(turn, dict):
        turn_status = turn.get("status")
        if isinstance(turn_status, str):
            fields["turn_status"] = turn_status
    return fields


def sanitize_message(message: object, direction: str) -> dict[str, Any]:
    """Return message shape and safe identifiers without message values."""

    if not isinstance(message, dict):
        return {
            "schema_version": SCHEMA_VERSION,
            "observed_at": now_utc(),
            "direction": direction,
            "message_kind": "unknown_message",
        }

    method = safe_name(message.get("method"))
    params = message.get("params")
    result = message.get("result")
    error = message.get("error")
    if "method" in message and "id" in message:
        message_kind = "request"
    elif "method" in message:
        message_kind = "notification"
    elif "error" in message:
        message_kind = "error_response"
    elif "result" in message:
        message_kind = "response"
    else:
        message_kind = "unknown_message"

    record: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "observed_at": now_utc(),
        "direction": direction,
        "message_kind": message_kind,
        "method": method,
        "id_present": "id" in message,
        "params_keys": key_names(params),
        "result_keys": key_names(result),
        "session_key": find_identifier(
            message, {"sessionId", "session_id", "threadId", "thread_id"}
        ),
        "turn_key": find_turn_identifier(message),
    }
    if isinstance(error, dict):
        code = error.get("code")
        record["error_code"] = code if isinstance(code, int) else "non_integer"
        record["error_keys"] = key_names(error)
    if message_kind == "notification" and method in EVENT_METHODS:
        record["event_name"] = method
    record.update(safe_status_fields(message))
    return record


@dataclass(frozen=True)
class DesktopProcess:
    pid: int
    image_name: str
    has_window: bool
    window_title_key: str | None


def _window_titles_by_pid() -> dict[int, str]:
    if os.name != "nt":
        return {}
    user32 = ctypes.windll.user32
    titles: dict[int, str] = {}
    enum_windows_proc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    @enum_windows_proc
    def callback(hwnd: int, _: int) -> bool:
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        buffer = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buffer, length + 1)
        process_id = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
        titles[int(process_id.value)] = buffer.value
        return True

    user32.EnumWindows(callback, 0)
    return titles


def desktop_processes() -> list[DesktopProcess]:
    if os.name != "nt":
        return []
    tasklist = subprocess.run(
        ["tasklist.exe", "/FO", "CSV", "/NH", "/FI", "IMAGENAME eq codex.exe"],
        capture_output=True,
        text=True,
        encoding="oem",
        errors="replace",
        check=False,
    )
    titles = _window_titles_by_pid()
    processes: list[DesktopProcess] = []
    for fields in csv.reader(tasklist.stdout.splitlines()):
        if len(fields) < 2:
            continue
        try:
            pid = int(fields[1])
        except ValueError:
            continue
        title = titles.get(pid, "")
        processes.append(
            DesktopProcess(
                pid=pid,
                image_name=str(fields[0]),
                has_window=bool(title),
                window_title_key=hash_value(title) if title else None,
            )
        )
    return processes


def session_index_snapshot(codex_home: Path) -> dict[str, Any]:
    path = codex_home / "session_index.jsonl"
    if not path.is_file():
        return {"exists": False, "record_count": 0, "ids_key": None, "mtime_ns": None}
    identifiers: list[str] = []
    record_count = 0
    with path.open("r", encoding="utf-8", errors="replace") as stream:
        for line in stream:
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                continue
            record_count += 1
            if isinstance(record, dict) and isinstance(record.get("id"), str):
                identifiers.append(record["id"])
    digest = hashlib.sha256("\n".join(sorted(identifiers)).encode("utf-8")).hexdigest()[:16]
    return {
        "exists": True,
        "record_count": record_count,
        "ids_key": "sha256:" + digest,
        "mtime_ns": path.stat().st_mtime_ns,
    }


def snapshot(codex_home: Path) -> dict[str, Any]:
    processes = desktop_processes()
    return {
        "observed_at": now_utc(),
        "desktop_processes": [
            {
                "pid": process.pid,
                "image_name": process.image_name,
                "has_window": process.has_window,
                "window_title_key": process.window_title_key,
            }
            for process in processes
        ],
        "desktop_process_count": len(processes),
        "session_index": session_index_snapshot(codex_home),
    }


class AppServerClient:
    def __init__(self, command: list[str], timeout: float, output: TextIO) -> None:
        self.command = command
        self.timeout = timeout
        self.output = output
        self.process: subprocess.Popen[str] | None = None
        self.next_id = 1
        self.messages: list[dict[str, Any]] = []

    def start(self) -> None:
        self.process = subprocess.Popen(
            self.command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )

    def send(self, method: str, params: dict[str, Any] | None) -> int:
        if self.process is None or self.process.stdin is None:
            raise RuntimeError("App Server is not running.")
        request_id = self.next_id
        self.next_id += 1
        request = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            request["params"] = params
        self.process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        self._emit(request, "outbound")
        return request_id

    def notify(self, method: str, params: dict[str, Any] | None = None) -> None:
        if self.process is None or self.process.stdin is None:
            raise RuntimeError("App Server is not running.")
        request: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            request["params"] = params
        self.process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        self._emit(request, "outbound")

    def read_until(self, request_id: int | None = None, event_methods: set[str] | None = None) -> list[dict[str, Any]]:
        if self.process is None or self.process.stdout is None:
            raise RuntimeError("App Server is not running.")
        deadline = time.monotonic() + self.timeout
        captured: list[dict[str, Any]] = []
        while time.monotonic() < deadline:
            line = self.process.stdout.readline()
            if line == "":
                break
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                self._emit(None, "inbound", error="invalid_json")
                continue
            captured.append(message)
            self.messages.append(message)
            self._emit(message, "inbound")
            if request_id is not None and message.get("id") == request_id:
                return captured
            if event_methods and message.get("method") in event_methods:
                return captured
        return captured

    def respond_to_server_request(self, message: dict[str, Any]) -> None:
        if self.process is None or self.process.stdin is None:
            return
        response = {
            "jsonrpc": "2.0",
            "id": message.get("id"),
            "error": {"code": -32000, "message": "Probe does not handle server requests."},
        }
        self.process.stdin.write(json.dumps(response, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        self._emit(response, "outbound")

    def _emit(self, message: object, direction: str, error: str | None = None) -> None:
        record = sanitize_message(message, direction)
        if error:
            record["parse_error"] = error
        json.dump(record, self.output, ensure_ascii=False, separators=(",", ":"))
        self.output.write("\n")
        self.output.flush()

    def close(self) -> int | None:
        if self.process is None:
            return None
        if self.process.stdin is not None:
            self.process.stdin.close()
        try:
            return self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            try:
                return self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                return self.process.wait(timeout=5)

    @property
    def turn_completed(self) -> bool:
        return any(message.get("method") == "turn/completed" for message in self.messages)


def _default_codex_command() -> list[str]:
    command = shutil.which("codex.cmd") or shutil.which("codex.exe") or shutil.which("codex")
    if command is None:
        raise FileNotFoundError("codex CLI was not found on PATH.")
    return [command, "app-server", "--stdio"]


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--codex-home", type=Path, default=Path(os.environ.get("CODEX_HOME", Path.home() / ".codex")))
    parser.add_argument("--cwd", type=Path, default=Path.cwd())
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT_SECONDS)
    parser.add_argument("--no-turn", action="store_true", help="Only perform initialize and do not run a test turn.")
    return parser


def run(args: argparse.Namespace, output: TextIO) -> int:
    before = snapshot(args.codex_home)
    json.dump({"schema_version": SCHEMA_VERSION, "record_type": "probe_started", "snapshot": before}, output, ensure_ascii=False, separators=(",", ":"))
    output.write("\n")
    output.flush()

    client = AppServerClient(_default_codex_command(), args.timeout, output)
    app_server_error: str | None = None
    try:
        client.start()
        initialize_id = client.send(
            "initialize",
            {"clientInfo": {"name": "codex-hud-probe", "version": "0.1.0"}},
        )
        initialize_messages = client.read_until(request_id=initialize_id)
        if not any(message.get("id") == initialize_id and "result" in message for message in initialize_messages):
            app_server_error = "initialize_failed"
        else:
            client.notify("initialized")

        if app_server_error is None and not args.no_turn:
            thread_id = client.send(
                "thread/start",
                {
                    "cwd": str(args.cwd.resolve()),
                    "ephemeral": True,
                    "sandbox": "read-only",
                    "approvalPolicy": "never",
                },
            )
            thread_messages = client.read_until(request_id=thread_id)
            thread_result = next(
                (message.get("result", {}) for message in thread_messages if message.get("id") == thread_id),
                {},
            )
            thread = thread_result.get("thread") if isinstance(thread_result, dict) else None
            actual_thread_id = thread.get("id") if isinstance(thread, dict) else None
            if not isinstance(actual_thread_id, str):
                app_server_error = "thread_start_failed"
            else:
                turn_id = client.send(
                    "turn/start",
                    {
                        "threadId": actual_thread_id,
                        "input": [{"type": "text", "text": "Reply with the single word READY.", "text_elements": []}],
                    },
                )
                client.read_until(request_id=turn_id)
                client.read_until(event_methods={"turn/completed", "error"})
    except (OSError, RuntimeError, ValueError) as exc:
        app_server_error = type(exc).__name__
        json.dump({"schema_version": SCHEMA_VERSION, "record_type": "probe_error", "error_kind": app_server_error}, output, ensure_ascii=False, separators=(",", ":"))
        output.write("\n")
        output.flush()
    finally:
        exit_code = client.close()

    after = snapshot(args.codex_home)
    desktop_before = {process["pid"] for process in before["desktop_processes"]}
    after_by_pid = {process["pid"]: process for process in after["desktop_processes"]}
    desktop_survived = desktop_before.issubset(after_by_pid)
    desktop_windows_survived = all(
        not process["has_window"] or after_by_pid[process["pid"]]["has_window"]
        for process in before["desktop_processes"]
        if process["pid"] in after_by_pid
    )
    session_index_changed = before["session_index"] != after["session_index"]
    turn_successful = args.no_turn or client.turn_completed
    app_status = "Supported" if app_server_error is None and turn_successful else "Partial"
    coexistence = (
        "Supported"
        if desktop_survived and desktop_windows_survived and not session_index_changed
        else "Inconclusive"
    )
    summary = {
        "schema_version": SCHEMA_VERSION,
        "record_type": "probe_completed",
        "observed_at": now_utc(),
        "app_server_status": app_status,
        "app_server_error": app_server_error,
        "app_server_exit_code": exit_code,
        "app_server_exit_warning": "nonzero_exit" if exit_code not in (None, 0) else None,
        "turn_completed_observed": client.turn_completed,
        "desktop_coexistence": coexistence,
        "desktop_processes_survived": desktop_survived,
        "desktop_windows_survived": desktop_windows_survived,
        "desktop_process_count_before": len(desktop_before),
        "desktop_process_count_after_baseline": sum(
            1 for pid in desktop_before if pid in after_by_pid
        ),
        "new_codex_processes_after": sum(
            1 for pid in after_by_pid if pid not in desktop_before
        ),
        "session_index_changed": session_index_changed,
        "session_index_before": before["session_index"],
        "session_index_after": after["session_index"],
    }
    json.dump(summary, output, ensure_ascii=False, separators=(",", ":"))
    output.write("\n")
    output.flush()
    return 0 if app_server_error is None else 2


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    if args.timeout <= 0:
        print("--timeout must be positive", file=sys.stderr)
        return 2
    try:
        return run(args, sys.stdout)
    except (FileNotFoundError, OSError) as exc:
        print(f"App Server probe error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
