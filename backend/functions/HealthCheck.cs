using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace functions;

public class HealthCheck
{
    private const string FunctionName = "HealthCheck";
    private const string HttpMethodGet = "get";
    private const string RouteHealthCheck = "healthcheck";
    private readonly ILogger<HealthCheck> _logger;

    public HealthCheck(ILogger<HealthCheck> logger)
    {
        _logger = logger;
    }

    [Function(FunctionName)]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, HttpMethodGet, Route = RouteHealthCheck)] HttpRequest req,
        FunctionContext context)
    {
        _logger.LogInformation(
            "HealthCheck ping received. InvocationId={InvocationId}",
            context.InvocationId);

        return new OkObjectResult(new
        {
            status = "ok",
            service = "AI-Gastos backend",
            utcTimestamp = DateTime.UtcNow,
        });
    }
}
