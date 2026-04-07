using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace functions;

public class RefreshExchangeRate
{
    private const string FunctionName = "RefreshExchangeRate";
    // 06:00 UTC is 00:00 in Costa Rica (UTC-6).
    private const string MidnightCostaRicaUtcSchedule = "0 0 6 * * *";
    private readonly ExchangeRateService _exchangeRateService;
    private readonly ILogger<RefreshExchangeRate> _logger;

    public RefreshExchangeRate(
        ExchangeRateService exchangeRateService,
        ILogger<RefreshExchangeRate> logger)
    {
        _exchangeRateService = exchangeRateService;
        _logger = logger;
    }

    [Function(FunctionName)]
    public async Task Run(
        [TimerTrigger(MidnightCostaRicaUtcSchedule)] TimerInfo timerInfo,
        FunctionContext context,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "RefreshExchangeRate started. InvocationId={InvocationId} IsPastDue={IsPastDue}",
            context.InvocationId,
            timerInfo.IsPastDue);

        var snapshot = await _exchangeRateService.RefreshUsdToCrcRateAsync(ct);

        _logger.LogInformation(
            "RefreshExchangeRate completed. InvocationId={InvocationId} Rate={Rate} UpdatedAtUtc={UpdatedAtUtc}",
            context.InvocationId,
            snapshot.Rate,
            snapshot.UpdatedAtUtc);
    }
}
