const DEFAULT_FUNCTION_HOST =
  "https://email-processor-ai-angubybzh5feb8ce.canadacentral-01.azurewebsites.net";
const ON_EMAIL_PUSH_PATH = "/api/OnEmailPush";

function resolveOnEmailPushUrl() {
  const manifest = chrome.runtime?.getManifest?.();
  const hostPermissions = manifest?.host_permissions || [];
  const azureHostPermission = hostPermissions.find((permission) =>
    permission.includes(".azurewebsites.net"),
  );

  if (!azureHostPermission) {
    return `${DEFAULT_FUNCTION_HOST}${ON_EMAIL_PUSH_PATH}`;
  }

  try {
    const origin = new URL(azureHostPermission.replace("*", "")).origin;
    return `${origin}${ON_EMAIL_PUSH_PATH}`;
  } catch {
    return `${DEFAULT_FUNCTION_HOST}${ON_EMAIL_PUSH_PATH}`;
  }
}

const ON_EMAIL_PUSH_URL = resolveOnEmailPushUrl();

const BackgroundCore = {
  async getAuthToken(interactive) {
    return await chrome.identity.getAuthToken({ interactive });
  },

  tokenFromAuthResult(authResult) {
    if (!authResult) return "";
    if (typeof authResult === "string") return authResult;
    return authResult.token || "";
  },

  parsePossibleJson(value) {
    if (typeof value !== "string") return value;
    const trimmed = value.trim();
    if (!trimmed) return "";
    const looksLikeJson =
      (trimmed.startsWith("{") && trimmed.endsWith("}")) ||
      (trimmed.startsWith("[") && trimmed.endsWith("]")) ||
      (trimmed.startsWith("\"") && trimmed.endsWith("\""));
    if (!looksLikeJson) return value;
    try {
      return JSON.parse(trimmed);
    } catch {
      return value;
    }
  },

  parseResponseBody(text) {
    const firstPass = BackgroundCore.parsePossibleJson(text || "");
    if (typeof firstPass === "string") {
      return BackgroundCore.parsePossibleJson(firstPass);
    }
    return firstPass;
  },

  extractApiErrorMessage(body) {
    if (!body) return "";
    if (typeof body === "string") return body.trim();

    const direct =
      body.message ||
      body.error_description ||
      body.errorMessage ||
      body.title ||
      body.detail ||
      "";
    if (typeof direct === "string" && direct.trim()) return direct.trim();

    if (typeof body.error === "string" && body.error.trim()) {
      return body.error.trim();
    }

    if (body.error && typeof body.error === "object") {
      const nested =
        body.error.message ||
        body.error.error_description ||
        body.error.detail ||
        body.error.title ||
        "";
      if (typeof nested === "string" && nested.trim()) return nested.trim();
    }

    if (Array.isArray(body.errors) && body.errors.length > 0) {
      const first = body.errors[0];
      if (typeof first === "string") return first.trim();
      if (typeof first?.message === "string" && first.message.trim()) return first.message.trim();
    }

    return "";
  },

  translateKnownErrorDetail(message) {
    const raw = String(message || "").trim();
    if (!raw) return "";
    if (/[áéíóúñ¿¡]/i.test(raw)) return raw;

    if (/Failed to fetch/i.test(raw) || /NetworkError/i.test(raw)) {
      return "No se pudo establecer conexión. Revisa tu conexión a internet e inténtalo de nuevo.";
    }
    if (/token/i.test(raw) && /auth|acquire|session|signin|sign in/i.test(raw)) {
      return "No hay sesión activa. Inicia sesión nuevamente.";
    }
    if (/Invalid date range format/i.test(raw)) {
      return "Formato de fechas inválido. Usa el formato YYYY-MM-DD.";
    }
    if (/Start date must be before end date/i.test(raw)) {
      return "La fecha inicial debe ser anterior o igual a la fecha final.";
    }
    if (/Label "(.+)" was not found in Gmail\./i.test(raw)) {
      return raw.replace(
        /Label "(.+)" was not found in Gmail\./i,
        'No se encontró la etiqueta "$1" en Gmail.',
      );
    }
    if (/Could not resolve processed label id/i.test(raw)) {
      return "No se pudo resolver la etiqueta de destino para correos procesados.";
    }
    if (/Invalid sheetUrl/i.test(raw)) {
      return "La URL de Google Sheets no es válida.";
    }
    if (/Unknown message type/i.test(raw)) {
      return "Solicitud no reconocida por la extensión.";
    }

    return "";
  },

  buildHttpError(userMessage, status, body) {
    const apiMessage = BackgroundCore.extractApiErrorMessage(body);
    const translatedDetail = BackgroundCore.translateKnownErrorDetail(apiMessage);
    const isServerError = Number(status) >= 500;
    const finalMessage = translatedDetail
      ? `${userMessage} ${translatedDetail}`
      : isServerError
        ? "Ocurrió un error inesperado al procesar tu solicitud. Inténtalo de nuevo más tarde."
        : `${userMessage} Inténtalo de nuevo.`;
    const err = new Error(finalMessage);
    err.userMessage = finalMessage;
    err.httpStatus = status;
    err.rawApiMessage = apiMessage;
    return err;
  },

  toUserErrorMessage(error, fallback = "Ocurrió un error inesperado.") {
    if (error && typeof error === "object" && typeof error.userMessage === "string") {
      return error.userMessage;
    }

    const raw = String(error?.message || error || "").trim();
    const translated = BackgroundCore.translateKnownErrorDetail(raw);
    if (translated) return translated;
    return fallback;
  },

  async gmailRequest(token, path, params = {}) {
    const url = new URL(`https://gmail.googleapis.com/gmail/v1/${path}`);
    Object.entries(params).forEach(([key, value]) => {
      if (value !== undefined && value !== null && value !== "") {
        url.searchParams.set(key, String(value));
      }
    });

    const res = await fetch(url.toString(), {
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/json",
      },
    });

    if (!res.ok) {
      const text = await res.text();
      const body = BackgroundCore.parseResponseBody(text);
      throw BackgroundCore.buildHttpError("No fue posible consultar Gmail.", res.status, body);
    }

    return await res.json();
  },

  readHeader(payload, name) {
    const headers = payload?.headers || [];
    const found = headers.find((h) => h?.name?.toLowerCase() === name.toLowerCase());
    return found?.value || "";
  },

  decodeBase64Url(data) {
    if (!data) return "";
    const normalized = data.replace(/-/g, "+").replace(/_/g, "/");
    const padded = normalized + "=".repeat((4 - (normalized.length % 4)) % 4);
    try {
      return atob(padded);
    } catch {
      return "";
    }
  },

  collectBodyParts(payload, mimeType) {
    const results = [];
    const walk = (part) => {
      if (!part) return;
      const currentMimeType = (part.mimeType || "").toLowerCase();
      if (currentMimeType === mimeType && part.body?.data) {
        results.push(BackgroundCore.decodeBase64Url(part.body.data));
      }
      for (const child of part.parts || []) {
        walk(child);
      }
    };
    walk(payload);
    return results.join("\n").trim();
  },

  htmlToText(html) {
    if (!html) return "";
    return html
      .replace(/<style[\s\S]*?<\/style>/gi, " ")
      .replace(/<script[\s\S]*?<\/script>/gi, " ")
      .replace(/<[^>]+>/g, " ")
      .replace(/&nbsp;/gi, " ")
      .replace(/&amp;/gi, "&")
      .replace(/&lt;/gi, "<")
      .replace(/&gt;/gi, ">")
      .replace(/\s+/g, " ")
      .trim();
  },

  extractMessageText(payload) {
    if (!payload) return "";

    const plainText = BackgroundCore.collectBodyParts(payload, "text/plain");
    if (plainText) return plainText;

    const htmlText = BackgroundCore.collectBodyParts(payload, "text/html");
    if (htmlText) return BackgroundCore.htmlToText(htmlText);

    if (payload.body?.data) return BackgroundCore.decodeBase64Url(payload.body.data).trim();
    return "";
  },

  async findLabelIdByName(token, labelName) {
    const data = await BackgroundCore.gmailRequest(token, "users/me/labels");
    const labels = data.labels || [];
    const exact = labels.find((l) => l.name === labelName);
    if (exact) return exact.id;

    const caseInsensitive = labels.find(
      (l) => l.name?.toLowerCase() === labelName.toLowerCase(),
    );
    return caseInsensitive?.id || null;
  },

  buildDateRangeQuery(dateRange) {
    if (!dateRange) return "";

    const startDate = String(dateRange.startDate || "").trim();
    const endDate = String(dateRange.endDate || "").trim();
    const isDate = (value) => /^\d{4}-\d{2}-\d{2}$/.test(value);
    if (!isDate(startDate) || !isDate(endDate)) {
      throw new Error("Formato de fechas inválido. Usa el formato YYYY-MM-DD.");
    }
    if (startDate > endDate) {
      throw new Error("La fecha inicial debe ser anterior o igual a la fecha final.");
    }

    const startLocal = new Date(`${startDate}T00:00:00`);
    const endExclusiveLocal = new Date(`${endDate}T00:00:00`);
    endExclusiveLocal.setDate(endExclusiveLocal.getDate() + 1);

    // Gmail `after:` is strict. Subtract 1 second so start-day midnight is included.
    const afterEpoch = Math.floor(startLocal.getTime() / 1000) - 1;
    const beforeEpoch = Math.floor(endExclusiveLocal.getTime() / 1000);

    return `after:${afterEpoch} before:${beforeEpoch}`;
  },

  async listAllMessagesByLabel(token, labelId, dateRange = null) {
    const messageRefs = [];
    let pageToken = null;
    const query = BackgroundCore.buildDateRangeQuery(dateRange);

    do {
      const data = await BackgroundCore.gmailRequest(token, "users/me/messages", {
        labelIds: labelId,
        q: query,
        maxResults: 100,
        pageToken,
      });

      messageRefs.push(...(data.messages || []));
      pageToken = data.nextPageToken || null;
    } while (pageToken);

    return messageRefs;
  },

  async fetchMessageMetadata(token, messageId) {
    const data = await BackgroundCore.gmailRequest(token, `users/me/messages/${messageId}`, {
      format: "full",
    });

    return {
      sender: BackgroundCore.readHeader(data.payload, "From"),
      date: BackgroundCore.readHeader(data.payload, "Date"),
      message: BackgroundCore.extractMessageText(data.payload),
    };
  },

  async extractEmailsFromLabel(labelName, dateRange = null) {
    const cachedTokenObject = await BackgroundCore.getAuthToken(false).catch(() => null);
    const interactiveTokenObject = !cachedTokenObject
      ? await BackgroundCore.getAuthToken(true)
      : null;
    const token =
      BackgroundCore.tokenFromAuthResult(cachedTokenObject) ||
      BackgroundCore.tokenFromAuthResult(interactiveTokenObject);
    if (!token) {
      throw new Error("No hay sesión activa. Inicia sesión nuevamente.");
    }

    const labelId = await BackgroundCore.findLabelIdByName(token, labelName);
    if (!labelId) {
      throw new Error(`No se encontró la etiqueta "${labelName}" en Gmail.`);
    }

    const messageRefs = await BackgroundCore.listAllMessagesByLabel(token, labelId, dateRange);
    const emails = [];
    for (const msg of messageRefs) {
      emails.push(await BackgroundCore.fetchMessageMetadata(token, msg.id));
    }
    const messageIds = messageRefs.map((m) => m.id).filter(Boolean);

    return { labelName, labelId, total: emails.length, emails, messageIds, token };
  },

  async createLabel(token, labelName) {
    const res = await fetch("https://gmail.googleapis.com/gmail/v1/users/me/labels", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/json",
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        name: labelName,
        labelListVisibility: "labelShow",
        messageListVisibility: "show",
      }),
    });

    const text = await res.text();
    if (!res.ok) {
      const body = BackgroundCore.parseResponseBody(text);
      throw BackgroundCore.buildHttpError(
        "No fue posible crear la etiqueta en Gmail.",
        res.status,
        body,
      );
    }

    const body = BackgroundCore.parseResponseBody(text);
    return body && typeof body === "object" ? body : null;
  },

  async getOrCreateLabelId(token, labelName) {
    const existing = await BackgroundCore.findLabelIdByName(token, labelName);
    if (existing) return existing;

    const created = await BackgroundCore.createLabel(token, labelName);
    return created?.id || "";
  },

  async batchModifyMessageLabels(token, messageIds, addLabelIds = [], removeLabelIds = []) {
    if (!messageIds?.length) return null;

    const res = await fetch("https://gmail.googleapis.com/gmail/v1/users/me/messages/batchModify", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/json",
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        ids: messageIds,
        addLabelIds,
        removeLabelIds,
      }),
    });

    const text = await res.text();
    if (!res.ok) {
      const body = BackgroundCore.parseResponseBody(text);
      throw BackgroundCore.buildHttpError(
        "No fue posible mover etiquetas en Gmail.",
        res.status,
        body,
      );
    }

    const body = BackgroundCore.parseResponseBody(text);
    return body && typeof body === "object" ? body : {};
  },

  async pushEmailsToBackend(token, emails, categories = [], exclusionRules = []) {
    const res = await fetch(ON_EMAIL_PUSH_URL, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/json",
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        emails,
        categories,
        exclusionRules,
      }),
    });

    const text = await res.text();
    const body = BackgroundCore.parseResponseBody(text);

    if (!res.ok) {
      throw BackgroundCore.buildHttpError(
        "No se pudo procesar la información con el servidor.",
        res.status,
        body,
      );
    }

    return body || {};
  },

  extractSpreadsheetId(sheetUrl) {
    if (!sheetUrl) return "";
    const match = sheetUrl.match(/\/spreadsheets\/d\/([a-zA-Z0-9-_]+)/);
    return match?.[1] || "";
  },

  toSheetRows(parsedEntries) {
    return (parsedEntries || []).map((entry) => [
      BackgroundCore.formatDateOnly(entry?.date || ""),
      entry?.description || "",
      entry?.amount || "",
      entry?.category || "",
    ]);
  },

  formatDateOnly(rawDate) {
    const value = String(rawDate || "").trim();
    if (!value) return "";

    const ddmmyyyy = value.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})$/);
    if (ddmmyyyy) {
      const day = ddmmyyyy[1].padStart(2, "0");
      const month = ddmmyyyy[2].padStart(2, "0");
      const year = ddmmyyyy[3];
      return `${day}/${month}/${year}`;
    }

    const yyyymmdd = value.match(/^(\d{4})-(\d{2})-(\d{2})/);
    if (yyyymmdd) {
      return `${yyyymmdd[3]}/${yyyymmdd[2]}/${yyyymmdd[1]}`;
    }

    const parsed = new Date(value);
    if (!Number.isNaN(parsed.getTime())) {
      const day = String(parsed.getDate()).padStart(2, "0");
      const month = String(parsed.getMonth() + 1).padStart(2, "0");
      const year = String(parsed.getFullYear());
      return `${day}/${month}/${year}`;
    }

    return value;
  },

  async appendParsedEntriesToSheet(token, sheetUrl, sheetTab, parsedEntries) {
    const spreadsheetId = BackgroundCore.extractSpreadsheetId(sheetUrl);
    if (!spreadsheetId) {
      throw new Error("La URL de Google Sheets no es válida.");
    }

    const rows = BackgroundCore.toSheetRows(parsedEntries);
    if (rows.length === 0) return null;

    const startRow = await BackgroundCore.findFirstEmptyRowInColumnA(
      token,
      spreadsheetId,
      sheetTab,
    );
    const writeRange = encodeURIComponent(`${sheetTab}!A${startRow}:D`);
    const writeUrl =
      `https://sheets.googleapis.com/v4/spreadsheets/${spreadsheetId}/values/${writeRange}` +
      `?valueInputOption=USER_ENTERED`;

    const res = await fetch(writeUrl, {
      method: "PUT",
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/json",
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        majorDimension: "ROWS",
        values: rows,
      }),
    });

    const text = await res.text();
    const body = BackgroundCore.parseResponseBody(text);

    if (!res.ok) {
      throw BackgroundCore.buildHttpError(
        "No se pudo guardar la información en Google Sheets.",
        res.status,
        body,
      );
    }

    return body;
  },

  async findFirstEmptyRowInColumnA(token, spreadsheetId, sheetTab) {
    const firstDataRow = 2;
    const readRange = encodeURIComponent(`${sheetTab}!A${firstDataRow}:A`);
    const readUrl = `https://sheets.googleapis.com/v4/spreadsheets/${spreadsheetId}/values/${readRange}`;

    const res = await fetch(readUrl, {
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/json",
      },
    });

    const text = await res.text();
    const body = BackgroundCore.parseResponseBody(text);

    if (!res.ok) {
      throw BackgroundCore.buildHttpError(
        "No se pudo leer Google Sheets.",
        res.status,
        body,
      );
    }

    const values = Array.isArray(body?.values) ? body.values : [];
    for (let i = 0; i < values.length; i += 1) {
      const cell = Array.isArray(values[i]) ? String(values[i][0] || "").trim() : "";
      if (!cell) return firstDataRow + i;
    }

    return firstDataRow + values.length;
  },

  async fetchUserInfo(token) {
    const res = await fetch("https://www.googleapis.com/oauth2/v3/userinfo", {
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/json",
      },
    });
    if (!res.ok) {
      const text = await res.text();
      const body = BackgroundCore.parseResponseBody(text);
      throw BackgroundCore.buildHttpError(
        "No se pudo obtener la información de tu cuenta.",
        res.status,
        body,
      );
    }
    return await res.json();
  },

  async signOut() {
    const tokenObject = await chrome.identity
      .getAuthToken({ interactive: false })
      .catch(() => null);
    const token = BackgroundCore.tokenFromAuthResult(tokenObject);
    if (token) {
      await new Promise((resolve) =>
        chrome.identity.removeCachedAuthToken({ token }, resolve),
      );
    }

    await chrome.storage.sync.remove(["userEmail", "userName"]);
  },
};

self.BackgroundCore = BackgroundCore;
