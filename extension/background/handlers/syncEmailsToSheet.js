async function syncParsedEntriesToSheet(extraction, parsedResult, sheetUrl, sheetTab) {
  if (!sheetUrl) return null;
  const parsedEntries = parsedResult?.entries || [];
  if (parsedEntries.length === 0) return null;

  return await BackgroundCore.appendParsedEntriesToSheet(
    extraction.token,
    sheetUrl,
    sheetTab,
    parsedEntries,
  );
}

self.syncParsedEntriesToSheet = syncParsedEntriesToSheet;
