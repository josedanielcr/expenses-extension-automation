using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.RegularExpressions;

namespace functions;

public class OnEmailPush
{
    private const string FunctionName = "OnEmailPush";
    private const string HttpMethodPost = "post";
    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix = "Bearer ";

    private const string ErrorRequestRequired = "Request body is required.";
    private const string ErrorTokenRequired = "Authorization header with Bearer token is required.";
    private const string ErrorEmailsRequired = "emails must contain at least one entry.";
    private const string CurrencyUsd = "USD";
    private const string CurrencyCrc = "CRC";
    private const string CurrencyCodePattern = @"\(([A-Za-z]{3})\)";
    private static readonly Regex CurrencyCodeRegex = new(CurrencyCodePattern, RegexOptions.Compiled);
    private readonly ILogger<OnEmailPush> _logger;
    private readonly GoogleTokenValidator _tokenValidator;
    private readonly IOpenAiExpenseParser _expenseParser;
    private readonly ExchangeRateService _exchangeRateService;

    public OnEmailPush(
        ILogger<OnEmailPush> logger,
        GoogleTokenValidator tokenValidator,
        IOpenAiExpenseParser expenseParser,
        ExchangeRateService exchangeRateService)
    {
        _logger = logger;
        _tokenValidator = tokenValidator;
        _expenseParser = expenseParser;
        _exchangeRateService = exchangeRateService;
    }

    [Function(FunctionName)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, HttpMethodPost)] HttpRequest req,
        FunctionContext context,
        CancellationToken ct)
    {
        var payload = await req.ReadFromJsonAsync<OnEmailPushRequest>(cancellationToken: ct);
        if (payload is null)
            return BuildBadRequest(ErrorRequestRequired);

        _logger.LogInformation(
            "OnEmailPush request received. InvocationId={InvocationId} Emails={EmailCount} Categories={CategoryCount}",
            context.InvocationId,
            payload.Emails.Count,
            payload.Categories.Count);

        var token = ExtractTokenFromAuthorizationHeader(req);
        if (string.IsNullOrWhiteSpace(token))
            return BuildBadRequest(ErrorTokenRequired);

        var payloadValidationError = ValidateRequest(payload);
        if (payloadValidationError is not null)
            return payloadValidationError;

        await _tokenValidator.ValidateTokenAsync(token, ct);

        var parsedEntries = await ParseEmailsAsync(
            payload.Emails,
            payload.Categories,
            payload.ExclusionRules,
            ct);
        await ConvertUsdEntriesToCrcAsync(parsedEntries, ct);
        var orderedEntries = OrderEntriesByDateOldestFirst(parsedEntries);
        _logger.LogInformation(
            "OnEmailPush completed. InvocationId={InvocationId} ParsedEntries={ParsedCount}",
            context.InvocationId,
            orderedEntries.Count);

        return BuildSuccessResponse(orderedEntries);
    }

    private IActionResult? ValidateRequest(OnEmailPushRequest payload)
    {
        if (payload.Emails.Count == 0)
            return BuildBadRequest(ErrorEmailsRequired);

        return null;
    }

    private async Task<List<ExpenseParseResult>> ParseEmailsAsync(
        IReadOnlyCollection<EmailEntry> emails,
        IReadOnlyCollection<string> categories,
        IReadOnlyCollection<CategoryExclusionRule> exclusionRules,
        CancellationToken ct)
    {
        var emailList = emails as IReadOnlyList<EmailEntry> ?? emails.ToList();
        return await _expenseParser.ParseBatchAsync(emailList, categories, exclusionRules, ct);
    }

    private static IActionResult BuildBadRequest(string message) =>
        new BadRequestObjectResult(new { error = message });

    private static string ExtractTokenFromAuthorizationHeader(HttpRequest req)
    {
        if (!req.Headers.TryGetValue(AuthorizationHeader, out var headerValues))
            return string.Empty;

        var header = headerValues.ToString().Trim();
        if (header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return header[BearerPrefix.Length..].Trim();

        return header;
    }

    private static IActionResult BuildSuccessResponse(List<ExpenseParseResult> parsedEntries) =>
        new OkObjectResult(new
        {
            total = parsedEntries.Count,
            entries = parsedEntries,
        });

    private static List<ExpenseParseResult> OrderEntriesByDateOldestFirst(
        IReadOnlyList<ExpenseParseResult> entries)
    {
        var indexed = entries
            .Select((entry, index) => new
            {
                Entry = entry,
                Index = index,
                SortDate = TryParseEntryDate(entry.Date),
            })
            .ToList();

        indexed.Sort((left, right) =>
        {
            var leftHasDate = left.SortDate.HasValue;
            var rightHasDate = right.SortDate.HasValue;

            if (leftHasDate && rightHasDate)
            {
                var dateCompare = DateTimeOffset.Compare(left.SortDate!.Value, right.SortDate!.Value);
                if (dateCompare != 0)
                    return dateCompare;
            }
            else if (leftHasDate != rightHasDate)
            {
                return leftHasDate ? -1 : 1;
            }

            return left.Index.CompareTo(right.Index);
        });

        return indexed.Select(item => item.Entry).ToList();
    }

    private static DateTimeOffset? TryParseEntryDate(string? rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate))
            return null;

        var supportedFormats = new[]
        {
            "dd/MM/yyyy",
            "d/M/yyyy",
            "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ssZ",
        };

        var value = NormalizeDateForSorting(rawDate);

        // Slash-based dates are expected in local day-first format.
        if (value.Contains('/'))
        {
            if (DateTimeOffset.TryParseExact(
                    value,
                    new[] { "dd/MM/yyyy", "d/M/yyyy" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var dayFirstParsed))
            {
                return dayFirstParsed;
            }
        }

        if (DateTimeOffset.TryParseExact(
                value,
                supportedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var parsedExact))
        {
            return parsedExact;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedGeneric))
        {
            return parsedGeneric;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out parsedGeneric))
        {
            return parsedGeneric;
        }

        return null;
    }

    private static string NormalizeDateForSorting(string rawDate)
    {
        var value = rawDate.Trim();
        if (value.Length == 0)
            return string.Empty;

        // Drop trailing timezone labels like "(UTC)" while keeping numeric offset.
        value = Regex.Replace(value, @"\s*\([A-Za-z]{2,8}\)\s*$", string.Empty);
        // Convert RFC 2822 offset style (+0000) to ISO style (+00:00) for stable parsing.
        value = Regex.Replace(value, @"([+-]\d{2})(\d{2})\b", "$1:$2");
        // Keep tokenization predictable.
        value = Regex.Replace(value, @"\s+", " ").Trim();

        return value;
    }

    private async Task ConvertUsdEntriesToCrcAsync(
        IReadOnlyCollection<ExpenseParseResult> entries,
        CancellationToken ct)
    {
        var usdEntries = entries
            .Where(IsUsdCurrencyEntry)
            .ToList();
        if (usdEntries.Count == 0)
            return;

        var conversionRate = await _exchangeRateService.GetUsdToCrcRateAsync(ct);
        var convertedCount = 0;
        foreach (var entry in usdEntries)
        {
            if (!decimal.TryParse(entry.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var originalAmount))
                continue;

            var originalUsdAmount = FormatAmount(originalAmount);
            var convertedAmount = decimal.Round(originalAmount * conversionRate, 0, MidpointRounding.AwayFromZero);
            entry.Amount = FormatAmount(convertedAmount);
            entry.Description = ReplaceCurrencyCode(entry.Description, CurrencyUsd, CurrencyCrc);
            entry.Description = AppendOriginalUsdAmount(entry.Description, originalUsdAmount);
            convertedCount++;
        }

        _logger.LogInformation(
            "USD entries converted to CRC. ConvertedEntries={ConvertedEntries} Rate={Rate}",
            convertedCount,
            conversionRate);
    }

    private static bool IsUsdCurrencyEntry(ExpenseParseResult entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Description))
            return false;

        var match = CurrencyCodeRegex.Match(entry.Description);
        if (!match.Success)
            return false;

        var detectedCode = match.Groups[1].Value;
        return string.Equals(detectedCode, CurrencyUsd, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceCurrencyCode(string description, string fromCode, string toCode)
    {
        return CurrencyCodeRegex.Replace(
            description ?? string.Empty,
            match =>
            {
                var currentCode = match.Groups[1].Value;
                if (!string.Equals(currentCode, fromCode, StringComparison.OrdinalIgnoreCase))
                    return match.Value;

                return $"({toCode})";
            },
            1);
    }

    private static string FormatAmount(decimal amount)
    {
        var normalized = amount.ToString("0.############################", CultureInfo.InvariantCulture);
        return normalized.TrimEnd('0').TrimEnd('.');
    }

    private static string AppendOriginalUsdAmount(string description, string usdAmount)
    {
        if (string.IsNullOrWhiteSpace(usdAmount))
            return description ?? string.Empty;

        var currentDescription = description?.Trim() ?? string.Empty;
        var suffix = $"({usdAmount}$)";

        if (currentDescription.Length == 0)
            return suffix;

        return $"{currentDescription} {suffix}";
    }
}
