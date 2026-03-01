using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public sealed class OpenAiExpenseParser : IOpenAiExpenseParser
{
    private const string ChatCompletionsUrl = "https://api.openai.com/v1/chat/completions";
    private const string BearerScheme = "Bearer";
    private const string JsonMediaType = "application/json";
    private const string OpenAiSecretName = "openai-api-key";
    private const string DefaultModel = "gpt-5-nano";
    private const double DefaultTemperature = 1;
    private const string ConfigOpenAiApiKey = "OPENAI_API_KEY";
    private const string ConfigOpenAiApiKeyFromVault = "openai-api-key";
    private const string ConfigKeyVaultUri = "KEY_VAULT_URI";
    private const string ConfigOpenAiModel = "OPENAI_MODEL";
    private const string ConfigSystemPrompt = "OPENAI_SYSTEM_PROMPT";
    private const string ConfigUserPromptTemplate = "OPENAI_USER_PROMPT_TEMPLATE";
    private const string DefaultCategoriesText = "No categories provided.";
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
        CancellationToken ct = default)
    {
        if (emails.Count == 0)
            return [];

        var apiKey = await GetApiKeyAsync(ct);
        var model = GetConfiguredModel();
        var systemPrompt = GetConfiguredSystemPrompt();
        var userPrompt = BuildUserPrompt(emails, categories);
        _logger.LogInformation(
            "OpenAI batch parse started. Emails={EmailCount} Categories={CategoryCount} Model={Model} PromptChars={PromptChars}",
            emails.Count,
            categories.Count,
            model,
            userPrompt.Length);

        using var request = BuildChatCompletionsRequest(apiKey, model, systemPrompt, userPrompt);
        var responseBody = await SendRequestAndReadBodyAsync(request, ct);
        _logger.LogInformation("OpenAI raw response received. Chars={ResponseChars}", responseBody.Length);

        var parsed = ParseModelResult(responseBody);
        var normalized = NormalizeResults(parsed, emails);
        _logger.LogInformation(
            "OpenAI batch parse completed. Parsed={ParsedCount} Normalized={NormalizedCount}",
            parsed.Count,
            normalized.Count);
        return normalized;
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

    private string GetConfiguredSystemPrompt()
    {
        var prompt = _configuration[ConfigSystemPrompt];
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException($"{ConfigSystemPrompt} is required.");

        return prompt;
    }

    private string BuildUserPrompt(IReadOnlyList<EmailEntry> emails, IReadOnlyCollection<string> categories)
    {
        var template = _configuration[ConfigUserPromptTemplate];
        if (string.IsNullOrWhiteSpace(template))
            throw new InvalidOperationException($"{ConfigUserPromptTemplate} is required.");

        var categoryText = categories.Count > 0
            ? string.Join(", ", categories)
            : DefaultCategoriesText;
        var emailsJson = JsonSerializer.Serialize(emails);

        return template
            .Replace("{{categories}}", categoryText, StringComparison.Ordinal)
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
            temperature = DefaultTemperature,
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
}
