namespace PositionTracker.Email;

/// <summary>Builds the emailed links (confirm, sign-in, invitation) from <see cref="EmailOptions.PublicBaseUrl"/>.</summary>
public static class EmailLinks
{
    /// <summary>
    /// <c>{publicBaseUrl}/{path}?token={token}</c> with the token URL-escaped. Throws
    /// <see cref="InvalidOperationException"/> when the base URL is not configured or not absolute http(s).
    /// </summary>
    public static string Build(string publicBaseUrl, string path, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (!IsHttpUrl(publicBaseUrl))
            throw new InvalidOperationException("Email:PublicBaseUrl must be configured as an absolute http or https URL.");
        return publicBaseUrl.TrimEnd('/') + "/" + path.TrimStart('/') + "?token=" + Uri.EscapeDataString(token);
    }

    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrEmpty(uri.Host);
}
