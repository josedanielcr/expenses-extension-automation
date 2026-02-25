async function handleExtractEmailsFromLabel(msg) {
  const {
    sourceLabel = "ToParse",
    categories = [],
    sheetUrl = "",
    sheetTab = "draft",
    processedLabel = "AI Processed",
  } = await chrome.storage.sync.get([
    "sourceLabel",
    "categories",
    "sheetUrl",
    "sheetTab",
    "processedLabel",
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
  if (sheetWriteResult) {
    const labelMoveResult = await moveEmailsToProcessedLabel(
      extraction,
      sourceLabel,
      processedLabel,
    );
    console.log("Gmail label move result:", labelMoveResult);
  }

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
