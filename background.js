async function getAuthToken(interactive) {
  return await chrome.identity.getAuthToken({ interactive });
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
  if (tokenObject) {
    const token = tokenObject.token;
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
        const info = await fetchUserInfo(tokenObject.token);

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

      sendResponse({ ok: false, error: "Unknown message type" });
    } catch (err) {
      sendResponse({ ok: false, error: String(err?.message || err) });
    }
  })();

  return true;
});
