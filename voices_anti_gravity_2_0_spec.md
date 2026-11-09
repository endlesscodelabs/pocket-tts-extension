# Prompt Specification for Anti Gravity 2.0
**Project:** Voices (Local Neural TTS Browser Extension)
**Role:** You are an expert WebExtension and Python developer. Generate the full codebase for the "Voices" extension based on the specifications below.

## 1. Project Architecture
Generate the following file structure in the root directory:
```text
/voices-extension
  ├── manifest.json
  ├── background.js
  ├── popup/
  │   ├── popup.html
  │   ├── popup.css
  │   └── popup.js
  ├── server/
  │   ├── server.py
  │   ├── run.bat
  │   └── generate_icons.py
```

## 2. Core Features & Logic
### A. Backend (`server/server.py`)
*   **Framework:** FastAPI with Uvicorn.
*   **Requirements:** Import `pocket_tts`. Add `CORSMiddleware` (allow all origins, credentials, methods, headers).
*   **Endpoints:**
    *   `GET /health`: Returns `{"status": "ok"}`.
    *   `POST /tts`: Accepts JSON `{text: str, voice: str}`.
*   **Optimization:** Lazily load and cache voice embeddings (Alba, Fantine, Jean, Marius, Javert, Cosette, Eponine, Azelma) in a RAM dictionary so switching voices is instant. Return a WAV blob (`audio/wav`).

### B. Extension Manifest (`manifest.json`)
*   **Version:** Manifest V2 (compatible with Firefox).
*   **Permissions:** `storage`, `contextMenus`, `activeTab`, `http://127.0.0.1:8000/*`.
*   **Commands:** Bind `Alt+Shift+V` to a command named `read-selected-text`.
*   **Action:** Default popup set to `popup/popup.html`.

### C. Background Script (`background.js`)
*   **Context Menu:** Create "Read with Voices" for contexts `["selection", "editable"]`.
*   **Hotkey:** Add a listener for `browser.commands.onCommand` (`read-selected-text`).
*   **Text Extraction:** If `info.selectionText` is empty, fallback to `browser.tabs.executeScript` to grab the active input/textarea value or `window.getSelection()`.
*   **Playback Logic:** 
    *   Fetch `voice` and `speed` from `browser.storage.local`.
    *   Send `POST` to `http://127.0.0.1:8000/tts`.
    *   Play the blob via `new Audio(URL.createObjectURL(blob))`.
    *   *Crucial:* If audio is already playing, triggering the action again must `pause()` and destroy the current audio before starting the new one (or simply stop if it's the same text).

### D. UI/UX: Apple-Style Glassmorphism (`popup.css` & `popup.html`)
*   **Theme:** Auto-adapts to `prefers-color-scheme`.
*   **Glass Panel:**
    *   Use `backdrop-filter: blur(28px) saturate(180%)`.
    *   Background: `rgba(18, 18, 24, 0.72)` (dark mode) / `rgba(255, 255, 255, 0.78)` (light mode).
    *   Border: `1px solid rgba(255,255,255,0.1)` with `box-shadow: inset 0 1px 1px rgba(255, 255, 255, 0.2)` to create a specular edge.
*   **Components:**
    *   **Header:** Logo (V), Title (Voices), and a glowing status dot (Green for Online, Red for Offline).
    *   **Voice Selector:** Custom styled `<select>` or pill.
    *   **Speed Slider:** `0.75x` to `2.0x`.
    *   **Waveform Visualizer:** 5 vertical bars using CSS keyframes (`transform: scaleY(...)`) with staggered animation delays. Idle state = calm/flat. Active state = rhythmic bouncing.
    *   **Action Pill:** "Test Voice" button with dynamic text ("Speaking...").
*   **Offline State:** If `GET /health` fails, show a helper text block under the status dot: `Run: python server.py` (with a copy-to-clipboard function).

### E. Popup Logic (`popup.js`)
*   **Zero-Click Save:** Attach listeners to `input` and `change` on the voice selector and speed slider. Immediately save to `browser.storage.local`. No manual save button.
*   **Status Polling:** Ping `http://127.0.0.1:8000/health` every 5 seconds to update the Green/Red dot live.
*   **Test Playback:** Fetch audio from `/tts`, play it at the selected speed, and trigger the CSS waveform animation class during playback.

### F. Utility Scripts (`server/`)
*   **`generate_icons.py`:** Write a Python script using Pillow to read `logo_master.png`, resize it with `Image.Resampling.LANCZOS`, and export to `../icons/icon-16.png`, 32, 48, and 128.
*   **`run.bat`:** A simple batch script that runs `py server.py` and keeps the terminal open for transparent logs.