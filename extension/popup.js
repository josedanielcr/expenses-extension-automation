const STORAGE_KEYS = {
  SHEET_URL: "sheetUrl",
  USER_EMAIL: "userEmail",
  USER_NAME: "userName",
};

const MESSAGE_TYPES = {
  AUTH_SIGN_IN: "AUTH_SIGN_IN",
  AUTH_SIGN_OUT: "AUTH_SIGN_OUT",
  EXTRACT_EMAILS_FROM_LABEL: "EXTRACT_EMAILS_FROM_LABEL",
};

const ROUTES = {
  OPTIONS_PAGE: "options.html",
};

const TEXT = {
  UNKNOWN_ERROR: "unknown error",
  NOT_SIGNED_IN: "Not signed in",
  SIGNED_IN_PREFIX: "Signed in: ",
  SIGN_IN_TO_ENABLE_SYNC: "Sign in to enable sync",
  READY_TO_EXTRACT: "Ready to extract emails (sheet not configured yet)",
  READY_TO_SYNC: "Ready to sync",
  SIGNING_IN: "Signing in...",
  SIGNED_IN_SUCCESS: "Signed in",
  SIGNING_OUT: "Signing out...",
  SIGNED_OUT_SUCCESS: "Signed out",
  FETCHING_LABELED_EMAILS: "Fetching labeled emails...",
};

document.addEventListener("DOMContentLoaded", async () => {
  const statusEl = document.getElementById("status");
  const userLineEl = document.getElementById("userLine");

  const signInBtn = document.getElementById("signInBtn");
  const signOutBtn = document.getElementById("signOutBtn");
  const openSettingsBtn = document.getElementById("openSettings");
  const syncBtn = document.getElementById("syncBtn");

  function setStatus(msg) {
    statusEl.textContent = msg;
  }

  async function refreshUI() {
    const { sheetUrl, userEmail, userName } = await chrome.storage.sync.get([
      STORAGE_KEYS.SHEET_URL,
      STORAGE_KEYS.USER_EMAIL,
      STORAGE_KEYS.USER_NAME
    ]);

    const signedIn = !!userEmail;

    if (signedIn) {
      userLineEl.textContent = `${TEXT.SIGNED_IN_PREFIX}${userName ? userName + " — " : ""}${userEmail}`;
      signInBtn.disabled = true;
      signOutBtn.disabled = false;
    } else {
      userLineEl.textContent = TEXT.NOT_SIGNED_IN;
      signInBtn.disabled = false;
      signOutBtn.disabled = true;
    }

    syncBtn.disabled = !signedIn;
    if (!signedIn) setStatus(TEXT.SIGN_IN_TO_ENABLE_SYNC);
    else if (!sheetUrl) setStatus(TEXT.READY_TO_EXTRACT);
    else setStatus(TEXT.READY_TO_SYNC);
  }

  signInBtn.addEventListener("click", async () => {
    setStatus(TEXT.SIGNING_IN);
    const res = await chrome.runtime.sendMessage({ type: MESSAGE_TYPES.AUTH_SIGN_IN });
    if (!res?.ok) {
      setStatus(`Sign-in failed: ${res?.error || TEXT.UNKNOWN_ERROR}`);
    } else {
      setStatus(TEXT.SIGNED_IN_SUCCESS);
    }
    await refreshUI();
  });

  signOutBtn.addEventListener("click", async () => {
    setStatus(TEXT.SIGNING_OUT);
    const res = await chrome.runtime.sendMessage({ type: MESSAGE_TYPES.AUTH_SIGN_OUT });
    if (!res?.ok) {
      setStatus(`Sign-out failed: ${res?.error || TEXT.UNKNOWN_ERROR}`);
    } else {
      setStatus(TEXT.SIGNED_OUT_SUCCESS);
    }
    await refreshUI();
  });

  openSettingsBtn.addEventListener("click", () => {
    chrome.tabs.create({ url: chrome.runtime.getURL(ROUTES.OPTIONS_PAGE) });
  });

  syncBtn.addEventListener("click", async () => {
    try {
      setStatus(TEXT.FETCHING_LABELED_EMAILS);
      const res = await chrome.runtime.sendMessage({
        type: MESSAGE_TYPES.EXTRACT_EMAILS_FROM_LABEL
      });

      if (!res?.ok) {
        setStatus(`Sync failed: ${res?.error || TEXT.UNKNOWN_ERROR}`);
        return;
      }

      setStatus(`Fetched ${res.total} emails from "${res.labelName}"`);
    } catch (err) {
      setStatus(`Sync failed: ${String(err?.message || err)}`);
    }
  });

  await refreshUI();
});
