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

    private const string ErrorRequestRequired = "Request body is required.";
    private const string ErrorTokenRequired = "Authorization header with Bearer token is required.";
    private const string ErrorEmailsRequired = "emails must contain at least one entry.";
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

        var parsedEntries = await ParseEmailsAsync(payload.Emails, payload.Categories, ct);
        _logger.LogInformation(
            "OnEmailPush completed. InvocationId={InvocationId} ParsedEntries={ParsedCount}",
            context.InvocationId,
            parsedEntries.Count);

        return BuildSuccessResponse(parsedEntries);
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
        CancellationToken ct)
    {
        var emailList = emails as IReadOnlyList<EmailEntry> ?? emails.ToList();
        return await _expenseParser.ParseBatchAsync(emailList, categories, ct);
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
}
