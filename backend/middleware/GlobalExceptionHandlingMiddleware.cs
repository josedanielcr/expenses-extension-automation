using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

public sealed class GlobalExceptionHandlingMiddleware : IFunctionsWorkerMiddleware
{
    private const string ErrorInvalidJson = "Invalid JSON body.";
    private const string ErrorInternalServer = "Internal server error while processing emails.";
    private const string ErrorOpenAiParsing = "OpenAI parsing failed.";

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var logger = context.GetLogger<GlobalExceptionHandlingMiddleware>();
            var (statusCode, payload, logMessage) = MapException(ex);
            logger.LogError(ex, "{LogMessage} InvocationId={InvocationId}", logMessage, context.InvocationId);

            context.GetInvocationResult().Value = new ObjectResult(payload)
            {
                StatusCode = statusCode,
            };
        }
    }

    private static (int StatusCode, object Payload, string LogMessage) MapException(Exception ex)
    {
        return ex switch
        {
            UnauthorizedAccessException unauthorized => (
                StatusCodes.Status401Unauthorized,
                new { error = unauthorized.Message },
                "Unauthorized request."),

            JsonException => (
                StatusCodes.Status400BadRequest,
                new { error = ErrorInvalidJson },
                "Invalid JSON payload."),

            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                new { error = ErrorInvalidJson },
                "Invalid JSON payload."),

            InvalidOperationException invalidOperation => (
                StatusCodes.Status500InternalServerError,
                new
                {
                    error = ErrorOpenAiParsing,
                    details = invalidOperation.Message,
                    innerError = invalidOperation.InnerException?.Message ?? string.Empty,
                },
                "OpenAI parsing failed."),

            _ => (
                StatusCodes.Status500InternalServerError,
                new { error = ErrorInternalServer },
                "Unhandled exception.")
        };
    }
}
