using System.Security.Claims;
using System.Text.Json;

namespace FootLook.Central.Endpoints;

/// <summary>Small helpers shared by the endpoint files: error shape, body reading, the caller's id.</summary>
internal static class Api
{
    // Request bodies here are a few hundred bytes; the Microsoft ID token is the biggest thing anyone posts.
    public const int MaxSmallBodyBytes = 8 * 1024;
    public const int MaxSignInBodyBytes = 32 * 1024;

    // Web defaults, but a number must be a JSON number: "3" is not 3.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };

    /// <summary>Every error is exactly <c>{ "error": "&lt;code&gt;" }</c>: no reasons, no exception text.</summary>
    public static IResult Error(int statusCode, string error) => Results.Json(new { error }, statusCode: statusCode);

    public static IResult ProjectNotFound() => Error(StatusCodes.Status404NotFound, "project_not_found");

    public static IResult InvalidRequest() => Error(StatusCodes.Status400BadRequest, "invalid_request");

    /// <summary>The id of the signed-in user (the <c>sub</c> of the central session token), if it is a valid guid.</summary>
    public static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        var subject = user.FindFirst("sub")?.Value;
        return subject is not null && Guid.TryParse(subject, out userId);
    }

    public enum BodyKind { Empty, Invalid, Value }

    public readonly record struct Body<T>(BodyKind Kind, T? Value) where T : class;

    /// <summary>Reads a JSON body of at most <paramref name="maxBytes"/>. Malformed, mistyped or oversized is <see cref="BodyKind.Invalid"/>.</summary>
    public static async Task<Body<T>> ReadBodyAsync<T>(HttpRequest request, int maxBytes, CancellationToken cancellationToken) where T : class
    {
        if (request.ContentLength is > 0 and var declared && declared > maxBytes)
        {
            return new Body<T>(BodyKind.Invalid, null);
        }

        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return new Body<T>(BodyKind.Invalid, null);
                }

                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length == 0)
            {
                return new Body<T>(BodyKind.Empty, null);
            }

            var value = JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions);
            return value is null ? new Body<T>(BodyKind.Invalid, null) : new Body<T>(BodyKind.Value, value);
        }
        catch (JsonException)
        {
            return new Body<T>(BodyKind.Invalid, null);
        }
        catch (BadHttpRequestException)
        {
            return new Body<T>(BodyKind.Invalid, null);
        }
    }

    public static DateTime TruncateToSeconds(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
}
