async function emitSyncProgress(stage, state, detail = "") {
  try {
    await chrome.runtime.sendMessage({
      type: "SYNC_PROGRESS",
      stage,
      state,
      detail,
      at: Date.now(),
    });
  } catch {
    // Popup may be closed; ignore.
  }
}

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
  await emitSyncProgress("extract", "running", `Leyendo correos de "${labelName}"`);
  const extraction = await BackgroundCore.extractEmailsFromLabel(labelName);
  await emitSyncProgress("extract", "done", `${extraction.total} correos encontrados`);

  await emitSyncProgress("ai", "running", "Analizando gastos con IA");
  const parsedResult = await BackgroundCore.pushEmailsToBackend(
    extraction.token,
    extraction.emails,
    categories,
  );
  await emitSyncProgress("ai", "done", "Análisis completado");

  await emitSyncProgress("sheet", "running", `Guardando filas en "${sheetTab}"`);
  const sheetWriteResult = await syncParsedEntriesToSheet(
    extraction,
    parsedResult,
    sheetUrl,
    sheetTab,
  );
  if (sheetWriteResult) {
    await emitSyncProgress("sheet", "done", "Filas guardadas en Google Sheets");
  } else {
    await emitSyncProgress("sheet", "done", "Sin filas para guardar");
  }

  if (sheetWriteResult) {
    await emitSyncProgress("labels", "running", `Moviendo correos a "${processedLabel}"`);
    const labelMoveResult = await moveEmailsToProcessedLabel(
      extraction,
      sourceLabel,
      processedLabel,
    );
    console.log("Gmail label move result:", labelMoveResult);
    await emitSyncProgress("labels", "done", `${labelMoveResult.moved || 0} correos movidos`);
  } else {
    await emitSyncProgress("labels", "done", "Movimiento omitido");
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
