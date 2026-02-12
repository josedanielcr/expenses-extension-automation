document.addEventListener("DOMContentLoaded", async () => {
  const statusEl = document.getElementById("status");
  const openSettingsBtn = document.getElementById("openSettings");
  const syncBtn = document.getElementById("syncBtn");

  openSettingsBtn.addEventListener("click", () => {
    chrome.tabs.create({
      url: chrome.runtime.getURL("options.html")
    });
  });

  const { sheetUrl } = await chrome.storage.sync.get(["sheetUrl"]);

  if (sheetUrl) {
    syncBtn.disabled = false;
    statusEl.textContent = "Ready to sync";
  } else {
    syncBtn.disabled = true;
    statusEl.textContent = "Configure settings first";
  }

  syncBtn.addEventListener("click", () => {
    statusEl.textContent = "Sync started… (not implemented yet)";
  });
});