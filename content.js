// Voices Browser Content Script
// Captures selected text upon pressing Alt + C directly in the page

function getSelectedText() {
  let text = window.getSelection()?.toString()?.trim() || "";

  if (!text && document.activeElement) {
    const el = document.activeElement;
    if (typeof el.selectionStart === "number" && typeof el.selectionEnd === "number" && el.selectionStart !== el.selectionEnd) {
      text = el.value.substring(el.selectionStart, el.selectionEnd).trim();
    } else if (el.value) {
      text = el.value.trim();
    } else if (el.isContentEditable) {
      text = el.innerText?.trim() || "";
    }
  }

  return text;
}

// Respond to background querying selection
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === "get-selection-text") {
    sendResponse({ text: getSelectedText() });
  }
  return false;
});

// In-page fallback listener for Alt + C
let lastKeypressTime = 0;

document.addEventListener("keydown", (e) => {
  if (e.altKey && !e.ctrlKey && !e.metaKey && (e.code === "KeyC" || e.key === "c" || e.key === "C")) {
    const now = Date.now();
    if (now - lastKeypressTime < 400) return; // Prevent rapid duplicate triggers
    lastKeypressTime = now;

    const text = getSelectedText();
    try {
      chrome.runtime.sendMessage({
        type: "shortcut-triggered",
        text: text
      }).catch(() => {});
    } catch (_) {}
  }
}, true);
