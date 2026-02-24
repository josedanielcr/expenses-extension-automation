using System.Net.Http.Json;
using System.Net.Http.Headers;

public sealed class GoogleTokenValidator
{
    private const string BearerPrefix = "Bearer ";
    private const string UserInfoUrl = "https://www.googleapis.com/oauth2/v3/userinfo";
    private readonly HttpClient httpClient;

    public GoogleTokenValidator(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<GoogleTokenInfo?> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        var normalizedToken = NormalizeToken(token);
        if (string.IsNullOrWhiteSpace(normalizedToken))
            throw new UnauthorizedAccessException("Invalid Google token: token is empty.");

        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedToken);
        request.Headers.Accept.ParseAdd("application/json");
        using var resp = await httpClient.SendAsync(request, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new UnauthorizedAccessException($"Invalid Google token. Status={(int)resp.StatusCode} Body={body}");
        }

        var info = await resp.Content.ReadFromJsonAsync<GoogleTokenInfo>(cancellationToken: ct)
            ?? throw new UnauthorizedAccessException("Invalid Google token (empty response).");
        if (string.IsNullOrWhiteSpace(info.UserId))
            info.UserId = info.Sub;

        return info;
    }

    private static string NormalizeToken(string rawToken)
    {
        var token = rawToken?.Trim() ?? string.Empty;
        if (token.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            token = token[BearerPrefix.Length..].Trim();
        }

        return token.Trim('"');
    }
}
