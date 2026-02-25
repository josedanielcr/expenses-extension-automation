async function handleExtractEmailsFromLabel(msg) {
  const {
    sourceLabel = "ToParse",
    categories = [],
    sheetUrl = "",
    sheetTab = "draft",
  } = await chrome.storage.sync.get([
    "sourceLabel",
    "categories",
    "sheetUrl",
    "sheetTab",
  ]);
  const labelName = msg.labelName || sourceLabel || "ToParse";
  const extraction = await BackgroundCore.extractEmailsFromLabel(labelName);
  const parsedResult = await BackgroundCore.pushEmailsToBackend(
    extraction.token,
    extraction.emails,
    categories,
  );
  const sheetWriteResult = await syncParsedEntriesToSheet(
    extraction,
    parsedResult,
    sheetUrl,
    sheetTab,
  );

  const rowsAppended = (parsedResult?.entries || []).length;

  return {
    ok: true,
    labelName: extraction.labelName,
    total: extraction.total,
    parsed: parsedResult,
    rowsAppended,
  };
}

self.handleExtractEmailsFromLabel = handleExtractEmailsFromLabel;
