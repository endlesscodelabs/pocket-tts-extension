let currentAudio = null;
let audioQueue = [];
let playbackSpeed = 1.0;
let isStreamActive = false;

function b64ToBlob(b64Data, contentType = "audio/wav") {
  const byteCharacters = atob(b64Data);
  const len = byteCharacters.length;
  const bytes = new Uint8Array(len);
  for (let i = 0; i < len; i++) {
    bytes[i] = byteCharacters.charCodeAt(i);
  }
  return new Blob([bytes], { type: contentType });
}

function notifyEnded() {
  if (!isStreamActive && audioQueue.length === 0 && !currentAudio) {
    chrome.runtime.sendMessage({ type: "audio-ended" }).catch(() => {});
  }
}

function playNextInQueue() {
  if (audioQueue.length === 0) {
    currentAudio = null;
    notifyEnded();
    return;
  }

  const nextBlob = audioQueue.shift();
  const url = URL.createObjectURL(nextBlob);
  const audio = new Audio(url);
  audio.playbackRate = playbackSpeed;
  currentAudio = audio;

  const cleanup = () => {
    URL.revokeObjectURL(url);
    if (currentAudio === audio) {
      currentAudio = null;
    }
    playNextInQueue();
  };

  audio.addEventListener("ended", cleanup);
  audio.addEventListener("error", (e) => {
    console.warn("Offscreen audio playback error:", e);
    cleanup();
  });

  audio.play().catch((err) => {
    console.warn("Offscreen audio playback play() failed:", err);
    cleanup();
  });
}

function stopAudio() {
  isStreamActive = false;
  audioQueue = [];
  if (currentAudio) {
    try {
      currentAudio.pause();
      currentAudio.removeAttribute("src");
      currentAudio.load();
    } catch (_) {}
    currentAudio = null;
  }
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.target !== "offscreen") return;

  if (msg.type === "start-playback") {
    stopAudio();
    isStreamActive = true;
    if (msg.speed) playbackSpeed = msg.speed;
    sendResponse({ ok: true });
  } else if (msg.type === "enqueue-audio") {
    if (msg.speed) playbackSpeed = msg.speed;
    try {
      const blob = b64ToBlob(msg.audio);
      audioQueue.push(blob);
      if (!currentAudio) {
        playNextInQueue();
      }
      sendResponse({ ok: true });
    } catch (e) {
      console.error("Offscreen failed to decode audio blob:", e);
      sendResponse({ ok: false, error: e.message });
    }
  } else if (msg.type === "stream-finished") {
    isStreamActive = false;
    if (!currentAudio && audioQueue.length === 0) {
      notifyEnded();
    }
    sendResponse({ ok: true });
  } else if (msg.type === "stop-audio") {
    stopAudio();
    sendResponse({ ok: true });
  }
  return true;
});
