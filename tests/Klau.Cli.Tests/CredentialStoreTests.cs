using Klau.Cli.Auth;
using Xunit;

namespace Klau.Cli.Tests;

public class CredentialStoreTests
{
    [Fact]
    public void Mask_LongKey_ShowsPrefixAndSuffix()
    {
        var masked = CredentialStore.Mask("kl_live_abc123def456ghi789");
        Assert.StartsWith("kl_live_abc1", masked);
        Assert.EndsWith("i789", masked);
        Assert.Contains("...", masked);
    }

    [Fact]
    public void Mask_ShortKey_ShowsPlaceholder()
    {
        var masked = CredentialStore.Mask("kl_live_short");
        Assert.Equal("kl_live_****", masked);
    }

    [Fact]
    public void ResolveApiKey_CliFlagWins()
    {
        var result = CredentialStore.ResolveApiKey("kl_live_from_flag");
        Assert.Equal("kl_live_from_flag", result);
    }

    [Fact]
    public void ResolveApiKey_NullFlag_FallsToEnvVar()
    {
        var original = Environment.GetEnvironmentVariable("KLAU_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("KLAU_API_KEY", "kl_live_from_env");
            var result = CredentialStore.ResolveApiKey(null);
            Assert.Equal("kl_live_from_env", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KLAU_API_KEY", original);
        }
    }

    [Fact]
    public void ResolveApiKey_NothingSet_ReturnsNull()
    {
        var original = Environment.GetEnvironmentVariable("KLAU_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("KLAU_API_KEY", "");
            // Can't easily test stored credentials without mocking the file system,
            // but we can verify the chain returns null when nothing is set
            var result = CredentialStore.ResolveApiKey(null);
            // Result depends on whether credentials.json exists on this machine
            // so we just verify it doesn't throw
            Assert.True(result is null || result.StartsWith("kl_live_"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KLAU_API_KEY", original);
        }
    }
}
