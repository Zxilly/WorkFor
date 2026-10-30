"""Coordinate existing immutable native binaries without rebuilding Explorer DLLs.

Run only after explicitly choosing to exercise the verified research adapter.
The host must already be running under the same user/session/integrity.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import queue
import subprocess
import threading
import time


ROOT = Path(__file__).resolve().parents[2]
LIVE = ROOT / "out/native-live/WorkFor.NativeBridge.exe"
BASE = ROOT / "out/native-agent/WorkFor.NativeBridge.exe"
NO_WINDOW = subprocess.CREATE_NO_WINDOW


def baseline_inspection() -> dict:
    result = subprocess.run(
        [str(BASE), "--probe-explorer"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=15,
        creationflags=NO_WINDOW,
    )
    if result.returncode:
        raise RuntimeError("Baseline inspection failed: " + result.stderr[-2000:])
    report = json.loads(result.stdout)
    if not report.get("readOnly") or report.get("layoutAttached"):
        raise RuntimeError("Baseline did not report a read-only inspection")
    inspection = report["result"]["inspection"]
    if inspection.get("result", -1) < 0:
        raise RuntimeError("Baseline root inspection failed")
    return inspection


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--duration", type=int, default=10)
    args = parser.parse_args()
    if not 3 <= args.duration <= 60:
        parser.error("duration must be 3..60 seconds")
    if not LIVE.is_file() or not BASE.is_file():
        raise SystemExit("Build and verify both immutable native experiment directories first")

    events: queue.Queue[dict | None] = queue.Queue(maxsize=128)
    errors: list[str] = []
    process = subprocess.Popen(
        [str(LIVE), "--run-explorer"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        creationflags=NO_WINDOW,
    )

    def read_states() -> None:
        assert process.stdout is not None
        try:
            for line in process.stdout:
                if len(line) > 16384:
                    raise RuntimeError("Native status line exceeded limit")
                events.put(json.loads(line), timeout=5)
        except Exception as error:
            errors.append(str(error))
        finally:
            events.put(None, timeout=5)

    def read_errors() -> None:
        assert process.stderr is not None
        errors.append(process.stderr.read(16384))

    reader = threading.Thread(target=read_states, daemon=True)
    stderr_reader = threading.Thread(target=read_errors, daemon=True)
    reader.start()
    stderr_reader.start()
    states: list[dict] = []
    attached_inspection = None
    stopped = False
    deadline = time.monotonic() + 15
    started = None
    try:
        while time.monotonic() < deadline:
            event = events.get(timeout=max(0.1, deadline - time.monotonic()))
            if event is None:
                raise RuntimeError("Live exited before attachment: " + "; ".join(errors))
            states.append(event)
            payload = event.get("payload", {})
            if payload.get("state") == "Attached" and payload.get("layoutAttached"):
                if started is None:
                    started = time.monotonic()
                if payload.get("displayUpdates", 0) >= 1:
                    attached_inspection = baseline_inspection()
                    break
        if attached_inspection is None:
            raise RuntimeError("Live never consumed a host snapshot before the deadline")
        assert started is not None
        deadline = started + args.duration
        while time.monotonic() < deadline:
            try:
                event = events.get(timeout=max(0.1, deadline - time.monotonic()))
            except queue.Empty:
                continue
            if event is None:
                raise RuntimeError("Live exited during observation: " + "; ".join(errors))
            states.append(event)
    finally:
        assert process.stdin is not None
        process.stdin.close()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            # Do not terminate Explorer or load another DLL when detach cannot
            # be demonstrated. Retain the native controller for investigation.
            raise RuntimeError("Native detach did not finish; inspect the controller before retrying")
        reader.join(timeout=2)
        stderr_reader.join(timeout=2)
        while True:
            try:
                event = events.get_nowait()
            except queue.Empty:
                break
            if event is not None:
                states.append(event)
                if event.get("type") == "bridge.liveStopped":
                    stopped = not event.get("layoutAttached", True)

    if process.returncode or not stopped:
        raise RuntimeError("Live cleanup failed: " + "; ".join(errors))
    detached_inspection = baseline_inspection()
    report = {
        "experiment": "existing-baseline-inspection-of-live-layout",
        "durationSeconds": args.duration,
        "dllRebuilt": False,
        "attachedInspection": attached_inspection,
        "detachedInspection": detached_inspection,
        "states": states,
        "cleanupConfirmed": stopped,
    }
    path = ROOT / ".local/explorer-live-inspected.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": str(path),
        "cleanupConfirmed": stopped,
        "attachedNodeCount": len(attached_inspection["nodes"]),
        "detachedNodeCount": len(detached_inspection["nodes"]),
        "maxDisplayUpdates": max((x.get("payload", {}).get("displayUpdates", 0) for x in states), default=0),
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
