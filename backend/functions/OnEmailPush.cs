using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace functions;

public class OnEmailPush
{
    private readonly ILogger<OnEmailPush> _logger;

    public OnEmailPush(ILogger<OnEmailPush> logger)
    {
        _logger = logger;
    }

    [Function("OnEmailPush")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");
        return new OkObjectResult("Welcome to Azure Functions!");
    }
}
