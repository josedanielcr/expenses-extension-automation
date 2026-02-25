const ON_EMAIL_PUSH_URL =
  "https://email-processor-ai-angubybzh5feb8ce.canadacentral-01.azurewebsites.net/api/OnEmailPush";

const BackgroundCore = {
  async getAuthToken(interactive) {
    return await chrome.identity.getAuthToken({ interactive });
  },

  tokenFromAuthResult(authResult) {
    if (!authResult) return "";
    if (typeof authResult === "string") return authResult;
    return authResult.token || "";
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
      throw new Error(`gmail request failed: ${res.status} ${text}`);
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

  async listAllMessagesByLabel(token, labelId) {
    const messageRefs = [];
    let pageToken = null;

    do {
      const data = await BackgroundCore.gmailRequest(token, "users/me/messages", {
        labelIds: labelId,
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

  async extractEmailsFromLabel(labelName) {
    const cachedTokenObject = await BackgroundCore.getAuthToken(false).catch(() => null);
    const interactiveTokenObject = !cachedTokenObject
      ? await BackgroundCore.getAuthToken(true)
      : null;
    const token =
      BackgroundCore.tokenFromAuthResult(cachedTokenObject) ||
      BackgroundCore.tokenFromAuthResult(interactiveTokenObject);
    if (!token) {
      throw new Error("No auth token available. Please sign in again.");
    }

    const labelId = await BackgroundCore.findLabelIdByName(token, labelName);
    if (!labelId) {
      throw new Error(`Label "${labelName}" was not found in Gmail.`);
    }

    const messageRefs = await BackgroundCore.listAllMessagesByLabel(token, labelId);
    const emails = [];
    for (const msg of messageRefs) {
      emails.push(await BackgroundCore.fetchMessageMetadata(token, msg.id));
    }

    return { labelName, labelId, total: emails.length, emails, token };
  },

  async pushEmailsToBackend(token, emails, categories = []) {
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
      }),
    });

    const text = await res.text();
    let body = null;
    try {
      body = text ? JSON.parse(text) : null;
    } catch {
      body = text;
    }

    if (!res.ok) {
      throw new Error(
        `OnEmailPush failed: ${res.status} ${typeof body === "string" ? body : JSON.stringify(body)}`,
      );
    }

    return body;
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
      throw new Error("Invalid sheetUrl. Could not extract spreadsheet id.");
    }

    const range = encodeURIComponent(`${sheetTab}!A:D`);
    const url =
      `https://sheets.googleapis.com/v4/spreadsheets/${spreadsheetId}/values/${range}:append` +
      `?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS`;

    const rows = BackgroundCore.toSheetRows(parsedEntries);
    const res = await fetch(url, {
      method: "POST",
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
    let body = null;
    try {
      body = text ? JSON.parse(text) : null;
    } catch {
      body = text;
    }

    if (!res.ok) {
      throw new Error(
        `Sheet append failed: ${res.status} ${typeof body === "string" ? body : JSON.stringify(body)}`,
      );
    }

    return body;
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
      throw new Error(`userinfo failed: ${res.status} ${text}`);
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
