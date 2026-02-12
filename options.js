const DEFAULTS = {
  sourceLabel: "ToParse",
  processedLabel: "AI Processed",
  sheetUrl: "",
  sheetTab: "draft",
  categories: ["supermarket", "restaurants", "subscriptions"]
};

function $(id) { return document.getElementById(id); }

function categoriesToText(list) {
  return (list || []).join("\n");
}
function textToCategories(text) {
  return (text || "")
    .split("\n")
    .map(s => s.trim())
    .filter(Boolean);
}

let statusTimer = null;
function setStatus(msg) {
  const el = $("status");
  el.textContent = msg;
  clearTimeout(statusTimer);
  statusTimer = setTimeout(() => (el.textContent = ""), 2500);
}

async function loadSettings() {
  const stored = await chrome.storage.sync.get(Object.keys(DEFAULTS));
  const settings = { ...DEFAULTS, ...stored };

  $("sourceLabel").value = settings.sourceLabel;
  $("processedLabel").value = settings.processedLabel;
  $("sheetUrl").value = settings.sheetUrl;
  $("sheetTab").value = settings.sheetTab;
  $("categories").value = categoriesToText(settings.categories);
}

async function saveSettings() {
  const sourceLabel = $("sourceLabel").value.trim() || DEFAULTS.sourceLabel;
  const processedLabel = $("processedLabel").value.trim() || DEFAULTS.processedLabel;
  const sheetUrl = $("sheetUrl").value.trim();
  const sheetTab = $("sheetTab").value.trim() || DEFAULTS.sheetTab;
  const categories = textToCategories($("categories").value);

  await chrome.storage.sync.set({ sourceLabel, processedLabel, sheetUrl, sheetTab, categories });
  setStatus("Saved ✅");
}

async function resetSettings() {
  await chrome.storage.sync.set(DEFAULTS);
  await loadSettings();
  setStatus("Reset ✅");
}

document.addEventListener("DOMContentLoaded", async () => {
  await loadSettings();

  $("save").addEventListener("click", saveSettings);
  $("reset").addEventListener("click", resetSettings);
});