const FIELD_IDS = {
  SOURCE_LABEL: "sourceLabel",
  PROCESSED_LABEL: "processedLabel",
  SHEET_URL: "sheetUrl",
  SHEET_TAB: "sheetTab",
  CATEGORIES: "categories",
  STATUS: "status",
  SAVE: "save",
  RESET: "reset",
};

const TEXT = {
  SAVED: "Saved",
  RESET: "Reset",
};

const UI = {
  LINE_BREAK: "\n",
  STATUS_CLEAR_DELAY_MS: 2500,
};

const DEFAULTS = {
  sourceLabel: "ToParse",
  processedLabel: "AI Processed",
  sheetUrl: "",
  sheetTab: "draft",
  categories: ["supermarket", "restaurants", "subscriptions"]
};

function $(id) { return document.getElementById(id); }

function categoriesToText(list) {
  return (list || []).join(UI.LINE_BREAK);
}
function textToCategories(text) {
  return (text || "")
    .split(UI.LINE_BREAK)
    .map(s => s.trim())
    .filter(Boolean);
}

let statusTimer = null;
function setStatus(msg) {
  const el = $(FIELD_IDS.STATUS);
  el.textContent = msg;
  clearTimeout(statusTimer);
  statusTimer = setTimeout(() => (el.textContent = ""), UI.STATUS_CLEAR_DELAY_MS);
}

async function loadSettings() {
  const stored = await chrome.storage.sync.get(Object.keys(DEFAULTS));
  const settings = { ...DEFAULTS, ...stored };

  $(FIELD_IDS.SOURCE_LABEL).value = settings.sourceLabel;
  $(FIELD_IDS.PROCESSED_LABEL).value = settings.processedLabel;
  $(FIELD_IDS.SHEET_URL).value = settings.sheetUrl;
  $(FIELD_IDS.SHEET_TAB).value = settings.sheetTab;
  $(FIELD_IDS.CATEGORIES).value = categoriesToText(settings.categories);
}

async function saveSettings() {
  const sourceLabel = $(FIELD_IDS.SOURCE_LABEL).value.trim() || DEFAULTS.sourceLabel;
  const processedLabel = $(FIELD_IDS.PROCESSED_LABEL).value.trim() || DEFAULTS.processedLabel;
  const sheetUrl = $(FIELD_IDS.SHEET_URL).value.trim();
  const sheetTab = $(FIELD_IDS.SHEET_TAB).value.trim() || DEFAULTS.sheetTab;
  const categories = textToCategories($(FIELD_IDS.CATEGORIES).value);

  await chrome.storage.sync.set({ sourceLabel, processedLabel, sheetUrl, sheetTab, categories });
  setStatus(TEXT.SAVED);
}

async function resetSettings() {
  await chrome.storage.sync.set(DEFAULTS);
  await loadSettings();
  setStatus(TEXT.RESET);
}

document.addEventListener("DOMContentLoaded", async () => {
  await loadSettings();

  $(FIELD_IDS.SAVE).addEventListener("click", saveSettings);
  $(FIELD_IDS.RESET).addEventListener("click", resetSettings);
});
