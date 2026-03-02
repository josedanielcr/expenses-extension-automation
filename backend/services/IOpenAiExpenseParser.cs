public interface IOpenAiExpenseParser
{
    Task<List<ExpenseParseResult>> ParseBatchAsync(
        IReadOnlyList<EmailEntry> emails,
        IReadOnlyCollection<string> categories,
        IReadOnlyCollection<CategoryExclusionRule> exclusionRules,
        CancellationToken ct = default);
}
