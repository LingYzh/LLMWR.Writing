(function () {
  "use strict";

  var PROTOCOL = "llmw-web-bridge";
  var VERSION = 1;
  var sessionId = null;

  function setText(id, text) {
    var el = document.getElementById(id);
    if (el) {
      el.textContent = text;
    }
  }

  function uuid() {
    var bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    var hex = Array.from(bytes, function (b) {
      return b.toString(16).padStart(2, "0");
    }).join("");
    return (
      hex.slice(0, 8) +
      "-" +
      hex.slice(8, 12) +
      "-" +
      hex.slice(12, 16) +
      "-" +
      hex.slice(16, 20) +
      "-" +
      hex.slice(20)
    );
  }

  function post(semanticType, payload, replyTo) {
    var envelope = {
      protocol: PROTOCOL,
      version: VERSION,
      documentSessionId: sessionId,
      messageId: uuid(),
      semanticType: semanticType,
      payload: payload || {}
    };
    if (replyTo) {
      envelope.replyTo = replyTo;
    }
    chrome.webview.postMessage(envelope);
  }

  var projectSample = '<script>\nchrome.webview.postMessage({\n  semanticType: "externalLink.request"\n})\n</script>';
  setText("project-sample", projectSample);

  if (!window.chrome || !chrome.webview) {
    setText("status", "bridge unavailable");
    return;
  }

  chrome.webview.addEventListener("message", function (event) {
    var msg = event.data;
    if (!msg || msg.protocol !== PROTOCOL || msg.version !== VERSION) {
      return;
    }

    if (msg.semanticType === "host.hello") {
      sessionId = msg.documentSessionId;
      setText("status", "host hello received");
      post("renderer.ready", { shell: "wp15-static" });
      return;
    }

    if (msg.semanticType === "host.status") {
      setText("status", "bridge ready");
      return;
    }

    if (msg.semanticType === "bridge.pong") {
      setText("ping-status", "pong");
      return;
    }

    if (msg.semanticType === "bridge.error" && msg.payload) {
      setText("error", String(msg.payload.code || "error"));
    }
  });

  var ping = document.getElementById("ping");
  if (ping) {
    ping.addEventListener("click", function () {
      post("bridge.ping", {});
    });
  }

  var ext = document.getElementById("ext");
  if (ext) {
    ext.addEventListener("click", function () {
      post("externalLink.request", { uri: "https://example.com/path" });
    });
  }
})();
