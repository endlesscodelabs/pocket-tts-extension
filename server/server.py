"""
server.py
─────────
FastAPI Neural TTS Server for Voices.
Provides offline local text-to-speech synthesis using Pocket TTS with:
  • Realistic voice personas
  • Phonetic normalization (currencies, abbreviations, tech acronyms, symbols)
  • In-memory LRU audio caching
  • Sentence chunking & streaming (Server-Sent Events)
  • Custom pronunciation dictionary
"""

from __future__ import annotations

import base64
import hashlib
import io
import json
import os
import re
import sys
from collections import OrderedDict
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, Dict, List, Optional

from fastapi import FastAPI, HTTPException, Response, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, StreamingResponse
from pydantic import BaseModel, Field
import uvicorn

# ── Safe Environment Initialization ──────────────────────────────────────────
# Prevent OpenBLAS / MKL memory allocation errors on Windows / constrained environments
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
os.environ.setdefault("OMP_NUM_THREADS", "1")
os.environ.setdefault("MKL_NUM_THREADS", "1")

# ── Optional / Resilient Imports ──────────────────────────────────────────────
try:
    import numpy as np
    HAS_NUMPY = True
except Exception as _e:
    np = None  # type: ignore[assignment]
    HAS_NUMPY = False

try:
    import scipy.io.wavfile
    HAS_SCIPY = True
except Exception as _e:
    HAS_SCIPY = False

try:
    from pocket_tts import TTSModel
    HAS_POCKET_TTS = True
    POCKET_TTS_IMPORT_ERROR: Optional[str] = None
except Exception as _e:
    TTSModel = None  # type: ignore[assignment]
    HAS_POCKET_TTS = False
    POCKET_TTS_IMPORT_ERROR = str(_e)

# ── Global State & Configuration ─────────────────────────────────────────────
KNOWN_VOICES = ["alba", "fantine", "jean", "marius", "javert", "cosette", "eponine", "azelma"]
DEFAULT_SAMPLE_RATE = 24000
CACHE_MAX_ENTRIES = 256

model: Optional[Any] = None
model_error: Optional[str] = None
voice_cache: Dict[str, Any] = {}
response_cache: OrderedDict[str, bytes] = OrderedDict()

PRONUNCIATION_FILE = Path(__file__).resolve().parent / "pronunciation_dict.json"
pronunciation_dict: Dict[str, str] = {}


# ── Pronunciation Training Dictionary ─────────────────────────────────────────
def load_pronunciation_dict() -> None:
    global pronunciation_dict
    if PRONUNCIATION_FILE.exists():
        try:
            with open(PRONUNCIATION_FILE, "r", encoding="utf-8") as f:
                pronunciation_dict = json.load(f)
            print(f"[ Voices ] Loaded {len(pronunciation_dict)} complex word pronunciations.")
        except Exception as e:
            print(f"[ Voices ] Warning: Failed to load pronunciation dictionary: {e}")
            pronunciation_dict = {}
    else:
        pronunciation_dict = {}


def save_pronunciation_dict() -> None:
    try:
        with open(PRONUNCIATION_FILE, "w", encoding="utf-8") as f:
            json.dump(pronunciation_dict, f, indent=2, ensure_ascii=False)
    except Exception as e:
        print(f"[ Voices ] Warning: Failed to save pronunciation dictionary: {e}")


# ── Model & Voice Embeddings ──────────────────────────────────────────────────
def load_tts_model() -> bool:
    """Safely initialize the Pocket TTS model and pre-warm the default voice."""
    global model, model_error
    if not HAS_POCKET_TTS:
        model_error = (
            f"pocket_tts library is not available: {POCKET_TTS_IMPORT_ERROR}. "
            "Please ensure dependencies are installed (pip install -r requirements.txt)."
        )
        print(f"[ Voices ] ERROR: {model_error}")
        return False

    try:
        print("[ Voices ] Loading pocket_tts model...")
        model = TTSModel.load_model()
        print("[ Voices ] Model ready.")
        model_error = None

        # Pre-warm default voice
        try:
            get_voice_state("alba")
        except Exception as prewarm_err:
            print(f"[ Voices ] Notice: Default voice pre-warm skipped: {prewarm_err}")

        return True
    except Exception as e:
        model_error = str(e)
        model = None
        print(f"[ Voices ] ERROR: Failed to load pocket_tts model: {e}")
        return False


def get_voice_state(voice_name: str) -> Any:
    """Lazily load and cache a voice embedding. Subsequent calls are instant."""
    if model is None:
        raise RuntimeError("TTS model is not loaded.")
    key = voice_name.lower().strip()
    if key not in voice_cache:
        print(f"[ Voices ] Caching voice: {key}")
        voice_cache[key] = model.get_state_for_audio_prompt(key)
        print(f"[ Voices ] Voice cached: {key}")
    return voice_cache[key]


# ── In-Memory Response & Chunk Cache (LRU) ───────────────────────────────────
def get_cache_key(voice: str, text: str) -> str:
    normalized = " ".join(text.split()).strip()
    h = hashlib.sha256(normalized.encode("utf-8")).hexdigest()[:16]
    return f"{voice}:{h}"


def cache_get(key: str) -> Optional[bytes]:
    if key in response_cache:
        response_cache.move_to_end(key)
        return response_cache[key]
    return None


def cache_put(key: str, val: bytes) -> None:
    response_cache[key] = val
    response_cache.move_to_end(key)
    if len(response_cache) > CACHE_MAX_ENTRIES:
        response_cache.popitem(last=False)


# ── Complex Words & Speech Normalization Engine ──────────────────────────────
ACRONYM_MAP = {
    "TTS": "T T S",
    "API": "A P I",
    "HTML": "H T M L",
    "CSS": "C S S",
    "JSON": "Jason",
    "SQL": "sequel",
    "CLI": "C L I",
    "SDK": "S D K",
    "HTTP": "H T T P",
    "HTTPS": "H T T P S",
    "URL": "U R L",
    "LLM": "L L M",
    "CPU": "C P U",
    "GPU": "G P U",
    "RAM": "ram",
    "ROM": "rom",
    "GUI": "gooey",
    "FAQ": "F A Q",
    "UUID": "U U I D",
    "PDF": "P D F",
    "PNG": "P N G",
    "JPG": "J peg",
    "JPEG": "J peg",
    "WAV": "wave",
    "MP3": "M P 3",
    "MP4": "M P 4",
    "OS": "O S",
    "AI": "A I",
    "ML": "M L",
    "UI": "U I",
    "UX": "U X",
    "RGB": "R G B",
    "RGBA": "R G B A",
}


def normalize_complex_words(text: str) -> str:
    """Intelligently normalize complex words, acronyms, technical terms, currencies, units, and symbols for natural neural speech."""
    if not text:
        return ""

    original_text = text

    # Currencies
    text = re.sub(r'\$(\d+(?:\.\d+)?)', r'\1 dollars', text)
    text = re.sub(r'€(\d+(?:\.\d+)?)', r'\1 euros', text)
    text = re.sub(r'£(\d+(?:\.\d+)?)', r'\1 pounds', text)
    text = re.sub(r'¥(\d+(?:\.\d+)?)', r'\1 yen', text)

    # Software versions: v1.1 -> version 1.1, V2.0 -> version 2.0
    text = re.sub(r'\b[vV](\d+(?:\.\d+)+)\b', r'version \1', text)

    # Programming languages and ecosystems
    text = re.sub(r'\bC#(?!\w)', 'C sharp', text)
    text = re.sub(r'\bC\+\+(?!\w)', 'C plus plus', text)
    text = re.sub(r'(?:\b|\s)\.NET\b', ' dot net', text, flags=re.IGNORECASE)
    text = re.sub(r'\bNode\.js\b', 'Node dot J S', text, flags=re.IGNORECASE)

    # Standard Latin and English abbreviations
    text = re.sub(r'\be\.g\.,?\s*', 'for example, ', text, flags=re.IGNORECASE)
    text = re.sub(r'\bi\.e\.,?\s*', 'that is, ', text, flags=re.IGNORECASE)
    text = re.sub(r'\betc\.\b', 'etcetera', text, flags=re.IGNORECASE)
    text = re.sub(r'\bvs\.\b|\bvs\b', 'versus', text, flags=re.IGNORECASE)
    text = re.sub(r'\bapprox\.\b', 'approximately', text, flags=re.IGNORECASE)
    text = re.sub(r'\bdept\.\b', 'department', text, flags=re.IGNORECASE)
    text = re.sub(r'\bDr\.\b', 'Doctor', text)
    text = re.sub(r'\bMr\.\b', 'Mister', text)
    text = re.sub(r'\bMrs\.\b', 'Missus', text)
    text = re.sub(r'\bMs\.\b', 'Miss', text)
    text = re.sub(r'\bProf\.\b', 'Professor', text)

    # Measurements & Units
    text = re.sub(r'(\d+)\s*(?:km/h|kmh)\b', r'\1 kilometers per hour', text, flags=re.IGNORECASE)
    text = re.sub(r'(\d+)\s*mph\b', r'\1 miles per hour', text, flags=re.IGNORECASE)
    text = re.sub(r'(\d+)\s*(?:GB|Gb)\b', r'\1 gigabytes', text)
    text = re.sub(r'(\d+)\s*(?:MB|Mb)\b', r'\1 megabytes', text)
    text = re.sub(r'(\d+)\s*(?:KB|Kb)\b', r'\1 kilobytes', text)
    text = re.sub(r'(\d+)\s*(?:TB|Tb)\b', r'\1 terabytes', text)
    text = re.sub(r'(\d+)\s*(?:kHz|khz)\b', r'\1 kilohertz', text, flags=re.IGNORECASE)
    text = re.sub(r'(\d+)\s*(?:MHz|mhz)\b', r'\1 megahertz', text, flags=re.IGNORECASE)
    text = re.sub(r'(\d+)\s*(?:GHz|ghz)\b', r'\1 gigahertz', text, flags=re.IGNORECASE)
    text = re.sub(r'(\d+)\s*ms\b', r'\1 milliseconds', text)
    text = re.sub(r'(\d+)\s*fps\b', r'\1 frames per second', text, flags=re.IGNORECASE)
    text = re.sub(r'(\d+)\s*px\b', r'\1 pixels', text, flags=re.IGNORECASE)
    text = re.sub(r'(\d+)\s*cm\b', r'\1 centimeters', text, flags=re.IGNORECASE)
    text = re.sub(r'(\d+)\s*mm\b', r'\1 millimeters', text, flags=re.IGNORECASE)
    text = re.sub(r'(\d+)\s*kg\b', r'\1 kilograms', text, flags=re.IGNORECASE)

    # Complex words from pronunciation dictionary (longest phrases first)
    if pronunciation_dict:
        sorted_words = sorted(pronunciation_dict.keys(), key=lambda w: len(w), reverse=True)
        for word in sorted_words:
            pron = pronunciation_dict[word]
            pattern = r'\b' + re.escape(word) + r'\b'
            text = re.sub(pattern, pron, text, flags=re.IGNORECASE)

    # Tech acronyms
    for acr, pron in ACRONYM_MAP.items():
        text = re.sub(r'\b' + acr + r'\b', pron, text)

    # Spoken symbols (slashes, math, etc.)
    text = re.sub(r'/', ' slash ', text)
    text = re.sub(r'\\', ' backslash ', text)
    text = re.sub(r'\+', ' plus ', text)
    text = re.sub(r'=', ' equals ', text)
    text = re.sub(r'%', ' percent ', text)
    text = re.sub(r'&', ' and ', text)
    text = re.sub(r'@', ' at ', text)
    text = re.sub(r'#(\w+)', r'hash \1', text)
    text = re.sub(r'#', ' hash ', text)
    text = re.sub(r'_', ' underscore ', text)
    text = re.sub(r'\|', ' pipe ', text)
    text = re.sub(r'~', ' tilde ', text)
    text = re.sub(r'\*', ' asterisk ', text)

    # Clean whitespace and keep prosody punctuation tight
    text = re.sub(r'[ \t]+', ' ', text)
    text = re.sub(r'\s+([,.:;?!])', r'\1', text)
    normalized = text.strip()

    if normalized != original_text:
        preview_orig = (original_text[:40] + "...") if len(original_text) > 40 else original_text
        preview_norm = (normalized[:40] + "...") if len(normalized) > 40 else normalized
        print(f"[ Voices ] Phonetically normalized: \"{preview_orig}\" -> \"{preview_norm}\"")

    return normalized


# ── Sentence Chunking Helper ──────────────────────────────────────────────────
def split_into_sentences(text: str) -> List[str]:
    """Split text cleanly into conversational sentence chunks."""
    text = text.strip()
    if not text:
        return []

    # Split on sentence boundaries: periods, exclamation marks, questions, newlines
    raw_chunks = re.split(r'(?<=[.!?…])\s+|\n{2,}', text)
    sentences: List[str] = []

    for chunk in raw_chunks:
        chunk = chunk.strip()
        if not chunk:
            continue

        if len(chunk) > 200:
            sub_chunks = re.split(r'(?<=[,;:\-])\s+', chunk)
            current = ""
            for sub in sub_chunks:
                if len(current) + len(sub) < 180:
                    current = f"{current} {sub}".strip()
                else:
                    if current:
                        sentences.append(current)
                    current = sub
            if current:
                sentences.append(current)
        else:
            sentences.append(chunk)

    return sentences if sentences else [text]


def audio_tensor_to_wav(audio_np: Any, sample_rate: Optional[int] = None) -> bytes:
    """Encode numpy audio array to standard WAV bytes."""
    if not HAS_SCIPY:
        raise RuntimeError("scipy is required for WAV encoding.")
    buf = io.BytesIO()
    sr = sample_rate or getattr(model, "sample_rate", DEFAULT_SAMPLE_RATE)
    scipy.io.wavfile.write(buf, sr, audio_np)
    buf.seek(0)
    return buf.getvalue()


# ── Pydantic Request Models ───────────────────────────────────────────────────
class TTSRequest(BaseModel):
    text: str = Field(..., min_length=1, description="Text to synthesize into speech")
    voice: str = Field(default="alba", description="Voice persona identifier")

    model_config = {"extra": "ignore"}


class PronunciationRequest(BaseModel):
    word: str = Field(..., min_length=1, description="Word or phrase to normalize")
    pronunciation: str = Field(..., min_length=1, description="Spoken phonetic respelling")

    model_config = {"extra": "ignore"}


# ── Lifespan Management ───────────────────────────────────────────────────────
@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup actions
    load_pronunciation_dict()
    load_tts_model()
    yield
    # Shutdown actions (if any)
    voice_cache.clear()
    response_cache.clear()


# ── Application Factory ───────────────────────────────────────────────────────
app = FastAPI(
    title="Voices - Local Neural TTS Bridge",
    version="1.1.0",
    description="Offline neural speech synthesis bridge powered by Pocket TTS",
    lifespan=lifespan,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


# ── Endpoints ─────────────────────────────────────────────────────────────────
@app.get("/", tags=["Info"])
async def root():
    return {
        "service": "Voices - Local Neural TTS Bridge",
        "version": "1.1.0",
        "status": "ready" if model is not None else "degraded",
        "docs_url": "/docs",
        "health_url": "/health",
    }


@app.get("/health", tags=["Monitoring"])
async def health():
    return {
        "status": "ok" if model is not None else "degraded",
        "version": "1.1.0",
        "model_loaded": model is not None,
        "model_error": model_error,
        "cached_voices": list(voice_cache.keys()),
        "response_cache_items": len(response_cache),
        "trained_complex_words": len(pronunciation_dict),
    }


@app.get("/pronunciation", tags=["Pronunciation"])
async def get_pronunciations():
    """Retrieve all trained complex words and phonetic respellings."""
    return {
        "total": len(pronunciation_dict),
        "words": pronunciation_dict,
    }


@app.post("/pronunciation", tags=["Pronunciation"])
async def train_pronunciation(req: PronunciationRequest):
    """Train or register a new complex word with custom spoken pronunciation."""
    word = req.word.strip().lower()
    pron = req.pronunciation.strip()

    if not word or not pron:
        return Response(
            status_code=status.HTTP_400_BAD_REQUEST,
            content="Both 'word' and 'pronunciation' must be provided.",
        )

    pronunciation_dict[word] = pron
    save_pronunciation_dict()
    print(f"[ Voices ] Trained new word: '{word}' -> '{pron}'")
    return {
        "status": "ok",
        "word": word,
        "pronunciation": pron,
        "total_trained": len(pronunciation_dict),
    }


@app.post("/tts", tags=["TTS"])
async def tts(req: TTSRequest):
    """Generate audio WAV file for the requested text and voice."""
    if model is None:
        error_msg = f"TTS model unavailable: {model_error or 'Model not initialized'}"
        return JSONResponse(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            content={"error": error_msg},
        )

    raw_text = req.text.strip()
    voice = req.voice.lower().strip()

    if not raw_text:
        return Response(status_code=status.HTTP_400_BAD_REQUEST, content="No text provided")

    if voice not in KNOWN_VOICES:
        return Response(
            status_code=status.HTTP_400_BAD_REQUEST,
            content=f"Unknown voice: '{voice}'. Available voices: {', '.join(KNOWN_VOICES)}",
        )

    # Normalize complex words, technical terms, symbols, and irregular pronunciations
    text = normalize_complex_words(raw_text)

    full_key = get_cache_key(voice, text)
    cached_wav = cache_get(full_key)
    if cached_wav:
        return Response(content=cached_wav, media_type="audio/wav", headers={"X-Cache": "HIT"})

    try:
        state = get_voice_state(voice)
    except Exception as e:
        return JSONResponse(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            content={"error": f"Failed to retrieve voice state: {e}"},
        )

    sentences = split_into_sentences(text)

    try:
        if len(sentences) <= 1:
            audio = model.generate_audio(state, text)
            wav_bytes = audio_tensor_to_wav(audio.numpy())
            cache_put(full_key, wav_bytes)
            return Response(content=wav_bytes, media_type="audio/wav", headers={"X-Cache": "MISS"})

        # Process chunks and concatenate with natural silence pause
        audio_segments = []
        sr = getattr(model, "sample_rate", DEFAULT_SAMPLE_RATE)
        silence = np.zeros(int(sr * 0.12), dtype=np.float32) if HAS_NUMPY else None

        for i, s in enumerate(sentences):
            s_key = get_cache_key(voice, s)
            s_cached = cache_get(s_key)

            chunk_audio = model.generate_audio(state, s).numpy()
            audio_segments.append(chunk_audio)
            if i < len(sentences) - 1 and silence is not None:
                audio_segments.append(silence)

            if not s_cached:
                cache_put(s_key, audio_tensor_to_wav(chunk_audio))

        combined = np.concatenate(audio_segments) if HAS_NUMPY else audio_segments[0]
        full_wav = audio_tensor_to_wav(combined)
        cache_put(full_key, full_wav)

        return Response(content=full_wav, media_type="audio/wav", headers={"X-Cache": "MISS"})
    except Exception as e:
        return JSONResponse(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            content={"error": f"Speech generation error: {e}"},
        )


@app.post("/tts/stream", tags=["TTS"])
async def tts_stream(req: TTSRequest):
    """Server-Sent Events streaming endpoint yielding each sentence audio chunk immediately."""
    if model is None:
        error_msg = f"TTS model unavailable: {model_error or 'Model not initialized'}"
        return JSONResponse(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            content={"error": error_msg},
        )

    raw_text = req.text.strip()
    voice = req.voice.lower().strip()

    if not raw_text:
        return Response(status_code=status.HTTP_400_BAD_REQUEST, content="No text provided")

    if voice not in KNOWN_VOICES:
        return Response(
            status_code=status.HTTP_400_BAD_REQUEST,
            content=f"Unknown voice: '{voice}'. Available voices: {', '.join(KNOWN_VOICES)}",
        )

    # Normalize complex words, technical terms, symbols, and irregular pronunciations
    text = normalize_complex_words(raw_text)

    try:
        state = get_voice_state(voice)
    except Exception as e:
        return JSONResponse(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            content={"error": f"Failed to retrieve voice state: {e}"},
        )

    sentences = split_into_sentences(text)
    total = len(sentences)

    async def event_generator():
        for i, sentence in enumerate(sentences):
            s_key = get_cache_key(voice, sentence)
            cached_wav = cache_get(s_key)
            if cached_wav:
                wav_bytes = cached_wav
                cache_hit = True
            else:
                try:
                    audio = model.generate_audio(state, sentence)
                    wav_bytes = audio_tensor_to_wav(audio.numpy())
                    cache_put(s_key, wav_bytes)
                    cache_hit = False
                except Exception as chunk_err:
                    print(f"[ Voices ] Chunk error on sentence {i}: {chunk_err}")
                    continue

            b64_audio = base64.b64encode(wav_bytes).decode("ascii")
            payload = json.dumps({
                "index": i,
                "total": total,
                "text": sentence,
                "audio": b64_audio,
                "cached": cache_hit,
            })
            yield f"data: {payload}\n\n"

        yield f"data: {json.dumps({'event': 'done', 'total': total})}\n\n"

    return StreamingResponse(
        event_generator(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
        },
    )


# ── Entry Point ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    host = os.environ.get("VOICES_HOST", "127.0.0.1")
    port = int(os.environ.get("VOICES_PORT", "8000"))
    print(f"[ Voices ] Starting server on http://{host}:{port} ...")
    uvicorn.run("server:app", host=host, port=port, reload=False)
