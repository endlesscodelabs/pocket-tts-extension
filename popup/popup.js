const ext = typeof chrome !== "undefined" ? chrome : browser;

// ── DOM References ────────────────────────────────────────────────────────────
const voiceSelect   = document.getElementById("voice-select");
const speedSlider   = document.getElementById("speed-slider");
const speedVal      = document.getElementById("speed-val");
const statusDot     = document.getElementById("status-dot");
const statusText    = document.getElementById("status-text");
const statusBadge   = document.getElementById("status-badge");
const testBtn       = document.getElementById("test-btn");
const btnLabel      = document.getElementById("btn-label");
const waveform      = document.getElementById("waveform");
const headerStopBtn = document.getElementById("header-stop-btn");

const SERVER = "http://127.0.0.1:8000";
let testAudio = null;
let healthInterval = null;

// ── Load Saved Settings ───────────────────────────────────────────────────────
async function loadSavedSettings() {
  try {
    const data = await ext.storage.local.get({ voice: "alba", speed: 1.0 });
    if (data) {
      if (data.voice) voiceSelect.value = data.voice;
      if (data.speed) {
        speedSlider.value = data.speed;
        speedVal.innerText = `${parseFloat(data.speed).toFixed(2)}×`;
      }
    }
  } catch (err) {
    console.warn("Voices: Error loading saved settings:", err);
  }
}
loadSavedSettings();

// ── Zero-Click Save ───────────────────────────────────────────────────────────
voiceSelect.addEventListener("change", () => {
  ext.storage.local.set({ voice: voiceSelect.value });
});

speedSlider.addEventListener("input", () => {
  const v = parseFloat(speedSlider.value).toFixed(2);
  speedVal.innerText = `${v}×`;
  ext.storage.local.set({ speed: parseFloat(v) });
});

// ── Health Check ──────────────────────────────────────────────────────────────
async function checkHealth() {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 2500);

  try {
    const res = await fetch(`${SERVER}/health`, {
      method: "GET",
      signal: controller.signal
    });
    clearTimeout(timer);
    if (res.ok) {
      setStatus(true);
      return true;
    }
  } catch (_) {
    clearTimeout(timer);
  }
  setStatus(false);
  return false;
}

function setStatus(online) {
  statusDot.className  = `status-dot ${online ? "online" : "offline"}`;
  statusText.innerText = online ? "Online" : "Offline";
  if (statusBadge) {
    statusBadge.title = online
      ? "Backend Server is running (127.0.0.1:8000)"
      : "Backend Server Offline — Run: python server.py";
  }
}

checkHealth();
healthInterval = setInterval(checkHealth, 5000);

// ── Waveform & Button Helpers ─────────────────────────────────────────────────
function startWaveform() { waveform.classList.add("active"); }
function stopWaveform()  { waveform.classList.remove("active"); }

function resetTestBtnState() {
  if (testAudio) {
    try {
      testAudio.pause();
      testAudio.removeAttribute("src");
      testAudio.load();
    } catch (_) {}
    testAudio = null;
  }
  stopWaveform();
  btnLabel.innerText = "Test Voice";
  testBtn.disabled = false;
}

// Sync waveform if background is already speaking
try {
  ext.runtime.sendMessage({ type: "get-playback-state" }, (res) => {
    if (ext.runtime.lastError) return;
    if (res && res.isPlaying) startWaveform();
  });
} catch (_) {}

ext.runtime.onMessage.addListener((msg) => {
  if (msg.type === "playback-state") {
    if (msg.isPlaying) {
      startWaveform();
    } else if (!testAudio) {
      stopWaveform();
    }
  }
});

headerStopBtn.addEventListener("click", async () => {
  resetTestBtnState();
  try {
    await ext.runtime.sendMessage({ type: "stop-playback" });
  } catch (_) {}
  stopWaveform();
});

// ── Test Voice ────────────────────────────────────────────────────────────────
testBtn.addEventListener("click", async () => {
  if (testAudio) {
    resetTestBtnState();
    return;
  }

  // Stop any active background speech before starting preview test
  try {
    await ext.runtime.sendMessage({ type: "stop-playback" });
  } catch (_) {}

  testBtn.disabled = true;
  btnLabel.innerText = "Speaking…";
  startWaveform();

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 12000);

  try {
    const res = await fetch(`${SERVER}/tts`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        text: "Hello! I am Voices, your offline neural speech companion.",
        voice: voiceSelect.value
      }),
      signal: controller.signal
    });
    clearTimeout(timer);

    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const audio = new Audio(url);
    audio.playbackRate = parseFloat(speedSlider.value);
    testAudio = audio;

    const cleanup = () => {
      URL.revokeObjectURL(url);
      resetTestBtnState();
    };

    audio.addEventListener("ended", cleanup);
    audio.addEventListener("error", cleanup);

    await audio.play();
    testBtn.disabled = false;
  } catch (err) {
    clearTimeout(timer);
    console.error("Voices TTS error:", err);
    setStatus(false);
    btnLabel.innerText = "Server Offline";
    stopWaveform();
    setTimeout(() => {
      resetTestBtnState();
    }, 2000);
  }
});

// Clean up timers and audio on popup unload
window.addEventListener("unload", () => {
  if (healthInterval) {
    clearInterval(healthInterval);
    healthInterval = null;
  }
  if (testAudio) {
    testAudio.pause();
    testAudio.removeAttribute("src");
    testAudio = null;
  }
});
