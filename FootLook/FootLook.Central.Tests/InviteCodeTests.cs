using System.Text.RegularExpressions;
using FootLook.Central.Data;
using FootLook.Central.Options;
using FootLook.Central.Security;

namespace FootLook.Central.Tests;

public class InviteCodeTests
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    [Fact]
    public void Codes_are_FL_dash_plus_ten_unambiguous_characters()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.Matches("^FL-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{10}$", InviteCodes.Generate());
        }
    }

    [Fact]
    public void The_alphabet_has_no_lookalikes()
    {
        Assert.Equal(32, Alphabet.Length);
        foreach (var ambiguous in "IO01")
        {
            Assert.DoesNotContain(ambiguous, Alphabet);
        }

        var seen = new HashSet<char>();
        for (var i = 0; i < 3000; i++)
        {
            foreach (var c in InviteCodes.Generate().AsSpan(3))
            {
                seen.Add(c);
            }
        }

        // Every symbol shows up (so the generator really uses the whole alphabet) and nothing else does.
        Assert.Equal(Alphabet.OrderBy(c => c), seen.OrderBy(c => c));
    }

    [Fact]
    public void Codes_do_not_repeat()
    {
        var codes = Enumerable.Range(0, 5000).Select(_ => InviteCodes.Generate()).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("FL-ABCDEFGH23", "FL-ABCDEFGH23")]
    [InlineData("fl-abcdefgh23", "FL-ABCDEFGH23")]
    [InlineData("  FL-ABCDEFGH23  ", "FL-ABCDEFGH23")]
    [InlineData("FLABCDEFGH23", "FL-ABCDEFGH23")]
    [InlineData("FL ABCDE FGH23", "FL-ABCDEFGH23")]
    [InlineData("FL-ABCDE-FGH23", "FL-ABCDEFGH23")]
    public void What_a_person_types_is_normalised(string typed, string expected)
    {
        Assert.True(InviteCodes.TryNormalize(typed, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FL-ABCDEFGH2")]        // too short
    [InlineData("FL-ABCDEFGH234")]      // too long
    [InlineData("XX-ABCDEFGH23")]       // wrong prefix
    [InlineData("FL-ABCDEFGH0O")]       // ambiguous characters are not in the alphabet
    [InlineData("FL-ABCDEFGH1I")]
    [InlineData("FL-ABCDEFGH2!")]
    [InlineData("FL-ABCDEFGH23; DROP TABLE Users")]
    public void Anything_that_cannot_be_a_code_is_refused(string? typed)
    {
        Assert.False(InviteCodes.TryNormalize(typed, out _));
    }

    [Fact]
    public void The_hash_is_64_lowercase_hex_characters_and_stable()
    {
        var hash = InviteCodes.Hash("FL-ABCDEFGH23");
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, InviteCodes.Hash("FL-ABCDEFGH23"));
        Assert.NotEqual(hash, InviteCodes.Hash("FL-ABCDEFGH24"));
        Assert.DoesNotContain("ABCDEFGH23", hash, StringComparison.OrdinalIgnoreCase);
    }
}

public class ProjectIdTests
{
    [Fact]
    public void Ids_are_prj_plus_16_base32_characters()
    {
        for (var i = 0; i < 200; i++)
        {
            var id = ProjectIds.New();
            Assert.Matches("^prj_[a-z2-7]{16}$", id);
            Assert.True(ProjectIds.IsWellFormed(id));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("prj_")]
    [InlineData("prj_abcdefghijklmnop1")]   // '1' is not base32
    [InlineData("PRJ_abcdefghijklmnop")]
    [InlineData("prj_ABCDEFGHIJKLMNOP")]
    [InlineData("prj_abcdefghijklmno")]
    [InlineData("prj_abcdefghijklmnopq")]
    [InlineData("prj_abcdefghijklmn'--")]
    [InlineData("xyz_abcdefghijklmnop")]
    public void Anything_else_is_not_a_project_id(string? id)
    {
        Assert.False(ProjectIds.IsWellFormed(id));
    }
}

public class RedeemThrottleTests
{
    [Fact]
    public void Blocks_after_the_configured_failures_and_only_that_user_and_only_for_the_window()
    {
        var clock = new ManualTimeProvider();
        var throttle = new RedeemThrottle(clock, new CentralOptions { RedeemMaxFailures = 3, RedeemFailureWindowMinutes = 15 });
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        for (var i = 0; i < 2; i++)
        {
            throttle.RecordFailure(a);
        }

        Assert.False(throttle.IsBlocked(a));
        throttle.RecordFailure(a);
        Assert.True(throttle.IsBlocked(a));
        Assert.False(throttle.IsBlocked(b));

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(throttle.IsBlocked(a));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(throttle.IsBlocked(a));
    }

    [Fact]
    public void Failures_age_out_one_by_one()
    {
        var clock = new ManualTimeProvider();
        var throttle = new RedeemThrottle(clock, new CentralOptions { RedeemMaxFailures = 2, RedeemFailureWindowMinutes = 10 });
        var user = Guid.NewGuid();

        throttle.RecordFailure(user);
        clock.Advance(TimeSpan.FromMinutes(6));
        throttle.RecordFailure(user);
        Assert.True(throttle.IsBlocked(user));

        clock.Advance(TimeSpan.FromMinutes(5)); // the first failure is now 11 minutes old
        Assert.False(throttle.IsBlocked(user));
    }
}
