const FIELD_IDS = {
  SOURCE_LABEL: "sourceLabel",
  PROCESSED_LABEL: "processedLabel",
  SHEET_URL: "sheetUrl",
  SHEET_TAB: "sheetTab",
  CATEGORIES: "categories",
  EXCLUSION_RULES_LIST: "exclusionRulesList",
  ADD_EXCLUSION_RULE: "addExclusionRule",
  STATUS: "status",
  SAVE: "save",
  RESET: "reset",
};

const TEXT = {
  SAVED: "Guardado",
  RESET: "Restablecido",
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
  categories: ["supermarket", "restaurants", "subscriptions"],
  exclusionRules: [],
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

let exclusionRulesState = [];

function normalizeExclusionRules(rules, validCategories) {
  const categorySet = new Set(validCategories);
  return (rules || [])
    .map((rule) => ({
      word: String(rule?.word || "").trim(),
      category: String(rule?.category || "").trim(),
    }))
    .filter((rule) => rule.word && categorySet.has(rule.category));
}

function getCurrentCategories() {
  return textToCategories($(FIELD_IDS.CATEGORIES).value);
}

function readExclusionRulesFromUI() {
  const rows = Array.from($(FIELD_IDS.EXCLUSION_RULES_LIST).querySelectorAll(".rule-row"));
  return rows.map((row) => {
    const wordInput = row.querySelector("input");
    const categorySelect = row.querySelector("select");
    return {
      word: wordInput?.value?.trim() || "",
      category: categorySelect?.value?.trim() || "",
    };
  });
}

function renderExclusionRules() {
  const categories = getCurrentCategories();
  const listEl = $(FIELD_IDS.EXCLUSION_RULES_LIST);
  listEl.innerHTML = "";

  if (categories.length === 0) {
    const empty = document.createElement("p");
    empty.className = "hint";
    empty.textContent = "Agrega categorías para poder crear reglas de exclusión.";
    listEl.appendChild(empty);
    return;
  }

  exclusionRulesState.forEach((rule, idx) => {
    const row = document.createElement("div");
    row.className = "rule-row";
    row.dataset.index = String(idx);

    const wordInput = document.createElement("input");
    wordInput.type = "text";
    wordInput.placeholder = "Palabra o frase";
    wordInput.setAttribute("aria-label", "Palabra o frase");
    wordInput.value = rule.word || "";

    const categorySelect = document.createElement("select");
    categorySelect.setAttribute("aria-label", "Categoría");
    categories.forEach((category) => {
      const option = document.createElement("option");
      option.value = category;
      option.textContent = category;
      categorySelect.appendChild(option);
    });
    categorySelect.value = categories.includes(rule.category) ? rule.category : categories[0];

    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "remove-rule";
    removeBtn.textContent = "Quitar";
    removeBtn.title = "Eliminar regla";
    removeBtn.setAttribute("aria-label", "Eliminar regla");

    row.appendChild(wordInput);
    row.appendChild(categorySelect);
    row.appendChild(removeBtn);
    listEl.appendChild(row);
  });
}

function addExclusionRule() {
  const categories = getCurrentCategories();
  if (categories.length === 0) {
    setStatus("Primero agrega al menos una categoría.");
    return;
  }

  exclusionRulesState = [
    ...readExclusionRulesFromUI(),
    { word: "", category: categories[0] },
  ];
  renderExclusionRules();
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
  const categories = settings.categories || [];

  $(FIELD_IDS.SOURCE_LABEL).value = settings.sourceLabel;
  $(FIELD_IDS.PROCESSED_LABEL).value = settings.processedLabel;
  $(FIELD_IDS.SHEET_URL).value = settings.sheetUrl;
  $(FIELD_IDS.SHEET_TAB).value = settings.sheetTab;
  $(FIELD_IDS.CATEGORIES).value = categoriesToText(categories);
  exclusionRulesState = normalizeExclusionRules(settings.exclusionRules, categories);
  renderExclusionRules();
}

async function saveSettings() {
  const sourceLabel = $(FIELD_IDS.SOURCE_LABEL).value.trim() || DEFAULTS.sourceLabel;
  const processedLabel = $(FIELD_IDS.PROCESSED_LABEL).value.trim() || DEFAULTS.processedLabel;
  const sheetUrl = $(FIELD_IDS.SHEET_URL).value.trim();
  const sheetTab = $(FIELD_IDS.SHEET_TAB).value.trim() || DEFAULTS.sheetTab;
  const categories = textToCategories($(FIELD_IDS.CATEGORIES).value);
  const exclusionRules = normalizeExclusionRules(readExclusionRulesFromUI(), categories);

  await chrome.storage.sync.set({
    sourceLabel,
    processedLabel,
    sheetUrl,
    sheetTab,
    categories,
    exclusionRules,
  });
  exclusionRulesState = exclusionRules;
  renderExclusionRules();
  setStatus(TEXT.SAVED);
}

async function resetSettings() {
  await chrome.storage.sync.set(DEFAULTS);
  await loadSettings();
  setStatus(TEXT.RESET);
}

document.addEventListener("DOMContentLoaded", async () => {
  await loadSettings();

  $(FIELD_IDS.CATEGORIES).addEventListener("input", () => {
    exclusionRulesState = normalizeExclusionRules(readExclusionRulesFromUI(), getCurrentCategories());
    renderExclusionRules();
  });
  $(FIELD_IDS.ADD_EXCLUSION_RULE).addEventListener("click", addExclusionRule);
  $(FIELD_IDS.EXCLUSION_RULES_LIST).addEventListener("click", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLElement) || !target.classList.contains("remove-rule")) return;

    const row = target.closest(".rule-row");
    const idx = Number(row?.dataset.index);
    if (Number.isNaN(idx)) return;

    exclusionRulesState = readExclusionRulesFromUI().filter((_, i) => i !== idx);
    renderExclusionRules();
  });

  $(FIELD_IDS.SAVE).addEventListener("click", saveSettings);
  $(FIELD_IDS.RESET).addEventListener("click", resetSettings);
});
