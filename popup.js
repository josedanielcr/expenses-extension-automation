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
      "sheetUrl",
      "userEmail",
      "userName"
    ]);

    const signedIn = !!userEmail;

    if (signedIn) {
      userLineEl.textContent = `Signed in: ${userName ? userName + " — " : ""}${userEmail}`;
      signInBtn.disabled = true;
      signOutBtn.disabled = false;
    } else {
      userLineEl.textContent = "Not signed in";
      signInBtn.disabled = false;
      signOutBtn.disabled = true;
    }

    // enable Sync only if signed in + configured sheet
    syncBtn.disabled = !(signedIn && sheetUrl);
    if (!signedIn) setStatus("Sign in to enable sync");
    else if (!sheetUrl) setStatus("Configure settings first");
    else setStatus("Ready to sync");
  }

  signInBtn.addEventListener("click", async () => {
    setStatus("Signing in...");
    const res = await chrome.runtime.sendMessage({ type: "AUTH_SIGN_IN" });
    if (!res?.ok) {
      setStatus(`Sign-in failed: ${res?.error || "unknown error"}`);
    } else {
      setStatus("Signed in ✅");
    }
    await refreshUI();
  });

  signOutBtn.addEventListener("click", async () => {
    setStatus("Signing out...");
    const res = await chrome.runtime.sendMessage({ type: "AUTH_SIGN_OUT" });
    if (!res?.ok) {
      setStatus(`Sign-out failed: ${res?.error || "unknown error"}`);
    } else {
      setStatus("Signed out ✅");
    }
    await refreshUI();
  });

  openSettingsBtn.addEventListener("click", () => {
    chrome.tabs.create({ url: chrome.runtime.getURL("options.html") });
  });

  syncBtn.addEventListener("click", () => {
    setStatus("Sync started… (next milestone)");
  });

  await refreshUI();
});