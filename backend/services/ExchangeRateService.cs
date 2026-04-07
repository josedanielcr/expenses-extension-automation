using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public sealed class ExchangeRateService
{
    private const string ConfigKeyVaultUri = "KEY_VAULT_URI";
    private const string ConfigExchangeRateApiKey = "EXCHANGE_RATE_API_KEY";
    private const string ExchangeRateApiSecretName = "Exchange-rate-API";
    private const string ConfigExchangeRateApiUrlTemplate = "EXCHANGE_RATE_API_URL_TEMPLATE";
    private const string ConfigTableConnectionString = "EXCHANGE_RATE_TABLE_CONNECTION_STRING";
    private const string ConfigTableServiceUri = "EXCHANGE_RATE_TABLE_SERVICE_URI";
    private const string ConfigTableName = "EXCHANGE_RATE_TABLE_NAME";
    private const string DefaultExchangeRateApiUrlTemplate = "https://v6.exchangerate-api.com/v6/{api-key}/pair/USD/CRC";
    private const string DefaultTableServiceUri = "https://compute911d.table.core.windows.net/";
    private const string DefaultTableName = "conversionRate";
    private const string DefaultPartitionKey = "exchangeRate";
    private const string DefaultRowKey = "USD_CRC";
    private const string CurrencyUsd = "USD";
    private const string CurrencyCrc = "CRC";
    private const string CurrencyCol = "COL";
    private const string ApiResultSuccess = "success";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExchangeRateService> _logger;
    private readonly SemaphoreSlim _secretLock = new(1, 1);
    private string? _apiKey;

    public ExchangeRateService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ExchangeRateService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<decimal> GetUsdToCrcRateAsync(CancellationToken ct = default)
    {
        var tableClient = CreateTableClient();
        var entity = await FindUsdToCrcEntityAsync(tableClient, ct);
        if (entity is null)
            throw new ApplicationException("USD->CRC conversion rate was not found in Azure Table Storage.");

        if (!TryGetDecimalRate(entity, out var rate))
            throw new ApplicationException("USD->CRC conversion rate is invalid in Azure Table Storage.");

        return rate;
    }

    public async Task<ExchangeRateSnapshot> RefreshUsdToCrcRateAsync(CancellationToken ct = default)
    {
        var apiKey = await GetApiKeyAsync(ct);
        var requestUrl = BuildExchangeRateRequestUrl(apiKey);

        using var response = await _httpClient.GetAsync(requestUrl, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ApplicationException($"Exchange rate API request failed. Status={(int)response.StatusCode} Body={body}");

        var apiResult = JsonSerializer.Deserialize<ExchangeRateApiResponse>(body, JsonOptions)
            ?? throw new ApplicationException("Exchange rate API returned an empty response.");
        if (!string.Equals(apiResult.Result, ApiResultSuccess, StringComparison.OrdinalIgnoreCase))
            throw new ApplicationException($"Exchange rate API did not return success. Result={apiResult.Result}");
        if (apiResult.ConversionRate <= 0)
            throw new ApplicationException("Exchange rate API returned an invalid conversion rate.");

        var tableClient = CreateTableClient();
        await tableClient.CreateIfNotExistsAsync(ct);

        var entity = await FindUsdToCrcEntityAsync(tableClient, ct)
            ?? new TableEntity(DefaultPartitionKey, DefaultRowKey);
        entity["From"] = CurrencyUsd;
        entity["To"] = CurrencyCrc;
        entity["Rate"] = (double)apiResult.ConversionRate;
        entity["UpdatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);

        _logger.LogInformation(
            "USD->CRC conversion rate refreshed. Rate={Rate} SourceLastUpdate={SourceLastUpdateUtc}",
            apiResult.ConversionRate,
            apiResult.TimeLastUpdateUtc ?? string.Empty);

        return new ExchangeRateSnapshot(apiResult.ConversionRate, DateTimeOffset.UtcNow);
    }

    private async Task<TableEntity?> FindUsdToCrcEntityAsync(TableClient tableClient, CancellationToken ct)
    {
        TableEntity? crcEntity = null;
        TableEntity? colEntity = null;
        TableEntity? usdFallbackEntity = null;

        await foreach (var entity in tableClient.QueryAsync<TableEntity>(cancellationToken: ct))
        {
            var from = TryGetString(entity, "From");
            if (!string.Equals(from, CurrencyUsd, StringComparison.OrdinalIgnoreCase))
                continue;

            var to = TryGetString(entity, "To");
            if (string.Equals(to, CurrencyCrc, StringComparison.OrdinalIgnoreCase))
                crcEntity = entity;
            else if (string.Equals(to, CurrencyCol, StringComparison.OrdinalIgnoreCase))
                colEntity = entity;

            usdFallbackEntity ??= entity;
        }

        return crcEntity ?? colEntity ?? usdFallbackEntity;
    }

    private static bool TryGetDecimalRate(TableEntity entity, out decimal value)
    {
        value = 0;
        if (!entity.TryGetValue("Rate", out var rawRate) || rawRate is null)
            return false;

        switch (rawRate)
        {
            case decimal decimalValue:
                value = decimalValue;
                return true;
            case double doubleValue:
                value = (decimal)doubleValue;
                return true;
            case float floatValue:
                value = (decimal)floatValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case long longValue:
                value = longValue;
                return true;
            case string stringValue:
                return decimal.TryParse(
                    stringValue.Trim(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out value);
            default:
                return false;
        }
    }

    private static string TryGetString(TableEntity entity, string key)
    {
        if (!entity.TryGetValue(key, out var rawValue) || rawValue is null)
            return string.Empty;

        return rawValue.ToString()?.Trim() ?? string.Empty;
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

            var directValue = _configuration[ConfigExchangeRateApiKey] ?? _configuration[ExchangeRateApiSecretName];
            if (!string.IsNullOrWhiteSpace(directValue))
            {
                _apiKey = directValue;
                return _apiKey;
            }

            var vaultUri = _configuration[ConfigKeyVaultUri];
            if (string.IsNullOrWhiteSpace(vaultUri))
            {
                throw new ApplicationException(
                    $"{ConfigKeyVaultUri} is required to load {ExchangeRateApiSecretName}.");
            }

            var client = new SecretClient(new Uri(vaultUri), new DefaultAzureCredential());
            var secret = await client.GetSecretAsync(ExchangeRateApiSecretName, cancellationToken: ct);
            _apiKey = secret.Value.Value;
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new ApplicationException($"Secret {ExchangeRateApiSecretName} is empty.");

            _logger.LogInformation("Loaded exchange rate API key from Key Vault.");
            return _apiKey;
        }
        finally
        {
            _secretLock.Release();
        }
    }

    private string BuildExchangeRateRequestUrl(string apiKey)
    {
        var template = _configuration[ConfigExchangeRateApiUrlTemplate];
        if (string.IsNullOrWhiteSpace(template))
            template = DefaultExchangeRateApiUrlTemplate;

        return template.Replace("{api-key}", Uri.EscapeDataString(apiKey), StringComparison.Ordinal);
    }

    private TableClient CreateTableClient()
    {
        var tableName = _configuration[ConfigTableName];
        if (string.IsNullOrWhiteSpace(tableName))
            tableName = DefaultTableName;

        var connectionString = _configuration[ConfigTableConnectionString];
        if (!string.IsNullOrWhiteSpace(connectionString))
            return new TableClient(connectionString, tableName);

        var tableServiceUri = _configuration[ConfigTableServiceUri];
        if (string.IsNullOrWhiteSpace(tableServiceUri))
            tableServiceUri = DefaultTableServiceUri;

        return new TableClient(new Uri(tableServiceUri), tableName, new DefaultAzureCredential());
    }

    private sealed class ExchangeRateApiResponse
    {
        [JsonPropertyName("result")]
        public string Result { get; init; } = string.Empty;

        [JsonPropertyName("conversion_rate")]
        public decimal ConversionRate { get; init; }

        [JsonPropertyName("time_last_update_utc")]
        public string? TimeLastUpdateUtc { get; init; }
    }
}

public sealed record ExchangeRateSnapshot(decimal Rate, DateTimeOffset UpdatedAtUtc);
