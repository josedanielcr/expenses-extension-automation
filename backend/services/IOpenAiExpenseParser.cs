public interface IOpenAiExpenseParser
{
    Task<ExpenseParseResult> ParseAsync(
        EmailEntry email,
        IReadOnlyCollection<string> categories,
        CancellationToken ct = default);
}
