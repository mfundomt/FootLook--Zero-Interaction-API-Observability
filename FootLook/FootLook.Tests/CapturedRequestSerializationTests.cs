using System.Text.Json;
using FootLook.Core.Models;

namespace FootLook.Tests;

public class CapturedRequestSerializationTests
{
    [Fact]
    public void ObserverSessionIds_are_never_serialized()
    {
        var capture = new CapturedRequest { Path = "/x", ObserverSessionIds = new List<string> { "session-secret-id" } };

        var json = JsonSerializer.Serialize(capture);
        var webJson = JsonSerializer.Serialize(capture, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("observerSessionIds", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("observerSessionIds", webJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session-secret-id", json);
    }

    [Fact]
    public void FootLookUserSummary_omits_the_password_hash()
    {
        var user = new FootLookUser("id", "a@b.co", "A", "SECRET-HASH", true, DateTime.UtcNow);

        var json = JsonSerializer.Serialize(FootLookUserSummary.From(user));

        Assert.DoesNotContain("SECRET-HASH", json);
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
    }
}
