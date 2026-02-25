async function handleAuthSignOut() {
  await BackgroundCore.signOut();
  return { ok: true };
}

self.handleAuthSignOut = handleAuthSignOut;
