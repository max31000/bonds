using Bonds.Infrastructure.Services;
using Xunit;

namespace Bonds.Tests;

public class JwtSecretGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short-secret")]
    [InlineData("dev-secret-change-in-production-min-32-chars!!")]
    [InlineData("dev-secret-change-me-min-32-characters!!")]
    [InlineData("dev-only-secret-key-do-not-use-in-prod-32chars")]
    [InlineData("prod-but-still-a-DEV-SECRET-0123456789abcdef")]
    public void EnsureProductionSafe_RejectsUnsafeSecret(string? secret)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JwtSecretGuard.EnsureProductionSafe(secret));
        Assert.StartsWith("Jwt:Secret must be configured", ex.Message);
    }

    [Fact]
    public void EnsureProductionSafe_AcceptsStrongSecret()
    {
        JwtSecretGuard.EnsureProductionSafe(new string('x', 48));
    }
}
