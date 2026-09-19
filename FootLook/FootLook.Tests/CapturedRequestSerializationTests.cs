using System.Text.Json;
using FootLook.Core.Models;

namespace FootLook.Tests;

public class CapturedRequestSerializationTests
{
    [Fact]
    public void ObserverIds_are_never_serialized()
    {
        var capture = new CapturedRequest { Path = "/x", ObserverIds = new List<string> { "user-secret-id" } };

        var json = JsonSerializer.Serialize(capture);
        var webJson = JsonSerializer.Serialize(capture, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("observerIds", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("observerIds", webJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user-secret-id", json);
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
