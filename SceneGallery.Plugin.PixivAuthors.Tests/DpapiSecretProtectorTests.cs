namespace SceneGallery.Plugin.PixivAuthors.Tests;

public sealed class DpapiSecretProtectorTests
{
    private const string TestSecret = "test-secret-value";

    [Fact]
    public void ProtectAndUnprotect_RoundTripsForCurrentUser()
    {
        var protectedValue = DpapiSecretProtector.Protect(TestSecret);

        Assert.NotNull(protectedValue);
        Assert.StartsWith(DpapiSecretProtector.Prefix, protectedValue);
        Assert.DoesNotContain(TestSecret, protectedValue);

        var plaintext = DpapiSecretProtector.Unprotect(
            protectedValue,
            "testSecret",
            _ => { },
            out var needsMigration);

        Assert.Equal(TestSecret, plaintext);
        Assert.False(needsMigration);
    }

    [Fact]
    public void Unprotect_LegacyPlaintextPassesThroughAndRequiresMigration()
    {
        var plaintext = DpapiSecretProtector.Unprotect(
            TestSecret,
            "testSecret",
            _ => { },
            out var needsMigration);

        Assert.Equal(TestSecret, plaintext);
        Assert.True(needsMigration);
    }

    [Fact]
    public void Unprotect_TamperedPayloadReturnsNullWithoutLoggingSecret()
    {
        var protectedValue = DpapiSecretProtector.Protect(TestSecret)!;
        var tamperedValue = Tamper(protectedValue);
        var logs = new List<string>();

        var plaintext = DpapiSecretProtector.Unprotect(
            tamperedValue,
            "testSecret",
            logs.Add,
            out var needsMigration);

        Assert.Null(plaintext);
        Assert.False(needsMigration);
        var warning = Assert.Single(logs);
        Assert.Equal(
            "WARNING: Secret setting 'testSecret' could not be decrypted (DPAPI decryption failed). It may belong to a different computer or Windows user; configure it again.",
            warning);
        Assert.DoesNotContain(TestSecret, warning);
        Assert.DoesNotContain(tamperedValue, warning);
    }

    [Fact]
    public void Unprotect_InvalidBase64ReturnsNull()
    {
        var logs = new List<string>();

        var plaintext = DpapiSecretProtector.Unprotect(
            DpapiSecretProtector.Prefix + "not-base64!",
            "testSecret",
            logs.Add,
            out var needsMigration);

        Assert.Null(plaintext);
        Assert.False(needsMigration);
        Assert.Equal(
            "WARNING: Secret setting 'testSecret' could not be decrypted (invalid base64). It may belong to a different computer or Windows user; configure it again.",
            Assert.Single(logs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullAndEmpty_AreNotProtectedOrMarkedForMigration(string? value)
    {
        Assert.Equal(value, DpapiSecretProtector.Protect(value));

        var plaintext = DpapiSecretProtector.Unprotect(
            value,
            "testSecret",
            _ => throw new InvalidOperationException("No warning expected."),
            out var needsMigration);

        Assert.Equal(value, plaintext);
        Assert.False(needsMigration);
    }

    private static string Tamper(string protectedValue)
    {
        var bytes = Convert.FromBase64String(protectedValue[DpapiSecretProtector.Prefix.Length..]);
        bytes[^1] ^= 0xff;
        return DpapiSecretProtector.Prefix + Convert.ToBase64String(bytes);
    }
}
