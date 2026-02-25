async function handleAuthSignIn() {
  const tokenObject = await BackgroundCore.getAuthToken(true);
  const token = BackgroundCore.tokenFromAuthResult(tokenObject);
  if (!token) {
    throw new Error("Could not acquire auth token.");
  }

  const info = await BackgroundCore.fetchUserInfo(token);
  await chrome.storage.sync.set({
    userEmail: info.email || "",
    userName: info.name || info.given_name || "",
  });

  return {
    ok: true,
    user: { email: info.email, name: info.name },
  };
}

self.handleAuthSignIn = handleAuthSignIn;
