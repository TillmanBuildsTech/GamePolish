using GamePolish.Core.Operations;

namespace GamePolish.Core.Tests.Operations;

/// <summary>
/// Covers <see cref="SecretRedactor"/> (.todo/212, acceptance criterion 2): every built-in pattern in a sample
/// string, and <see cref="SecretRedactor.RegisterSecret"/> redacting a value by exact match even when its shape
/// is not recognised.
/// </summary>
/// <remarks>
/// The registered values live in a process-wide set by design, so each test registers a value unique to itself
/// and asserts only about strings that contain it. Nothing here depends on the set being empty at any point -
/// including when the suite runs in a different order.
/// </remarks>
public class SecretRedactorTests
{
    private const string GitHubToken = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    [Fact]
    public void GitHubTokensOfEveryFamilyAreRedacted()
    {
        string[] tokens =
        [
            "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "ghu_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "ghs_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "ghr_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
        ];

        foreach (string token in tokens)
        {
            string redacted = SecretRedactor.Redact($"clone failed for {token}");

            Assert.Equal("clone failed for ***", redacted);
        }
    }

    [Fact]
    public void BearerTokensAreRedactedWithTheirPrefixKept()
    {
        string redacted = SecretRedactor.Redact("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig");

        Assert.Equal("Authorization: Bearer ***", redacted);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialsInAUrlAreRedactedWithTheSchemeAndHostKept()
    {
        string redacted = SecretRedactor.Redact(
            "fatal: unable to access 'https://x-access-token:hunter2@github.com/nope/nope/': 403");

        Assert.Equal("fatal: unable to access 'https://***@github.com/nope/nope/': 403", redacted);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AwsAccessKeyIdsAreRedacted()
    {
        string redacted = SecretRedactor.Redact("aws_access_key_id = AKIAIOSFODNN7EXAMPLE");

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("passwd: hunter2")]
    [InlineData("token = hunter2")]
    [InlineData("secret=hunter2")]
    [InlineData("api_key=hunter2")]
    [InlineData("api-key: hunter2")]
    [InlineData("authorization=hunter2")]
    public void KeyValuePairsKeepTheKeyAndRedactTheValue(string sample)
    {
        string redacted = SecretRedactor.Redact(sample);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);

        // The key survives, so a reader still knows what kind of value was removed.
        Assert.StartsWith(sample[..sample.IndexOf(sample.Contains('=') ? '=' : ':')], redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatchAllIsCaseInsensitive()
    {
        string redacted = SecretRedactor.Redact("PASSWORD=Hunter2 and TOKEN: Hunter3");

        Assert.DoesNotContain("Hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Hunter3", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegisteredSecretIsRedactedByExactMatch()
    {
        const string Secret = "not-a-recognisable-shape-9f3a7c";

        SecretRedactor.RegisterSecret(Secret);

        Assert.Equal("s3 credentials are *** here", SecretRedactor.Redact($"s3 credentials are {Secret} here"));
    }

    [Fact]
    public void ARegisteredGitHubTokenIsRedactedTwiceOverWithoutLeavingAFragment()
    {
        const string Token = "ghp_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";

        SecretRedactor.RegisterSecret(Token);

        string redacted = SecretRedactor.Redact($"cloning with {Token} now");

        Assert.Equal("cloning with *** now", redacted);
        Assert.DoesNotContain("ghp_", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortRegisteredSecretIsIgnored()
    {
        SecretRedactor.RegisterSecret("abc");

        Assert.Equal("abcdef is a word", SecretRedactor.Redact("abcdef is a word"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullEmptyAndBlankRegistrationIsIgnored(string? secret)
    {
        SecretRedactor.RegisterSecret(secret);

        Assert.Equal("unchanged text", SecretRedactor.Redact("unchanged text"));
    }

    [Fact]
    public void RedactNeverReturnsNull()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
        Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
    }

    [Fact]
    public void ContainsSecretAgreesWithRedact()
    {
        Assert.True(SecretRedactor.ContainsSecret($"token {GitHubToken}"));
        Assert.True(SecretRedactor.ContainsSecret("Authorization: Bearer abc.def.ghi"));
        Assert.False(SecretRedactor.ContainsSecret("Source staged from https://github.com/brues-code/pfUI"));
        Assert.False(SecretRedactor.ContainsSecret(null));
        Assert.False(SecretRedactor.ContainsSecret(string.Empty));
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        const string Text = "Snapshot created 20261015T214203123Z (0 files) in C:\\Games\\World of Warcraft";

        Assert.Equal(Text, SecretRedactor.Redact(Text));
        Assert.False(SecretRedactor.ContainsSecret(Text));
    }
}
