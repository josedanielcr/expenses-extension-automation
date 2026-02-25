async function moveEmailsToProcessedLabel(extraction, sourceLabelName, processedLabelName) {
  const token = extraction?.token || "";
  const messageIds = extraction?.messageIds || [];
  if (!token || messageIds.length === 0) {
    return { moved: 0 };
  }

  const sourceLabelId =
    extraction?.labelId || (await BackgroundCore.findLabelIdByName(token, sourceLabelName));
  const processedLabelId = await BackgroundCore.getOrCreateLabelId(token, processedLabelName);

  if (!processedLabelId) {
    throw new Error(`Could not resolve processed label id for "${processedLabelName}".`);
  }

  await BackgroundCore.batchModifyMessageLabels(
    token,
    messageIds,
    [processedLabelId],
    sourceLabelId ? [sourceLabelId] : [],
  );

  return {
    moved: messageIds.length,
    sourceLabelId: sourceLabelId || "",
    processedLabelId,
  };
}

self.moveEmailsToProcessedLabel = moveEmailsToProcessedLabel;
