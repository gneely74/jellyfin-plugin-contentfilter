"""FastAPI HTTP server wrapping NudeNet ONNX detection for Jellyfin ContentFilter."""

from __future__ import annotations

import hmac
import logging
import os
import tempfile
from contextlib import asynccontextmanager
from typing import Any

from fastapi import Depends, FastAPI, File, Header, HTTPException, UploadFile, status

logger = logging.getLogger("nudenet_server")
logging.basicConfig(level=os.environ.get("LOG_LEVEL", "INFO").upper())

_detector: Any | None = None


def get_detector() -> Any:
    """Return the singleton NudeDetector instance, initializing it lazily if needed.

    Returns:
        An initialized ``nudenet.NudeDetector`` instance.

    Raises:
        RuntimeError: If the ``nudenet`` package cannot be imported.
    """
    global _detector
    if _detector is None:
        from nudenet import NudeDetector  # type: ignore[import-untyped]

        logger.info("Initializing NudeNet NudeDetector ONNX model...")
        _detector = NudeDetector()
        logger.info("NudeNet NudeDetector initialized.")
    return _detector


def set_detector(detector: Any | None) -> None:
    """Override the singleton detector instance (primarily for unit testing).

    Args:
        detector: Replacement detector object or ``None`` to reset.
    """
    global _detector
    _detector = detector


def verify_api_key(authorization: str | None = Header(default=None)) -> None:
    """Validate optional Bearer token authentication against ``NUDENET_API_KEY``.

    Args:
        authorization: Raw ``Authorization`` HTTP header value.

    Raises:
        HTTPException: With status 401 if ``NUDENET_API_KEY`` is set and does not match.
    """
    expected = os.environ.get("NUDENET_API_KEY", "").strip()
    if not expected:
        return

    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Missing Bearer token.",
        )

    token = authorization[len("Bearer ") :].strip()
    if not hmac.compare_digest(token, expected):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid API key.",
        )


@asynccontextmanager
async def lifespan(_app: FastAPI):
    """Preload the NudeNet ONNX model on server startup unless disabled.

    Args:
        _app: The FastAPI application instance.
    """
    if os.environ.get("NUDENET_LAZY_LOAD", "").lower() not in ("1", "true", "yes"):
        try:
            get_detector()
        except Exception:
            logger.exception("Failed to preload NudeDetector at startup; will retry on first request.")
    yield


app = FastAPI(
    title="ContentFilter NudeNet Detection Server",
    version="1.0.0",
    lifespan=lifespan,
)


@app.get("/health")
def health() -> dict[str, str]:
    """Return service health status.

    Returns:
        A JSON object indicating service readiness.
    """
    return {"status": "ok"}


@app.post("/detect", dependencies=[Depends(verify_api_key)])
async def detect(files: list[UploadFile] = File(...)) -> dict[str, list[dict[str, Any]]]:
    """Run NudeNet object detection on one or more uploaded image frames.

    Args:
        files: Uploaded JPEG/PNG image files sent via multipart form-data.

    Returns:
        A dictionary containing ``results``, where each entry has ``filename``
        and ``detections`` (list of ``class``, ``score``, and ``box``).

    Raises:
        HTTPException: If no files are uploaded or detection fails.
    """
    if not files:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="At least one image file must be provided.",
        )

    detector = get_detector()
    results: list[dict[str, Any]] = []

    with tempfile.TemporaryDirectory(prefix="nudenet_") as tmpdir:
        for idx, upload in enumerate(files):
            suffix = os.path.splitext(upload.filename or f"frame_{idx}.jpg")[1] or ".jpg"
            frame_path = os.path.join(tmpdir, f"frame_{idx:06d}{suffix}")
            content = await upload.read()
            with open(frame_path, "wb") as fh:
                fh.write(content)

            raw_detections = detector.detect(frame_path) or []
            normalized = [
                {
                    "class": str(det.get("class", "")),
                    "score": float(det.get("score", 0.0)),
                    "box": [int(v) for v in det.get("box", [])],
                }
                for det in raw_detections
            ]
            results.append(
                {
                    "filename": upload.filename or f"frame_{idx:06d}{suffix}",
                    "detections": normalized,
                }
            )

    return {"results": results}


if __name__ == "__main__":
    import uvicorn

    port = int(os.environ.get("PORT", "8001"))
    uvicorn.run("server:app", host="0.0.0.0", port=port, workers=1)
