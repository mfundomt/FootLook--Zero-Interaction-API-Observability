using FootLook.Core.Security;

namespace FootLook.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_is_not_the_plain_password_and_uses_versioned_format()
    {
        var hash = FootLookPasswordHasher.Hash("correct horse battery");

        Assert.NotEqual("correct horse battery", hash);
        Assert.DoesNotContain("correct horse battery", hash);
        Assert.StartsWith("v1.", hash);
        Assert.Equal(4, hash.Split('.').Length);
    }

    [Fact]
    public void Verify_accepts_the_right_password_and_rejects_others()
    {
        var hash = FootLookPasswordHasher.Hash("correct horse battery");

        Assert.True(FootLookPasswordHasher.Verify("correct horse battery", hash));
        Assert.False(FootLookPasswordHasher.Verify("Correct horse battery", hash));
        Assert.False(FootLookPasswordHasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void Hashing_the_same_password_twice_gives_different_hashes_that_both_verify()
    {
        var first = FootLookPasswordHasher.Hash("same-password-1");
        var second = FootLookPasswordHasher.Hash("same-password-1");

        Assert.NotEqual(first, second);
        Assert.True(FootLookPasswordHasher.Verify("same-password-1", first));
        Assert.True(FootLookPasswordHasher.Verify("same-password-1", second));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("v1.210000.onlythree")]
    [InlineData("v2.210000.AAAA.AAAA")]
    [InlineData("v1.abc.AAAA.AAAA")]
    [InlineData("v1.0.AAAA.AAAA")]
    [InlineData("v1.-5.AAAA.AAAA")]
    [InlineData("v1.210000.!!!notbase64!!!.AAAA")]
    [InlineData("v1.210000.AAAA.!!!notbase64!!!")]
    [InlineData("v1.210000.AAAA.AAAA.extra")]
    public void Verify_returns_false_for_malformed_stored_hash(string? stored)
    {
        Assert.False(FootLookPasswordHasher.Verify("whatever-password", stored));
    }

    [Fact]
    public void Verify_honours_the_iteration_count_stored_in_the_hash()
    {
        // Build a hash with a lower work factor than the current default; Verify must read
        // the iterations back out of the string rather than assuming the default.
        var salt = new byte[16];
        var derived = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "legacy-password", salt, 1000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var stored = $"v1.1000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(derived)}";

        Assert.True(FootLookPasswordHasher.Verify("legacy-password", stored));
        Assert.False(FootLookPasswordHasher.Verify("other-password", stored));
    }

    [Fact]
    public void BurnVerifyTime_does_not_throw()
    {
        FootLookPasswordHasher.BurnVerifyTime("anything");
    }
}
