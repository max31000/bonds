using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>
/// Generates JWT tokens for integration tests, matching the format validated by AddJwtBearer
/// in Program.cs. TestWebApplicationFactory overrides Jwt:Secret/Issuer/Audience to these values.
/// </summary>
public static class JwtTestHelper
{
    public const string TestSecret = "bonds-integration-test-secret-key-32chars!!"; // >= 32 chars for HMAC-SHA256
    public const string TestIssuer = "bonds";
    public const string TestAudience = "bonds";

    public static string GenerateToken(ulong userId, TimeSpan? expiry = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            expires: DateTime.UtcNow.Add(expiry ?? TimeSpan.FromHours(1)),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
