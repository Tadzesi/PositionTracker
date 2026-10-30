using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace PositionTracker.Api.Endpoints;

/// <summary>Reads size-capped JSON request bodies for the minimal-API handlers.</summary>
internal static class JsonBody
{
    /// <summary>
    /// Deserializes the body, capped at <paramref name="maxBytes"/>. Returns a 413 result as <c>Error</c> when the
    /// body is too large: by <c>Content-Length</c> before reading, otherwise while reading, both through the
    /// server's <see cref="IHttpMaxRequestBodySizeFeature"/> (Kestrel) and a counting stream (any server).
    /// Returns a <c>null</c> body for malformed JSON.
    /// </summary>
    public static async Task<(T? Body, IResult? Error)> ReadAsync<T>(
        HttpContext context, JsonSerializerOptions options, long maxBytes)
        where T : class
    {
        if (context.Request.ContentLength > maxBytes) return (null, TooLarge(maxBytes));
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maxBytes;
        try
        {
            var body = new MaxLengthReadStream(context.Request.Body, maxBytes);
            return (await JsonSerializer.DeserializeAsync<T>(body, options, context.RequestAborted), null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
        catch (BadHttpRequestException ex)
        {
            return (null, Results.Problem(ex.Message, statusCode: ex.StatusCode));
        }
    }

    private static IResult TooLarge(long maxBytes) =>
        Results.Problem($"The request body must not exceed {maxBytes} bytes.",
            statusCode: StatusCodes.Status413PayloadTooLarge);
}
