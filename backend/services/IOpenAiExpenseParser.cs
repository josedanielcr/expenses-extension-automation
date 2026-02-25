public interface IOpenAiExpenseParser
{
    Task<List<ExpenseParseResult>> ParseBatchAsync(
        IReadOnlyList<EmailEntry> emails,
        IReadOnlyCollection<string> categories,
        CancellationToken ct = default);
}
