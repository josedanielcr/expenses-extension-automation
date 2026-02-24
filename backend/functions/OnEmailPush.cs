using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace functions;

public class OnEmailPush
{
    private const string FunctionName = "OnEmailPush";
    private const string HttpMethodPost = "post";
    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix = "Bearer ";

    private const string ErrorInvalidJson = "Invalid JSON body.";
    private const string ErrorRequestRequired = "Request body is required.";
    private const string ErrorTokenRequired = "Authorization header with Bearer token is required.";
    private const string ErrorEmailsRequired = "emails must contain at least one entry.";
    private const string WarnInvalidJson = "Invalid request body JSON.";

    private readonly ILogger<OnEmailPush> _logger;
    private readonly GoogleTokenValidator _tokenValidator;
    private readonly IOpenAiExpenseParser _expenseParser;

    public OnEmailPush(
        ILogger<OnEmailPush> logger,
        GoogleTokenValidator tokenValidator,
        IOpenAiExpenseParser expenseParser)
    {
        _logger = logger;
        _tokenValidator = tokenValidator;
        _expenseParser = expenseParser;
    }

    [Function(FunctionName)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, HttpMethodPost)] HttpRequest req,
        CancellationToken ct)
    {
        var readResult = await TryReadRequestAsync(req, ct);
        if (readResult.Error is not null)
            return readResult.Error;

        var payload = readResult.Payload!;
        var token = ExtractTokenFromAuthorizationHeader(req);
        if (string.IsNullOrWhiteSpace(token))
            return BuildBadRequest(ErrorTokenRequired);

        var payloadValidationError = ValidateRequest(payload);
        if (payloadValidationError is not null)
            return payloadValidationError;

        var tokenValidationError = await ValidateTokenAsync(token, ct);
        if (tokenValidationError is not null)
            return tokenValidationError;

        var parsedEntries = await ParseEmailsAsync(payload.Emails, payload.Categories, ct);
        return BuildSuccessResponse(parsedEntries);
    }

    private async Task<(OnEmailPushRequest? Payload, IActionResult? Error)> TryReadRequestAsync(
        HttpRequest req,
        CancellationToken ct)
    {
        try
        {
            var payload = await req.ReadFromJsonAsync<OnEmailPushRequest>(cancellationToken: ct);
            if (payload is null)
                return (null, BuildBadRequest(ErrorRequestRequired));

            return (payload, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, WarnInvalidJson);
            return (null, BuildBadRequest(ErrorInvalidJson));
        }
    }

    private IActionResult? ValidateRequest(OnEmailPushRequest payload)
    {
        if (payload.Emails.Count == 0)
            return BuildBadRequest(ErrorEmailsRequired);

        return null;
    }

    private async Task<IActionResult?> ValidateTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            await _tokenValidator.ValidateTokenAsync(token, ct);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            return new UnauthorizedObjectResult(new { error = ex.Message });
        }
    }

    private async Task<List<ExpenseParseResult>> ParseEmailsAsync(
        IReadOnlyCollection<EmailEntry> emails,
        IReadOnlyCollection<string> categories,
        CancellationToken ct)
    {
        var parsedEntries = new List<ExpenseParseResult>(emails.Count);
        foreach (var email in emails)
        {
            if (string.IsNullOrWhiteSpace(email.Message))
            {
                parsedEntries.Add(CreateEmptyResult(email.Date));
                continue;
            }

            var parsed = await _expenseParser.ParseAsync(email, categories, ct);
            parsedEntries.Add(parsed);
        }

        return parsedEntries;
    }

    private static ExpenseParseResult CreateEmptyResult(string date) =>
        new()
        {
            Date = date,
            Amount = string.Empty,
            Category = string.Empty,
            Description = string.Empty,
        };

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
}
