"""Unit tests for the NudeNet FastAPI detection server (tools/nudenet_server/server.py)."""

from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "tools" / "nudenet_server"))
os.environ["NUDENET_LAZY_LOAD"] = "1"

import server  # noqa: E402


class _FakeDetector:
    """Mock NudeNet detector returning deterministic bounding-box detections."""

    def __init__(self, mapping: dict[str, list[dict[str, Any]]] | None = None) -> None:
        self._mapping = mapping or {}

    def detect(self, image_path: str) -> list[dict[str, Any]]:
        """Return configured detections or a default empty list."""
        return self._mapping.get(Path(image_path).name, [
            {"class": "FEMALE_BREAST_EXPOSED", "score": 0.87, "box": [10, 20, 100, 120]},
            {"class": "FACE_FEMALE", "score": 0.95, "box": [5, 5, 40, 40]},
        ])


@pytest.fixture(autouse=True)
def _reset_detector_and_env(monkeypatch: pytest.MonkeyPatch) -> Any:
    """Reset the singleton detector and API key env var around each test."""
    monkeypatch.delenv("NUDENET_API_KEY", raising=False)
    server.set_detector(_FakeDetector())
    yield
    server.set_detector(None)


def test_health_endpoint() -> None:
    """Verify GET /health returns 200 OK with status=ok."""
    with TestClient(server.app) as client:
        resp = client.get("/health")
    assert resp.status_code == 200
    assert resp.json() == {"status": "ok"}


def test_detect_batch_returns_normalized_results() -> None:
    """Verify POST /detect processes multiple JPEG frames and normalizes output."""
    with TestClient(server.app) as client:
        files = [
            ("files", ("000001.jpg", b"\xff\xd8\xff\xe0fake-jpeg-1", "image/jpeg")),
            ("files", ("000002.jpg", b"\xff\xd8\xff\xe0fake-jpeg-2", "image/jpeg")),
        ]
        resp = client.post("/detect", files=files)

    assert resp.status_code == 200
    payload = resp.json()
    assert len(payload["results"]) == 2
    assert payload["results"][0]["filename"] == "000001.jpg"
    assert payload["results"][0]["detections"][0]["class"] == "FEMALE_BREAST_EXPOSED"
    assert payload["results"][0]["detections"][0]["score"] == pytest.approx(0.87)
    assert payload["results"][1]["filename"] == "000002.jpg"


def test_detect_requires_bearer_token_when_api_key_configured(monkeypatch: pytest.MonkeyPatch) -> None:
    """Verify Bearer authentication is enforced when NUDENET_API_KEY is set."""
    monkeypatch.setenv("NUDENET_API_KEY", "super-secret-token")

    with TestClient(server.app) as client:
        files = [("files", ("frame.jpg", b"\xff\xd8\xff\xe0jpeg", "image/jpeg"))]

        no_auth = client.post("/detect", files=files)
        assert no_auth.status_code == 401

        bad_auth = client.post("/detect", files=files, headers={"Authorization": "Bearer wrong"})
        assert bad_auth.status_code == 401

        ok_auth = client.post("/detect", files=files, headers={"Authorization": "Bearer super-secret-token"})
        assert ok_auth.status_code == 200
