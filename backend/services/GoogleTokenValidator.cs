using System.Net.Http.Json;

public sealed class GoogleTokenValidator
{
    private readonly HttpClient httpClient;

    public GoogleTokenValidator(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<GoogleTokenInfo?> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        var url = $"https://www.googleapis.com/oauth2/v3/tokeninfo?access_token={Uri.EscapeDataString(token)}";
        using var resp = await httpClient.GetAsync(url, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new UnauthorizedAccessException($"Invalid Google token. Status={(int)resp.StatusCode} Body={body}");
        }

        var info = await resp.Content.ReadFromJsonAsync<GoogleTokenInfo>(cancellationToken: ct)
            ?? throw new UnauthorizedAccessException("Invalid Google token (empty response).");
        return info;
    }
}