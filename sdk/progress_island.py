"""Dependency-free Python progress reporter for the Windows Progress Island app.

Usage:
    from progress_island import Task
    with Task("批量处理") as task:
        for i in range(100):
            do_work(i)
            task.update(i + 1, total=100, message=f"样本 {i + 1}/100")

Reporting failures never stop the user's calculation. Reports are best effort,
and final reports retry once. No remote server or Python package is required.
"""
from __future__ import annotations

import json
import math
import os
from pathlib import Path
import time
import urllib.error
import urllib.request
import uuid
import warnings


class Task:
    def __init__(self, title: str, *, task_id: str | None = None, config: str | None = None):
        self.title = title
        self.id = task_id or uuid.uuid4().hex
        root = Path(os.environ.get("PROGRESS_ISLAND_DATA") or Path(os.environ.get("LOCALAPPDATA", Path.home())) / "ProgressIsland")
        self.config_path = Path(config) if config else root / "settings.json"
        self._last_send = 0.0
        self._warned = False
        self._ended = False
        self._opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def _send(self, payload: dict, *, final: bool = False) -> bool:
        payload = {"id": self.id, "title": self.title, **payload}
        for attempt in range(2 if final else 1):
            try:
                cfg = json.loads(self.config_path.read_text(encoding="utf-8-sig"))
                port = int(cfg["port"])
                if not 1024 < port <= 65535:
                    raise ValueError("Invalid local port")
                request = urllib.request.Request(
                    f"http://127.0.0.1:{port}/tasks",
                    data=json.dumps(payload, ensure_ascii=False, allow_nan=False).encode("utf-8"),
                    headers={"Content-Type": "application/json", "Authorization": f"Bearer {cfg['token']}"},
                    method="POST",
                )
                with self._opener.open(request, timeout=1.5) as response:
                    response.read()
                self._last_send = time.monotonic()
                return True
            except (OSError, ValueError, KeyError, urllib.error.URLError):
                if final and attempt == 0:
                    time.sleep(0.15)
        if not self._warned:
            warnings.warn("任务岛上报失败；计算继续。请确认任务岛已启动。", RuntimeWarning, stacklevel=3)
            self._warned = True
        return False

    def update(self, current: float | None = None, *, total: float | None = None,
               progress: float | None = None, message: str = "", force: bool = False) -> bool:
        if self._ended:
            raise RuntimeError("Task already ended; use a new task id for a new run")
        if progress is None and current is not None:
            if total is None or total <= 0:
                raise ValueError("total must be positive when current is supplied")
            progress = current / total * 100
        if progress is not None and (not math.isfinite(progress) or not 0 <= progress <= 100):
            raise ValueError("progress must be a finite percentage from 0 to 100")
        if not force and time.monotonic() - self._last_send < 0.25:
            return False
        payload = {"status": "running", "detail": message}
        if progress is not None:
            payload["progress"] = progress
        return self._send(payload)

    def complete(self, message: str = "任务完成") -> bool:
        result = self._send({"status": "completed", "progress": 100, "detail": message}, final=True)
        self._ended = True
        return result

    def fail(self, message: str = "任务失败") -> bool:
        result = self._send({"status": "failed", "detail": message}, final=True)
        self._ended = True
        return result

    def __enter__(self) -> Task:
        self.update(message="已开始", force=True)
        return self

    def __exit__(self, exc_type, exc, tb) -> bool:
        if not self._ended:
            if exc is None:
                self.complete()
            else:
                self.fail(f"{exc_type.__name__}: {exc}"[:4000])
        return False
