async function getAuthToken(interactive) {
  return await chrome.identity.getAuthToken({ interactive });
}

function tokenFromAuthResult(authResult) {
  if (!authResult) return "";
  if (typeof authResult === "string") return authResult;
  return authResult.token || "";
}

async function gmailRequest(token, path, params = {}) {
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
}

function readHeader(payload, name) {
  const headers = payload?.headers || [];
  const found = headers.find((h) => h?.name?.toLowerCase() === name.toLowerCase());
  return found?.value || "";
}

function decodeBase64Url(data) {
  if (!data) return "";
  const normalized = data.replace(/-/g, "+").replace(/_/g, "/");
  const padded = normalized + "=".repeat((4 - (normalized.length % 4)) % 4);
  try {
    return atob(padded);
  } catch {
    return "";
  }
}

function collectBodyParts(payload, mimeType) {
  const results = [];
  const walk = (part) => {
    if (!part) return;
    const currentMimeType = (part.mimeType || "").toLowerCase();
    if (currentMimeType === mimeType && part.body?.data) {
      results.push(decodeBase64Url(part.body.data));
    }
    for (const child of part.parts || []) {
      walk(child);
    }
  };
  walk(payload);
  return results.join("\n").trim();
}

function htmlToText(html) {
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
}

function extractMessageText(payload) {
  if (!payload) return "";

  const plainText = collectBodyParts(payload, "text/plain");
  if (plainText) return plainText;

  const htmlText = collectBodyParts(payload, "text/html");
  if (htmlText) return htmlToText(htmlText);

  if (payload.body?.data) return decodeBase64Url(payload.body.data).trim();
  return "";
}

async function findLabelIdByName(token, labelName) {
  const data = await gmailRequest(token, "users/me/labels");
  const labels = data.labels || [];
  const exact = labels.find((l) => l.name === labelName);
  if (exact) return exact.id;

  const caseInsensitive = labels.find(
    (l) => l.name?.toLowerCase() === labelName.toLowerCase(),
  );
  return caseInsensitive?.id || null;
}

async function listAllMessagesByLabel(token, labelId) {
  const messageRefs = [];
  let pageToken = null;

  do {
    const data = await gmailRequest(token, "users/me/messages", {
      labelIds: labelId,
      maxResults: 100,
      pageToken,
    });

    messageRefs.push(...(data.messages || []));
    pageToken = data.nextPageToken || null;
  } while (pageToken);

  return messageRefs;
}

async function fetchMessageMetadata(token, messageId) {
  const data = await gmailRequest(token, `users/me/messages/${messageId}`, {
    format: "full",
  });

  return {
    sender: readHeader(data.payload, "From"),
    date: readHeader(data.payload, "Date"),
    message: extractMessageText(data.payload),
  };
}

async function extractEmailsFromLabel(labelName) {
  const cachedTokenObject = await getAuthToken(false).catch(() => null);
  const interactiveTokenObject = !cachedTokenObject
    ? await getAuthToken(true)
    : null;
  const token =
    tokenFromAuthResult(cachedTokenObject) ||
    tokenFromAuthResult(interactiveTokenObject);
  if (!token) {
    throw new Error("No auth token available. Please sign in again.");
  }

  const labelId = await findLabelIdByName(token, labelName);
  if (!labelId) {
    throw new Error(`Label "${labelName}" was not found in Gmail.`);
  }

  const messageRefs = await listAllMessagesByLabel(token, labelId);
  const emails = [];
  for (const msg of messageRefs) {
    emails.push(await fetchMessageMetadata(token, msg.id));
  }

  return { labelName, labelId, total: emails.length, emails };
}

async function fetchUserInfo(token) {
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
}

async function signOut() {
  const tokenObject = await chrome.identity
    .getAuthToken({ interactive: false })
    .catch(() => null);
  const token = tokenFromAuthResult(tokenObject);
  if (token) {
    await new Promise((resolve) =>
      chrome.identity.removeCachedAuthToken({ token }, resolve),
    );
  }

  await chrome.storage.sync.remove(["userEmail", "userName"]);
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  (async () => {
    try {
      if (msg.type === "AUTH_SIGN_IN") {
        const tokenObject = await getAuthToken(true);
        const token = tokenFromAuthResult(tokenObject);
        if (!token) {
          throw new Error("Could not acquire auth token.");
        }
        const info = await fetchUserInfo(token);

        await chrome.storage.sync.set({
          userEmail: info.email || "",
          userName: info.name || info.given_name || "",
        });

        sendResponse({
          ok: true,
          user: { email: info.email, name: info.name },
        });
        return;
      }

      if (msg.type === "AUTH_SIGN_OUT") {
        await signOut();
        sendResponse({ ok: true });
        return;
      }

      if (msg.type === "EXTRACT_EMAILS_FROM_LABEL") {
        const { sourceLabel = "ToParse" } = await chrome.storage.sync.get([
          "sourceLabel",
        ]);
        const labelName = msg.labelName || sourceLabel || "ToParse";
        const extraction = await extractEmailsFromLabel(labelName);
        sendResponse({
          ok: true,
          labelName: extraction.labelName,
          total: extraction.total,
        });
        return;
      }

      sendResponse({ ok: false, error: "Unknown message type" });
    } catch (err) {
      sendResponse({ ok: false, error: String(err?.message || err) });
    }
  })();

  return true;
});
