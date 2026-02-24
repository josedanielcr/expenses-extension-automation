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
    private const string JsonObjectType = "json_object";
    private const string OpenAiSecretName = "openai-api-key";
    private const string DefaultModel = "gpt-4o-mini";
    private const double DefaultTemperature = 0;
    private const string ConfigOpenAiApiKey = "OPENAI_API_KEY";
    private const string ConfigOpenAiApiKeyFromVault = "openai-api-key";
    private const string ConfigKeyVaultUri = "KEY_VAULT_URI";
    private const string ConfigOpenAiModel = "OPENAI_MODEL";
    private const string ConfigSystemPrompt = "OPENAI_SYSTEM_PROMPT";
    private const string ConfigUserPromptTemplate = "OPENAI_USER_PROMPT_TEMPLATE";
    private const string DefaultCategoriesText = "No categories provided.";
    private const string DefaultSystemPrompt =
        "You extract expense data from emails. Return valid JSON only with keys: date, amount, category, description.";
    private const string DefaultUserPromptTemplate =
        "Categories: {{categories}}\n" +
        "Sender: {{sender}}\n" +
        "Date: {{date}}\n" +
        "Message:\n" +
        "{{message}}\n\n" +
        "Rules:\n" +
        "- Use provided date when possible.\n" +
        "- amount should include currency symbol if present.\n" +
        "- category should be one of the provided categories when possible.\n" +
        "- description should be short and concrete.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
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

    public async Task<ExpenseParseResult> ParseAsync(
        EmailEntry email,
        IReadOnlyCollection<string> categories,
        CancellationToken ct = default)
    {
        var apiKey = await GetApiKeyAsync(ct);
        var model = GetConfiguredModel();
        var systemPrompt = GetConfiguredSystemPrompt();
        var userPrompt = BuildUserPrompt(email, categories);

        using var request = BuildChatCompletionsRequest(apiKey, model, systemPrompt, userPrompt);
        var responseBody = await SendRequestAndReadBodyAsync(request, ct);
        var result = ParseModelResult(responseBody);

        if (string.IsNullOrWhiteSpace(result.Date))
            result.Date = email.Date;

        return result;
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
        => _configuration[ConfigSystemPrompt] ?? DefaultSystemPrompt;

    private string BuildUserPrompt(EmailEntry email, IReadOnlyCollection<string> categories)
    {
        var template = _configuration[ConfigUserPromptTemplate] ?? DefaultUserPromptTemplate;
        var categoryText = categories.Count > 0
            ? string.Join(", ", categories)
            : DefaultCategoriesText;

        return template
            .Replace("{{categories}}", categoryText, StringComparison.Ordinal)
            .Replace("{{sender}}", email.Sender ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{date}}", email.Date ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{message}}", email.Message ?? string.Empty, StringComparison.Ordinal);
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
            response_format = new { type = JsonObjectType },
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

    private static ExpenseParseResult ParseModelResult(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("OpenAI returned an empty response.");

        return JsonSerializer.Deserialize<ExpenseParseResult>(content, JsonOptions)
            ?? throw new InvalidOperationException("OpenAI returned invalid JSON object.");
    }
}
