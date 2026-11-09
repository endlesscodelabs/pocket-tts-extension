# 🎙️ Voices: FastAPI Neural TTS Server

A lightweight, local-first FastAPI neural text-to-speech server powered by `pocket_tts`. Provides instant voice synthesis for both the Voices browser extension and the WinUI 3 desktop companion.

---

## 🚀 Key Capabilities

- **Offline Inference:** Runs locally on your machine with zero external network or cloud dependencies.
- **8 Neural Voice Personas:** `alba` (default narrator), `fantine`, `jean`, `marius`, `javert`, `cosette`, `eponine`, and `azelma`.
- **Low-Latency SSE Streaming:** `/tts/stream` uses Server-Sent Events with sentence boundary chunking to stream audio as each sentence completes synthesis.
- **Phonetic & Technical Normalization:** Automatically converts tech acronyms (`TTS`, `API`, `JSON`, `SQL`), programming symbols (`C#`, `C++`, `.NET`), currencies, units, and custom phonetic vocabulary.
- **LRU Audio & Voice Caching:** In-memory 256-entry LRU response cache and RAM-cached voice embeddings for zero-delay repeated queries.
- **Trainable Pronunciation Dictionary:** Persisted custom phonetic mappings in `pronunciation_dict.json`.

---

## 🛠️ Setup & Installation

### Prerequisites
- Python 3.10, 3.11, or 3.12 (Recommended)
- Windows, macOS, or Linux

### Installation

```bash
cd server
pip install -r requirements.txt
```

---

## 🏃 Running the Server

### Option A: One-Click Script (Windows)
Double-click `run.bat` in the `server/` directory.

### Option B: Command Line
```bash
python server.py
# Or with uvicorn directly:
uvicorn server:app --host 127.0.0.1 --port 8000
```

The server binds to `http://127.0.0.1:8000`. You can configure host and port using `VOICES_HOST` and `VOICES_PORT` environment variables.

---

## 📡 API Reference

### 1. `GET /health`
Returns server operational status, model readiness, and cache telemetry.
```json
{
  "status": "ok",
  "version": "1.1.0",
  "model_loaded": true,
  "cached_voices": ["alba"],
  "response_cache_items": 20,
  "trained_complex_words": 82
}
```

### 2. `POST /tts`
Synthesizes full text to an `audio/wav` binary payload.
```json
// Request Body
{
  "text": "Voices brings text to life with ultra-low latency neural speech.",
  "voice": "alba"
}
```

### 3. `POST /tts/stream`
Streams synthesized sentence chunks as Server-Sent Events (`text/event-stream`).
```text
data: {"index": 0, "total": 2, "text": "First sentence.", "audio": "<base64_wav>", "cached": false}

data: {"index": 1, "total": 2, "text": "Second sentence.", "audio": "<base64_wav>", "cached": false}

data: {"event": "done", "total": 2}
```

### 4. `GET /pronunciation` & `POST /pronunciation`
Retrieve or train custom phonetic overrides saved to `pronunciation_dict.json`.
```json
// POST /pronunciation
{
  "word": "kubernetes",
  "pronunciation": "koo-ber-net-ees"
}
```
