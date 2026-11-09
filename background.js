const ext = typeof chrome !== "undefined" ? chrome : browser;
const DEFAULT_SERVER = "http://127.0.0.1:8000";

// ── Context Menu ─────────────────────────────────────────────────────────────
function initContextMenu() {
  if (!ext.contextMenus) return;
  ext.contextMenus.removeAll(() => {
    if (ext.runtime.lastError) { /* ignore */ }
    ext.contextMenus.create({
      id: "read-with-voices",
      title: "Read with Voices",
      contexts: ["selection", "editable"]
    }, () => {
      if (ext.runtime.lastError) { /* ignore */ }
    });
  });
}

ext.runtime.onInstalled.addListener(() => {
  initContextMenu();
});
initContextMenu();

// ── Offscreen Document for MV3 Audio ─────────────────────────────────────────
let creatingOffscreenPromise = null;

async function ensureOffscreenDocument() {
  if (typeof ext.offscreen === "undefined") return;

  if (await ext.offscreen.hasDocument?.()) {
    return;
  }

  if (creatingOffscreenPromise) {
    await creatingOffscreenPromise;
    return;
  }

  try {
    creatingOffscreenPromise = ext.offscreen.createDocument({
      url: "offscreen.html",
      reasons: [ext.offscreen.Reason.AUDIO_PLAYBACK],
      justification: "Playback offline neural TTS audio"
    });
    await creatingOffscreenPromise;
  } catch (err) {
    if (!err.message?.includes("Only a single offscreen document")) {
      console.warn("Offscreen document creation error:", err);
    }
  } finally {
    creatingOffscreenPromise = null;
  }
}

// ── Playback State & Action Feedback ──────────────────────────────────────────
let isPlaying = false;
let currentSpokenText = "";
let currentAbortController = null;
let lastTriggerTime = 0;

function showActionBadge(text, color, title = "") {
  try {
    if (ext.action) {
      ext.action.setBadgeText({ text });
      ext.action.setBadgeBackgroundColor({ color });
      if (title) ext.action.setTitle({ title });
      setTimeout(() => {
        try {
          ext.action.setBadgeText({ text: "" });
          ext.action.setTitle({ title: "Voices" });
        } catch (_) {}
      }, 3500);
    }
  } catch (_) {}
}

function notifyStateChange() {
  try {
    ext.runtime.sendMessage({
      type: "playback-state",
      isPlaying,
      text: currentSpokenText
    }).catch(() => {});
  } catch (_) {}
}

async function stopPlayback() {
  if (currentAbortController) {
    currentAbortController.abort();
    currentAbortController = null;
  }

  try {
    await ext.runtime.sendMessage({
      target: "offscreen",
      type: "stop-audio"
    }).catch(() => {});
  } catch (_) {}

  isPlaying = false;
  currentSpokenText = "";
  notifyStateChange();
}

// High-performance chunked ArrayBuffer to Base64 conversion
function arrayBufferToBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  const CHUNK_SIZE = 0x8000; // 32768 bytes per chunk
  let binary = "";
  for (let i = 0; i < bytes.length; i += CHUNK_SIZE) {
    const chunk = bytes.subarray(i, i + CHUNK_SIZE);
    binary += String.fromCharCode.apply(null, chunk);
  }
  return btoa(binary);
}

// ── Core TTS function ─────────────────────────────────────────────────────────
async function speakText(text) {
  if (!text || !text.trim()) {
    showActionBadge("?", "#6b7280", "Voices: No text selected");
    return;
  }

  await stopPlayback();
  await ensureOffscreenDocument();

  const config = await ext.storage.local.get({
    voice: "alba",
    speed: 1.0,
    serverUrl: DEFAULT_SERVER
  });
  const server = config.serverUrl || DEFAULT_SERVER;

  isPlaying = true;
  currentSpokenText = text.trim();
  notifyStateChange();

  currentAbortController = new AbortController();
  let chunksEnqueued = 0;

  // Signal offscreen player that a new playback session started
  try {
    await ext.runtime.sendMessage({
      target: "offscreen",
      type: "start-playback",
      speed: config.speed
    }).catch(() => {});
  } catch (_) {}

  // 1. Try Streaming Endpoint
  let streamingSucceeded = false;
  try {
    const res = await fetch(`${server}/tts/stream`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text: currentSpokenText, voice: config.voice }),
      signal: currentAbortController.signal
    });

    if (res.ok && res.body) {
      const reader = res.body.getReader();
      const decoder = new TextDecoder("utf-8");
      let buffer = "";

      while (true) {
        const { value, done } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n\n");
        buffer = lines.pop() || "";

        for (const line of lines) {
          const trimmed = line.trim();
          if (!trimmed.startsWith("data:")) continue;
          const jsonStr = trimmed.slice(5).trim();
          try {
            const data = JSON.parse(jsonStr);
            if (data.audio) {
              chunksEnqueued++;
              await ext.runtime.sendMessage({
                target: "offscreen",
                type: "enqueue-audio",
                audio: data.audio,
                speed: config.speed
              }).catch(() => {});
            }
          } catch (_) {}
        }
      }

      await ext.runtime.sendMessage({
        target: "offscreen",
        type: "stream-finished"
      }).catch(() => {});

      currentAbortController = null;
      streamingSucceeded = true;
      return;
    }
  } catch (err) {
    if (err.name === "AbortError") return;
    console.warn("Voices: Streaming failed, checking fallback:", err);
  }

  // 2. Standard Endpoint Fallback (only if no chunks were enqueued)
  if (!streamingSucceeded && chunksEnqueued === 0) {
    try {
      if (currentAbortController?.signal.aborted) return;

      const res = await fetch(`${server}/tts`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ text: currentSpokenText, voice: config.voice }),
        signal: currentAbortController?.signal
      });

      if (!res.ok) throw new Error(`HTTP ${res.status}`);

      const buffer = await res.arrayBuffer();
      const b64 = arrayBufferToBase64(buffer);

      await ext.runtime.sendMessage({
        target: "offscreen",
        type: "enqueue-audio",
        audio: b64,
        speed: config.speed
      }).catch(() => {});

      await ext.runtime.sendMessage({
        target: "offscreen",
        type: "stream-finished"
      }).catch(() => {});

      currentAbortController = null;
    } catch (err) {
      if (err.name !== "AbortError") {
        console.error("Voices TTS error:", err);
        showActionBadge("ERR", "#ef4444", "Voices: Server offline or error");
      }
      await stopPlayback();
    }
  } else if (!streamingSucceeded) {
    // If stream failed mid-way, stop cleanly
    await stopPlayback();
  }
}

// ── Extract text from tab (Manifest V3) ────────────────────────────────────────
async function extractText(info, tab) {
  let text = info?.selectionText ? info.selectionText.trim() : "";
  if (!text && tab && tab.id) {
    // 1. Try querying content script first
    try {
      const response = await ext.tabs.sendMessage(tab.id, { type: "get-selection-text" });
      if (response && response.text) {
        text = response.text.trim();
      }
    } catch (_) {}

    // 2. Fall back to scripting execution if content script is unavailable
    if (!text) {
      try {
        const results = await ext.scripting.executeScript({
          target: { tabId: tab.id },
          func: () => {
            const sel = window.getSelection()?.toString()?.trim();
            if (sel) return sel;
            const el = document.activeElement;
            if (el) {
              if (typeof el.selectionStart === "number" && typeof el.selectionEnd === "number" && el.selectionStart !== el.selectionEnd) {
                return el.value.substring(el.selectionStart, el.selectionEnd).trim();
              }
              if (el.value) return el.value.trim();
              if (el.isContentEditable) return el.innerText?.trim() || "";
            }
            return "";
          }
        });
        if (results && results[0] && results[0].result) {
          text = results[0].result.trim();
        }
      } catch (e) {
        console.warn("Voices: could not read text from tab:", e);
      }
    }
  }
  return text;
}

// ── Debounced Shortcut Dispatcher ─────────────────────────────────────────────
async function handleShortcutTrigger(tabFromCommand = null, textFromContent = null) {
  const now = Date.now();
  if (now - lastTriggerTime < 450) {
    // Duplicate event fired within debouncing window (e.g. commands + content script keydown)
    return;
  }
  lastTriggerTime = now;

  if (isPlaying) {
    await stopPlayback();
    return;
  }

  let text = textFromContent ? textFromContent.trim() : "";
  if (!text) {
    let tab = tabFromCommand;
    if (!tab) {
      const [activeTab] = await ext.tabs.query({ active: true, currentWindow: true });
      tab = activeTab;
    }
    if (tab) {
      text = await extractText({}, tab);
    }
  }

  if (text) {
    await speakText(text);
  } else {
    showActionBadge("?", "#6b7280", "Voices: No text selected");
  }
}

// ── Context Menu Listener ─────────────────────────────────────────────────────
if (ext.contextMenus && ext.contextMenus.onClicked) {
  ext.contextMenus.onClicked.addListener(async (info, tab) => {
    if (info.menuItemId !== "read-with-voices") return;
    const text = await extractText(info, tab);
    if (text) {
      speakText(text);
    } else {
      showActionBadge("?", "#6b7280", "Voices: No text selected");
    }
  });
}

// ── Keyboard Shortcut Command Listener ─────────────────────────────────────────
if (ext.commands && ext.commands.onCommand) {
  ext.commands.onCommand.addListener(async (command) => {
    if (command === "read-selected-text") {
      const [tab] = await ext.tabs.query({ active: true, currentWindow: true });
      await handleShortcutTrigger(tab, null);
    }
  });
}

// ── Runtime Messages ──────────────────────────────────────────────────────────
ext.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.target === "offscreen") return; // Handled by offscreen.js

  if (msg.type === "shortcut-triggered") {
    handleShortcutTrigger(sender.tab || null, msg.text || null);
    sendResponse({ ok: true });
  } else if (msg.type === "speak-text" && msg.text) {
    speakText(msg.text);
    sendResponse({ ok: true });
  } else if (msg.type === "stop-playback") {
    stopPlayback();
    sendResponse({ ok: true });
  } else if (msg.type === "get-playback-state") {
    sendResponse({
      isPlaying,
      text: currentSpokenText
    });
  } else if (msg.type === "audio-ended") {
    if (!currentAbortController) {
      isPlaying = false;
      currentSpokenText = "";
      notifyStateChange();
    }
    sendResponse({ ok: true });
  }
  return true;
});
