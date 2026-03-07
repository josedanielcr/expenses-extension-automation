using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

public sealed class OpenAiExpenseParser : IOpenAiExpenseParser
{
    private const string ChatCompletionsUrl = "https://api.openai.com/v1/chat/completions";
    private const string BearerScheme = "Bearer";
    private const string JsonMediaType = "application/json";
    private const string OpenAiSecretName = "openai-api-key";
    private const string DefaultModel = "gpt-5-nano";
    private const string ConfigOpenAiApiKey = "OPENAI_API_KEY";
    private const string ConfigOpenAiApiKeyFromVault = "openai-api-key";
    private const string ConfigKeyVaultUri = "KEY_VAULT_URI";
    private const string ConfigOpenAiModel = "OPENAI_MODEL";
    private const string ConfigSystemPrompt = "OPENAI_SYSTEM_PROMPT";
    private const string ConfigUserPromptTemplate = "OPENAI_USER_PROMPT_TEMPLATE";
    private const string ConfigBatchSize = "OPENAI_EMAIL_BATCH_SIZE";
    private const string ConfigBatchConcurrency = "OPENAI_BATCH_CONCURRENCY";
    private const string ConfigMaxEmailMessageChars = "OPENAI_MAX_EMAIL_MESSAGE_CHARS";
    private const int DefaultBatchSize = 5;
    private const int DefaultBatchConcurrency = 2;
    private const int DefaultMaxEmailMessageChars = 4000;
    private const string NonTransactionCategory = "N/A";
    private const string NonTransactionAmount = "0";
    private const string NonTransactionDescriptionFallback = "Email no transaccional";
    private const int NonTransactionSummaryMaxChars = 80;
    private const string DefaultCategoriesText = "No categories provided.";
    private const string ExclusionRulesPlaceholder = "{{exclusion_rules_json}}";
    private const int LogPreviewLength = 1200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new FlexibleStringJsonConverter() },
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiExpenseParser> _logger;
    private readonly SemaphoreSlim _secretLock = new(1, 1);
    private string? _apiKey;

    public OpenAiExpenseParser(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<OpenAiExpenseParser> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<ExpenseParseResult>> ParseBatchAsync(
        IReadOnlyList<EmailEntry> emails,
        IReadOnlyCollection<string> categories,
        IReadOnlyCollection<CategoryExclusionRule> exclusionRules,
        CancellationToken ct = default)
    {
        if (emails.Count == 0)
            return [];

        var apiKey = await GetApiKeyAsync(ct);
        var model = GetConfiguredModel();
        var systemPrompt = GetConfiguredSystemPrompt();
        var batchSize = GetConfiguredBatchSize();
        var batchConcurrency = GetConfiguredBatchConcurrency();
        var totalBatches = (emails.Count + batchSize - 1) / batchSize;

        _logger.LogInformation(
            "OpenAI parse started. Emails={EmailCount} Categories={CategoryCount} Model={Model} BatchSize={BatchSize} BatchConcurrency={BatchConcurrency} TotalBatches={TotalBatches}",
            emails.Count,
            categories.Count,
            model,
            batchSize,
            batchConcurrency,
            totalBatches);

        var chunkTasks = new List<Task<ChunkResult>>(totalBatches);
        using var gate = new SemaphoreSlim(batchConcurrency, batchConcurrency);
        for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
        {
            var start = batchIndex * batchSize;
            var count = Math.Min(batchSize, emails.Count - start);
            var chunk = new List<EmailEntry>(count);
            for (var i = 0; i < count; i++)
                chunk.Add(emails[start + i]);

            var chunkNumber = batchIndex + 1;
            chunkTasks.Add(ProcessChunkAsync(
                gate,
                apiKey,
                model,
                systemPrompt,
                chunk,
                categories,
                exclusionRules,
                chunkNumber,
                totalBatches,
                ct));
        }

        var chunkResults = await Task.WhenAll(chunkTasks);
        Array.Sort(chunkResults, static (left, right) => left.ChunkNumber.CompareTo(right.ChunkNumber));

        var allResults = new List<ExpenseParseResult>(emails.Count);
        foreach (var chunkResult in chunkResults)
            allResults.AddRange(chunkResult.Results);

        _logger.LogInformation(
            "OpenAI parse completed. Emails={EmailCount} Aggregated={AggregatedCount}",
            emails.Count,
            allResults.Count);
        return allResults;
    }

    private async Task<string> GetApiKeyAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
            return _apiKey;

        await _secretLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(_apiKey))
                return _apiKey;

            var directValue = _configuration[ConfigOpenAiApiKey] ?? _configuration[ConfigOpenAiApiKeyFromVault];
            if (!string.IsNullOrWhiteSpace(directValue))
            {
                _apiKey = directValue;
                return _apiKey;
            }

            var vaultUri = _configuration[ConfigKeyVaultUri];
            if (string.IsNullOrWhiteSpace(vaultUri))
                throw new InvalidOperationException($"{ConfigKeyVaultUri} is required to load {OpenAiSecretName}.");

            var client = new SecretClient(new Uri(vaultUri), new DefaultAzureCredential());
            var secret = await client.GetSecretAsync(OpenAiSecretName, cancellationToken: ct);
            _apiKey = secret.Value.Value;
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException($"Secret {OpenAiSecretName} is empty.");

            _logger.LogInformation("Loaded OpenAI API key from Key Vault.");
            return _apiKey;
        }
        finally
        {
            _secretLock.Release();
        }
    }

    private string GetConfiguredModel()
        => _configuration[ConfigOpenAiModel] ?? DefaultModel;

    private int GetConfiguredBatchSize()
    {
        var configuredValue = _configuration[ConfigBatchSize];
        if (!int.TryParse(configuredValue, out var batchSize) || batchSize <= 0)
            return DefaultBatchSize;

        return batchSize;
    }

    private int GetConfiguredBatchConcurrency()
    {
        var configuredValue = _configuration[ConfigBatchConcurrency];
        if (!int.TryParse(configuredValue, out var concurrency) || concurrency <= 0)
            return DefaultBatchConcurrency;

        return concurrency;
    }

    private int GetConfiguredMaxEmailMessageChars()
    {
        var configuredValue = _configuration[ConfigMaxEmailMessageChars];
        if (!int.TryParse(configuredValue, out var maxChars) || maxChars <= 0)
            return DefaultMaxEmailMessageChars;

        return maxChars;
    }

    private string GetConfiguredSystemPrompt()
    {
        var prompt = _configuration[ConfigSystemPrompt];
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException($"{ConfigSystemPrompt} is required.");

        return prompt;
    }

    private string BuildUserPrompt(
        IReadOnlyList<EmailEntry> emails,
        IReadOnlyCollection<string> categories,
        IReadOnlyCollection<CategoryExclusionRule> exclusionRules)
    {
        var template = _configuration[ConfigUserPromptTemplate];
        if (string.IsNullOrWhiteSpace(template))
            throw new InvalidOperationException($"{ConfigUserPromptTemplate} is required.");
        if (!template.Contains(ExclusionRulesPlaceholder, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{ConfigUserPromptTemplate} must include placeholder {ExclusionRulesPlaceholder}.");

        var categoryText = categories.Count > 0
            ? string.Join(", ", categories)
            : DefaultCategoriesText;
        var emailsJson = JsonSerializer.Serialize(BuildPromptEmailPayload(emails));
        var exclusionRulesJson = JsonSerializer.Serialize(exclusionRules ?? []);

        return template
            .Replace("{{categories}}", categoryText, StringComparison.Ordinal)
            .Replace(ExclusionRulesPlaceholder, exclusionRulesJson, StringComparison.Ordinal)
            .Replace("{{emails_json}}", emailsJson, StringComparison.Ordinal);
    }

    private HttpRequestMessage BuildChatCompletionsRequest(
        string apiKey,
        string model,
        string systemPrompt,
        string userPrompt)
    {
        var requestBody = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            JsonMediaType);

        return request;
    }

    private async Task<ChunkResult> ProcessChunkAsync(
        SemaphoreSlim gate,
        string apiKey,
        string model,
        string systemPrompt,
        IReadOnlyList<EmailEntry> chunk,
        IReadOnlyCollection<string> categories,
        IReadOnlyCollection<CategoryExclusionRule> exclusionRules,
        int chunkNumber,
        int totalBatches,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var userPrompt = BuildUserPrompt(chunk, categories, exclusionRules);
            _logger.LogInformation(
                "OpenAI batch parse chunk started. Chunk={ChunkIndex}/{TotalBatches} Emails={ChunkEmailCount} PromptChars={PromptChars}",
                chunkNumber,
                totalBatches,
                chunk.Count,
                userPrompt.Length);

            using var request = BuildChatCompletionsRequest(apiKey, model, systemPrompt, userPrompt);
            var responseBody = await SendRequestAndReadBodyAsync(request, ct);
            _logger.LogInformation(
                "OpenAI batch parse chunk response received. Chunk={ChunkIndex}/{TotalBatches} ResponseChars={ResponseChars}",
                chunkNumber,
                totalBatches,
                responseBody.Length);

            var parsed = ParseModelResult(responseBody);
            var normalized = NormalizeResults(parsed, chunk);
            _logger.LogInformation(
                "OpenAI batch parse chunk completed. Chunk={ChunkIndex}/{TotalBatches} Parsed={ParsedCount} Normalized={NormalizedCount}",
                chunkNumber,
                totalBatches,
                parsed.Count,
                normalized.Count);

            return new ChunkResult(chunkNumber, normalized);
        }
        finally
        {
            gate.Release();
        }
    }

    private List<PromptEmailEntry> BuildPromptEmailPayload(IReadOnlyList<EmailEntry> emails)
    {
        var maxMessageChars = GetConfiguredMaxEmailMessageChars();
        var output = new List<PromptEmailEntry>(emails.Count);
        for (var i = 0; i < emails.Count; i++)
        {
            var email = emails[i];
            output.Add(new PromptEmailEntry
            {
                Date = email.Date?.Trim() ?? string.Empty,
                Sender = email.Sender?.Trim() ?? string.Empty,
                Message = Truncate(email.Message?.Trim() ?? string.Empty, maxMessageChars),
            });
        }

        return output;
    }

    private async Task<string> SendRequestAndReadBodyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI request failed. Status={(int)response.StatusCode} Body={body}");

        return body;
    }

    private static List<ExpenseParseResult> ParseModelResult(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var message = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");
        var content = ExtractAssistantContent(message);

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("OpenAI returned an empty response.");

        var cleaned = UnwrapJsonStringPayload(StripJsonCodeFences(content));
        using var contentDoc = JsonDocument.Parse(cleaned);
        var root = contentDoc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<ExpenseParseResult>>(root.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException("OpenAI returned an invalid JSON array.");
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (TryGetArrayProperty(root, "entries", out var entriesArray) ||
                TryGetArrayProperty(root, "results", out entriesArray) ||
                TryGetArrayProperty(root, "data", out entriesArray))
            {
                return JsonSerializer.Deserialize<List<ExpenseParseResult>>(entriesArray.GetRawText(), JsonOptions)
                    ?? throw new InvalidOperationException("OpenAI returned invalid entries array.");
            }

            if (LooksLikeExpenseObject(root))
            {
                var single = JsonSerializer.Deserialize<ExpenseParseResult>(root.GetRawText(), JsonOptions)
                    ?? throw new InvalidOperationException("OpenAI returned invalid expense object.");
                return [single];
            }
        }

        throw new InvalidOperationException("OpenAI returned invalid JSON array.");
    }

    private static string UnwrapJsonStringPayload(string content)
    {
        var current = content.Trim();

        for (var i = 0; i < 3; i++)
        {
            using var doc = JsonDocument.Parse(current);
            if (doc.RootElement.ValueKind != JsonValueKind.String)
                return current;

            var inner = doc.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(inner))
                return string.Empty;

            current = inner.Trim();
        }

        return current;
    }

    private static List<ExpenseParseResult> NormalizeResults(
        IReadOnlyList<ExpenseParseResult> modelResults,
        IReadOnlyList<EmailEntry> sourceEmails)
    {
        var normalized = new List<ExpenseParseResult>(sourceEmails.Count);

        for (var i = 0; i < sourceEmails.Count; i++)
        {
            var source = sourceEmails[i];
            var fromModel = i < modelResults.Count ? modelResults[i] : null;

            var result = fromModel ?? new ExpenseParseResult();
            if (string.IsNullOrWhiteSpace(result.Date))
                result.Date = source.Date ?? string.Empty;

            result.Amount ??= string.Empty;
            result.Category ??= string.Empty;
            result.Description ??= string.Empty;
            result.Amount = result.Amount.Trim();
            result.Category = result.Category.Trim();
            result.Description = result.Description.Trim();

            if (IsNonTransactionalResult(result))
            {
                result.Amount = NonTransactionAmount;
                result.Category = NonTransactionCategory;
                result.Description = BuildNonTransactionSummary(source.Message);
            }

            normalized.Add(result);
        }

        return normalized;
    }

    private static string ExtractAssistantContent(JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("content", out var contentElement))
            return string.Empty;

        if (contentElement.ValueKind == JsonValueKind.String)
            return contentElement.GetString() ?? string.Empty;

        if (contentElement.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in contentElement.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    var text = textElement.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                }
            }

            return string.Join("\n", parts);
        }

        return contentElement.GetRawText();
    }

    private static string StripJsonCodeFences(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineBreak = trimmed.IndexOf('\n');
        if (firstLineBreak < 0)
            return trimmed.Trim('`').Trim();

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstLineBreak)
            return trimmed[(firstLineBreak + 1)..].Trim();

        return trimmed[(firstLineBreak + 1)..lastFence].Trim();
    }

    private static bool TryGetArrayProperty(JsonElement root, string propertyName, out JsonElement arrayElement)
    {
        if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            arrayElement = value;
            return true;
        }

        arrayElement = default;
        return false;
    }

    private static bool LooksLikeExpenseObject(JsonElement root)
    {
        return root.TryGetProperty("date", out _) ||
               root.TryGetProperty("amount", out _) ||
               root.TryGetProperty("category", out _) ||
               root.TryGetProperty("description", out _);
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value;

        return value[..maxChars];
    }

    private static bool IsNonTransactionalResult(ExpenseParseResult result)
    {
        return string.IsNullOrWhiteSpace(result.Amount) &&
               string.IsNullOrWhiteSpace(result.Category) &&
               string.IsNullOrWhiteSpace(result.Description);
    }

    private static string BuildNonTransactionSummary(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return NonTransactionDescriptionFallback;

        var collapsed = string.Join(
            " ",
            message
                .Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(collapsed))
            return NonTransactionDescriptionFallback;

        var firstSentenceEnd = collapsed.IndexOfAny(new[] { '.', ';', ':' });
        var summary = firstSentenceEnd > 0
            ? collapsed[..firstSentenceEnd]
            : collapsed;
        summary = summary.Trim(' ', '-', '_', ',', '.');
        if (string.IsNullOrWhiteSpace(summary))
            return NonTransactionDescriptionFallback;

        return Truncate(summary, NonTransactionSummaryMaxChars);
    }

    private sealed record ChunkResult(int ChunkNumber, List<ExpenseParseResult> Results);

    private sealed class PromptEmailEntry
    {
        [JsonPropertyName("date")]
        public string Date { get; init; } = string.Empty;

        [JsonPropertyName("sender")]
        public string Sender { get; init; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;
    }
}
