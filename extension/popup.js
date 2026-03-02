const STORAGE_KEYS = {
  SHEET_URL: "sheetUrl",
  USER_EMAIL: "userEmail",
  USER_NAME: "userName",
  USE_DATE_RANGE: "useDateRange",
  START_DATE: "startDate",
  END_DATE: "endDate",
};

const MESSAGE_TYPES = {
  AUTH_SIGN_IN: "AUTH_SIGN_IN",
  AUTH_SIGN_OUT: "AUTH_SIGN_OUT",
  EXTRACT_EMAILS_FROM_LABEL: "EXTRACT_EMAILS_FROM_LABEL",
  SYNC_PROGRESS: "SYNC_PROGRESS",
};

const TEXT = {
  UNKNOWN_ERROR: "error desconocido",
  NOT_SIGNED_IN: "Sin sesión iniciada",
  SIGNED_IN_PREFIX: "Sesión iniciada: ",
  SIGN_IN_TO_ENABLE_SYNC: "Inicia sesión para habilitar la sincronización",
  READY_TO_EXTRACT: "Listo para extraer correos (la hoja aún no está configurada)",
  READY_TO_SYNC: "Listo para sincronizar",
  SIGNING_IN: "Iniciando sesión...",
  SIGNED_IN_SUCCESS: "Sesión iniciada",
  SIGNING_OUT: "Cerrando sesión...",
  SIGNED_OUT_SUCCESS: "Sesión cerrada",
  FETCHING_LABELED_EMAILS: "Sincronizando correos y gastos...",
  SYNC_SUCCESS_PREFIX: "Sincronizado",
};

document.addEventListener("DOMContentLoaded", async () => {
  const statusEl = document.getElementById("status");
  const userLineEl = document.getElementById("userLine");

  const signInBtn = document.getElementById("signInBtn");
  const signOutBtn = document.getElementById("signOutBtn");
  const openSettingsBtn = document.getElementById("openSettings");
  const syncBtn = document.getElementById("syncBtn");
  const syncIndicator = document.getElementById("syncIndicator");
  const processLogEl = document.getElementById("processLog");
  const useDateRangeEl = document.getElementById("useDateRange");
  const dateRangeFieldsEl = document.getElementById("dateRangeFields");
  const startDateEl = document.getElementById("startDate");
  const endDateEl = document.getElementById("endDate");

  const stepOrder = ["extract", "ai", "sheet", "labels"];
  const stepLabels = {
    extract: "Leyendo correos",
    ai: "Analizando gastos con IA",
    sheet: "Guardando en Google Sheets",
    labels: "Moviendo etiquetas",
  };
  let stepState = {};

  function setStatus(msg) {
    statusEl.textContent = msg;
  }

  function setSyncIndicator(state) {
    syncIndicator.className = "sync-indicator";
    if (state === "loading") syncIndicator.classList.add("loading");
  }

  function resetStepState() {
    stepState = {
      extract: { state: "pending", detail: "" },
      ai: { state: "pending", detail: "" },
      sheet: { state: "pending", detail: "" },
      labels: { state: "pending", detail: "" },
    };
  }

  function renderProcessLog() {
    const escapeHtml = (value) =>
      String(value || "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;");

    const rows = stepOrder
      .filter((key) => stepState[key]?.state !== "pending")
      .map((key) => {
        const info = stepState[key];
        const stateClass = info.state === "running" ? "running" : "done";
        const text = escapeHtml(info.detail || stepLabels[key]);
        return `<div class="step ${stateClass}"><span class="step-dot"></span><span class="step-text">${text}</span></div>`;
      })
      .join("");

    processLogEl.innerHTML = rows;
  }

  function updateStep(stage, state, detail) {
    if (!stepState[stage]) return;
    stepState[stage] = { state, detail: detail || "" };
    renderProcessLog();
  }

  function clearProcessLogSoon() {
    window.setTimeout(() => {
      processLogEl.innerHTML = "";
      resetStepState();
    }, 1600);
  }

  function toggleDateRangeUI() {
    const enabled = !!useDateRangeEl.checked;
    dateRangeFieldsEl.classList.toggle("hidden", !enabled);
  }

  async function refreshUI() {
    const { sheetUrl, userEmail, userName, useDateRange, startDate, endDate } = await chrome.storage.sync.get([
      STORAGE_KEYS.SHEET_URL,
      STORAGE_KEYS.USER_EMAIL,
      STORAGE_KEYS.USER_NAME,
      STORAGE_KEYS.USE_DATE_RANGE,
      STORAGE_KEYS.START_DATE,
      STORAGE_KEYS.END_DATE,
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

    useDateRangeEl.checked = !!useDateRange;
    startDateEl.value = startDate || "";
    endDateEl.value = endDate || "";
    toggleDateRangeUI();
  }

  signInBtn.addEventListener("click", async () => {
    setStatus(TEXT.SIGNING_IN);
    const res = await chrome.runtime.sendMessage({ type: MESSAGE_TYPES.AUTH_SIGN_IN });
    if (!res?.ok) {
      setStatus(`Error al iniciar sesión: ${res?.error || TEXT.UNKNOWN_ERROR}`);
    } else {
      setStatus(TEXT.SIGNED_IN_SUCCESS);
    }
    await refreshUI();
  });

  signOutBtn.addEventListener("click", async () => {
    setStatus(TEXT.SIGNING_OUT);
    const res = await chrome.runtime.sendMessage({ type: MESSAGE_TYPES.AUTH_SIGN_OUT });
    if (!res?.ok) {
      setStatus(`Error al cerrar sesión: ${res?.error || TEXT.UNKNOWN_ERROR}`);
    } else {
      setStatus(TEXT.SIGNED_OUT_SUCCESS);
    }
    await refreshUI();
  });

  openSettingsBtn.addEventListener("click", () => {
    chrome.runtime.openOptionsPage();
  });

  useDateRangeEl.addEventListener("change", async () => {
    toggleDateRangeUI();
    await chrome.storage.sync.set({
      [STORAGE_KEYS.USE_DATE_RANGE]: useDateRangeEl.checked,
    });
  });

  startDateEl.addEventListener("change", async () => {
    await chrome.storage.sync.set({
      [STORAGE_KEYS.START_DATE]: startDateEl.value || "",
    });
  });

  endDateEl.addEventListener("change", async () => {
    await chrome.storage.sync.set({
      [STORAGE_KEYS.END_DATE]: endDateEl.value || "",
    });
  });

  syncBtn.addEventListener("click", async () => {
    try {
      const useDateRange = !!useDateRangeEl.checked;
      const startDate = startDateEl.value || "";
      const endDate = endDateEl.value || "";
      if (useDateRange) {
        if (!startDate || !endDate) {
          setStatus("Selecciona fecha inicial y final.");
          return;
        }
        if (startDate > endDate) {
          setStatus("La fecha inicial no puede ser mayor a la final.");
          return;
        }
      }

      resetStepState();
      renderProcessLog();
      setSyncIndicator("loading");
      setStatus(TEXT.FETCHING_LABELED_EMAILS);
      const res = await chrome.runtime.sendMessage({
        type: MESSAGE_TYPES.EXTRACT_EMAILS_FROM_LABEL,
        dateRange: useDateRange ? { startDate, endDate } : null,
      });

      if (!res?.ok) {
        setSyncIndicator("idle");
        setStatus(`Error al sincronizar: ${res?.error || TEXT.UNKNOWN_ERROR}`);
        clearProcessLogSoon();
        return;
      }

      setSyncIndicator("idle");
      updateStep("labels", "done", "Proceso finalizado");
      setStatus(`${TEXT.SYNC_SUCCESS_PREFIX} ${res.rowsAppended || 0} filas desde "${res.labelName}"`);
      clearProcessLogSoon();
    } catch (err) {
      setSyncIndicator("idle");
      setStatus(`Error al sincronizar: ${String(err?.message || err)}`);
      clearProcessLogSoon();
    }
  });

  chrome.runtime.onMessage.addListener((msg) => {
    if (msg?.type !== MESSAGE_TYPES.SYNC_PROGRESS) return;
    updateStep(msg.stage, msg.state, msg.detail);
  });

  resetStepState();
  await refreshUI();
});
