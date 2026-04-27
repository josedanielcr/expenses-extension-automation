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
    exclusionRules = [],
    sheetUrl = "",
    sheetTab = "draft",
    processedLabel = "AI Processed",
  } = await chrome.storage.sync.get([
    "sourceLabel",
    "categories",
    "exclusionRules",
    "sheetUrl",
    "sheetTab",
    "processedLabel",
  ]);

  const dateRange = msg?.dateRange || null;
  const labelName = msg.labelName || sourceLabel || "ToParse";
  const rangeText = dateRange?.startDate && dateRange?.endDate
    ? ` entre ${dateRange.startDate} y ${dateRange.endDate}`
    : "";
  await emitSyncProgress("extract", "running", `Leyendo correos de "${labelName}"${rangeText}`);
  const extraction = await BackgroundCore.extractEmailsFromLabel(labelName, dateRange);
  await emitSyncProgress("extract", "done", `${extraction.total} correos encontrados`);

  if (extraction.total === 0) {
    await emitSyncProgress("ai", "done", "Sin correos para analizar");
    await emitSyncProgress("sheet", "done", "Sin filas para guardar");
    await emitSyncProgress("labels", "done", "Movimiento omitido");

    return {
      ok: true,
      labelName: extraction.labelName,
      total: extraction.total,
      parsed: { entries: [] },
      rowsAppended: 0,
      noEmails: true,
    };
  }

  await emitSyncProgress("ai", "running", "Analizando gastos con IA");
  const parsedResult = await BackgroundCore.pushEmailsToBackend(
    extraction.token,
    extraction.emails,
    categories,
    exclusionRules,
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

  const rowsAppended = Array.isArray(parsedResult?.entries) ? parsedResult.entries.length : 0;

  return {
    ok: true,
    labelName: extraction.labelName,
    total: extraction.total,
    parsed: parsedResult,
    rowsAppended,
  };
}

self.handleExtractEmailsFromLabel = handleExtractEmailsFromLabel;
