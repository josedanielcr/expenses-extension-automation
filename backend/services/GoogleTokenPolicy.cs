public static class GoogleTokenPolicy
{
    public static void Enfore(GoogleTokenInfo info, string expectedAudience, params string[] expectedScopes)
    {
        if (!string.Equals(info.Aud, expectedAudience, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Token audience mismatch.");

        var scopes = (info.Scope ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var s in expectedScopes)
        {
            if (!scopes.Contains(s))
                throw new UnauthorizedAccessException($"Missing required scope: {s}");
        }
    }
}