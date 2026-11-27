"use strict";

const terminalElement = document.getElementById("terminal");
const bridge = window.chrome.webview;
const terminal = new Terminal({
  allowTransparency: false,
  cursorBlink: false,
  disableStdin: true,
  fontFamily: 'Consolas, "Cascadia Mono", monospace',
  fontSize: 14,
  lineHeight: 1.2,
  scrollback: 3500,
  theme: {
    background: "#0c1119", foreground: "#dae4f3", cursor: "#8af2cc",
    selectionBackground: "#365973", black: "#293548", red: "#ff8e96",
    green: "#8af2cc", yellow: "#fad58f", blue: "#98aff7", magenta: "#dbadf0",
    cyan: "#86d7f3", white: "#dae4f3", brightBlack: "#8798b1",
    brightRed: "#ffa8ad", brightGreen: "#a5f4d8", brightYellow: "#ffe6ad",
    brightBlue: "#b1c4fe", brightMagenta: "#f0c4fe", brightCyan: "#a6eaff",
    brightWhite: "#f3f7fd"
  }
});
const fit = new FitAddon.FitAddon();
terminal.loadAddon(fit);
terminal.open(terminalElement);
terminal.parser.registerOscHandler(52, () => true);
let inputId = null;
function sendInput(text) {
  if (inputId !== null) bridge.postMessage({kind: "input", id: inputId, text});
}
terminal.onData(sendInput);

function resize() {
  fit.fit();
  bridge.postMessage({kind: "size", columns: terminal.cols, rows: terminal.rows});
}
new ResizeObserver(resize).observe(terminalElement);

function copySelection() {
  const text = terminal.getSelection();
  if (text) bridge.postMessage({kind: "copy", text});
}

terminal.attachCustomKeyEventHandler(event => {
  if (event.type !== "keydown" || !event.ctrlKey || event.altKey) return true;
  const pasteKey = event.code === "KeyV" || event.key.toLowerCase() === "v";
  if (pasteKey && inputId !== null) {
    event.preventDefault();
    bridge.postMessage({kind: "paste", id: inputId});
    return false;
  }
  const copyKey = event.code === "KeyC" || event.key.toLowerCase() === "c";
  if (!copyKey || !terminal.hasSelection()) return true;
  event.preventDefault();
  copySelection();
  return false;
});
document.addEventListener("copy", event => {
  if (!terminal.hasSelection()) return;
  event.preventDefault();
  copySelection();
}, true);
document.addEventListener("paste", event => {
  if (inputId === null) return;
  event.preventDefault();
  event.stopImmediatePropagation();
  bridge.postMessage({kind: "paste", id: inputId});
}, true);
terminalElement.addEventListener("pointerdown", () => terminal.focus());

bridge.addEventListener("message", ({data}) => {
  if (data.kind === "reset") {
    terminal.reset();
    terminal.clear();
    resize();
  } else if (data.kind === "paste-text") {
    if (inputId !== null && data.id === inputId) {
      terminal.focus();
      terminal.paste(data.text);
    }
  } else if (data.kind === "input-mode") {
    inputId = data.enabled ? data.id : null;
    terminal.options.disableStdin = !data.enabled;
    if (data.enabled) {
      terminal.focus();
      // ConPTY can announce focus reporting after the WebView has received focus.
      sendInput("\x1b[I");
    }
  } else if (data.kind === "text") {
    terminal.write(data.text, data.start ? () => terminal.scrollToTop() : undefined);
  } else if (data.kind === "bytes") {
    const bytes = Uint8Array.from(atob(data.payload), character => character.charCodeAt(0));
    terminal.write(bytes, () => bridge.postMessage({kind: "flushed", id: data.id}));
  }
});

resize();
bridge.postMessage({kind: "ready"});
