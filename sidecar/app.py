"""PlottR0.2 STT Sidecar — audio transcription micro-service.

Exposes two endpoints:
  POST /transcribe  — multipart/form-data with an ``audio`` field → JSON transcript
  GET  /health      — liveness probe (model may not be loaded yet)

The heavier ML runtime (CTranslate2 + PyAV/ffmpeg) lives entirely inside
this process so the .NET backend keeps its own dependency tree clean.
"""

from __future__ import annotations

import io
import logging
import os
import re
import threading

from fastapi import FastAPI, File, HTTPException, UploadFile
from pydantic import BaseModel

# ---------------------------------------------------------------------------
# Configuration (single-source env vars — nothing else configurable)
# ---------------------------------------------------------------------------

WHISPER_MODEL: str = os.getenv("WHISPER_MODEL", "base")
PORT: int = int(os.getenv("PORT", "8000"))

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-8s  %(name)s  %(message)s",
)
log = logging.getLogger("stt-sidecar")

# ---------------------------------------------------------------------------
# FastAPI app
# ---------------------------------------------------------------------------

app = FastAPI(
    title="PlottR0.2 STT Sidecar",
    description="Transcribes audio blobs to text via faster-whisper.",
    version="0.1.0",
)

# ---------------------------------------------------------------------------
# Lazy model singleton
# ---------------------------------------------------------------------------

_model = None  # type: faster_whisper.WhisperModel | None
_model_lock = threading.Lock()


def _get_model():
    """Return the WhisperModel, loading it exactly once (thread-safe).

    The first request after a cold start pays the download + load cost
    (~142 MB for the ``base`` model, cached in ``~/.cache/huggingface``).
    Subsequent requests reuse the already-loaded model.
    """
    global _model  # noqa: PLW0603
    if _model is not None:
        return _model
    with _model_lock:
        # Double-check after acquiring the lock.
        if _model is not None:
            return _model
        log.info("Loading whisper model %r (CPU, int8) …", WHISPER_MODEL)
        from faster_whisper import WhisperModel

        _model = WhisperModel(
            WHISPER_MODEL,
            device="cpu",
            compute_type="int8",
        )
        log.info("Model %r loaded.", WHISPER_MODEL)
        return _model


# ---------------------------------------------------------------------------
# Response models
# ---------------------------------------------------------------------------


class TranscriptionResponse(BaseModel):
    text: str
    language: str
    duration: float


class HealthResponse(BaseModel):
    status: str
    model: str
    modelLoaded: bool


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------

_WHITESPACE_RE = re.compile(r"\s+")


def _normalise_text(raw: str) -> str:
    """Strip outer whitespace and collapse inner runs to single spaces."""
    return _WHITESPACE_RE.sub(" ", raw).strip()


@app.post("/transcribe", response_model=TranscriptionResponse)
async def transcribe(audio: UploadFile = File(...)) -> TranscriptionResponse:
    """Transcribe an audio file to text.

    Accepts any audio format that ffmpeg/PyAV can decode (webm, mp3, wav,
    m4a, …).  The .NET backend forwards the raw webm bytes from the
    browser's MediaRecorder untouched.
    """
    if audio.filename is None or audio.filename == "":
        raise HTTPException(status_code=400, detail="No filename provided.")

    data = await audio.read()
    if len(data) == 0:
        raise HTTPException(status_code=400, detail="Uploaded audio file is empty.")

    model = _get_model()

    # faster_whisper's decoder accepts a path, a file-like object, or a numpy
    # array — raw bytes make PyAV's PyIOFile raise ValueError. Wrap the upload
    # in BytesIO so no temp file is needed.
    segments, info = model.transcribe(
        io.BytesIO(data),
        language=None,   # auto-detect
        beam_size=5,
        vad_filter=True,  # skip silence for faster processing
    )

    text = _normalise_text(" ".join(seg.text for seg in segments))

    return TranscriptionResponse(
        text=text,
        language=info.language or "",
        duration=info.duration,
    )


@app.get("/health", response_model=HealthResponse)
async def health() -> HealthResponse:
    """Liveness probe — always 200. Reports whether the model is loaded."""
    return HealthResponse(
        status="ok",
        model=WHISPER_MODEL,
        modelLoaded=_model is not None,
    )


# ---------------------------------------------------------------------------
# Uvicorn entry-point (``python app.py``)
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=PORT)
