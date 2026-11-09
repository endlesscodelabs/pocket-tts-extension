# Project Specification: Voices Desktop (WinUI 3 / .NET 8)

## 1. Goal
Create a lightweight, frameless, native Windows 11 desktop application in C# (.NET 8) and WinUI 3 (Windows App SDK) that reads selected text out loud from any running application using a local FastAPI TTS server.

## 2. Technical Stack
- Framework: .NET 8, Windows App SDK / WinUI 3
- Audio Engine: Windows.Media.Playback.MediaPlayer
- Material Design: Windows 11 Mica / Acrylic backdrop, rounded corners, dark mode default

## 3. Core Modules & Logic

### A. Global Hotkey & Clipboard Interop
- Register a global hotkey (default: Alt + C) using Win32 `RegisterHotKey`.
- When triggered:
  1. Save existing clipboard text to a temporary variable.
  2. Simulate `Ctrl + C` using Win32 `SendInput` to grab current selection.
  3. Wait 80ms, extract the string from clipboard.
  4. Restore the original clipboard text.

### B. Local TTS Client
- Send a POST request to `http://127.0.0.1:8000/tts` with JSON payload:
  ```json
  {
    "text": "<captured_text>",
    "voice": "Alba",
    "speed": 1.0
  }