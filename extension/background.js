importScripts(
  "background/lib/core.js",
  "background/handlers/authSignIn.js",
  "background/handlers/authSignOut.js",
  "background/handlers/extractEmailsFromLabel.js",
  "background/handlers/syncEmailsToSheet.js",
  "background/handlers/moveEmailsToProcessedLabel.js",
);

const MESSAGE_TYPES = {
  AUTH_SIGN_IN: "AUTH_SIGN_IN",
  AUTH_SIGN_OUT: "AUTH_SIGN_OUT",
  EXTRACT_EMAILS_FROM_LABEL: "EXTRACT_EMAILS_FROM_LABEL",
};

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  (async () => {
    try {
      if (msg.type === MESSAGE_TYPES.AUTH_SIGN_IN) {
        sendResponse(await handleAuthSignIn());
        return;
      }

      if (msg.type === MESSAGE_TYPES.AUTH_SIGN_OUT) {
        sendResponse(await handleAuthSignOut());
        return;
      }

      if (msg.type === MESSAGE_TYPES.EXTRACT_EMAILS_FROM_LABEL) {
        sendResponse(await handleExtractEmailsFromLabel(msg));
        return;
      }

      sendResponse({ ok: false, error: "Unknown message type" });
    } catch (err) {
      sendResponse({ ok: false, error: String(err?.message || err) });
    }
  })();

  return true;
});
