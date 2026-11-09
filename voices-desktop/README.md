# Voices Desktop (WinUI 3 / .NET 8)

A lightweight, frameless, native Windows 11 desktop application that reads selected text out loud from any running application using a local FastAPI TTS server (`pocket-tts`).

Created as a standalone companion desktop application alongside the Voices browser extension.

---

## 🚀 Key Features

- **Frameless Windows 11 Design:**
  - Modern Windows 11 Mica backdrop (`MicaBackdrop`) with dark mode default and subtle specular borders.
  - Floating pill-card layout with custom draggable header bar and sleek window management.
  - Rhythmic 5-bar waveform visualizer that dynamically pulses during speech synthesis and playback.

- **Global Hotkey Interop (`Alt + C`):**
  - Works across **any** active Windows desktop application (browsers, Word, Notepad, IDEs, PDF viewers).
  - Implements non-destructive selection capture:
    1. Saves existing clipboard text to memory.
    2. Simulates `Ctrl + C` using Win32 `SendInput`.
    3. Asynchronously waits 80ms to extract the active selection.
    4. Automatically restores the original clipboard content.

- **Native Windows Audio Engine:**
  - Powered by `Windows.Media.Playback.MediaPlayer`.
  - Zero-lag in-memory streaming via `InMemoryRandomAccessStream`.
  - Supports Play, Pause, Stop, and dynamic speech rate adjustments.
  - Instant toggle: pressing `Alt + C` while audio is already playing stops current speech.

- **Local Neural TTS Integration:**
  - Connects to the local FastAPI TTS server at `http://127.0.0.1:8000`.
  - Auto-pings `GET /health` with a live glowing status dot (Green = Online, Red = Offline with quick start guide).
  - Sends POST requests to `http://127.0.0.1:8000/tts` with voice and speed parameters.
  - 8 supported neural voices: **Alba, Fantine, Jean, Marius, Javert, Cosette, Eponine, Azelma**.

---

## 📁 Project Structure

```text
voices-desktop/
├── VoicesDesktop.csproj       # .NET 8 WinUI 3 project configuration
├── app.manifest               # DPI PerMonitorV2 & Windows 11 manifest
├── App.xaml                   # Application entry & resources
├── App.xaml.cs                # Application lifecycle
├── MainWindow.xaml            # Frameless Mica UI & visualizer markup
├── MainWindow.xaml.cs         # Hotkey, waveform timer, and event orchestration
├── Models/
│   └── TtsRequest.cs          # TTS payload contract matching FastAPI
├── Services/
│   ├── ClipboardHelper.cs     # Win32 SendInput and clipboard preservation
│   ├── HotkeyManager.cs       # Win32 RegisterHotKey & message loop hook
│   └── TtsService.cs          # HTTP client & MediaPlayer engine
├── Assets/
│   └── logo.png               # Voices branding icon
├── run.bat                    # One-click launch script
└── README.md                  # Documentation
```

---

## 🛠️ Requirements & Getting Started

### 1. Prerequisites
- **Windows 10 (1809+, build 17763+) or Windows 11**
- **.NET 8 SDK** (v8.0+ installed system-wide or in `%USERPROFILE%\dotnet`)
- **Windows App SDK 1.6** (automatically resolved via NuGet packages `Microsoft.WindowsAppSDK` and `Microsoft.Windows.SDK.BuildTools`)
- **FastAPI TTS Server** (`server/server.py` in the root repository)

### 2. Start the Backend TTS Server
In the root directory, start the Python TTS server:
```powershell
py server\server.py
```
*(The server will initialize `pocket-tts` and warm up the default Alba voice cache).*

### 3. Run Voices Desktop
Simply double-click `run.bat` or run:
```powershell
cd voices-desktop
dotnet run
```

---

## ⌨️ How to Use

1. Launch **Voices Desktop**. Confirm that the status indicator in the top header is **Online** (Green).
2. Select text in any window (browser, code editor, document, chat app).
3. Press **`Alt + C`**.
4. Voices Desktop will capture the selection, synthesize speech via the local neural model, and play the audio with real-time waveform visualization.
5. To stop playback immediately at any time, press **`Alt + C`** again or click the Stop button.
