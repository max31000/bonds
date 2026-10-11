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
    [InlineData(JwtSecretGuard.CommittedDevSecret)]
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
