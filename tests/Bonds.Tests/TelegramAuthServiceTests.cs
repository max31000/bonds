using System.Security.Cryptography;
using System.Text;
using Bonds.Core.Services;
using Bonds.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Bonds.Tests;

/// <summary>
/// Unit tests for TelegramAuthService.ValidateAuthData.
///
/// Telegram validation algorithm (from the official docs):
///   1. Build data_check_string: sorted key=value pairs (excluding hash) joined by \n
///   2. secret_key = SHA256(bot_token)
///   3. hash = HMAC-SHA256(data_check_string, secret_key)  →  hex, lowercase
///   4. auth_date must not be older than 86400 seconds
/// </summary>
public class TelegramAuthServiceTests
{
    private const string TestBotToken = "1234567890:ABCDEFghijklmnopqrstuvwxyz_test_token";

    // secret_key = SHA256(bot_token) — mirrors TelegramAuthService constructor
    private static readonly byte[] SecretKey =
        SHA256.HashData(Encoding.UTF8.GetBytes(TestBotToken));

    private static TelegramAuthService CreateService(string botToken = TestBotToken)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:BotToken"] = botToken,
            })
            .Build();

        return new TelegramAuthService(config);
    }

    /// <summary>
    /// Builds a valid hash for the given TelegramAuthData using the same algorithm
    /// as TelegramAuthService so we can construct correct test payloads.
    /// </summary>
    private static string ComputeHash(TelegramAuthData data)
    {
        var fields = new SortedDictionary<string, string>
        {
            ["id"] = data.Id.ToString(),
            ["auth_date"] = data.AuthDate.ToString(),
        };

        if (!string.IsNullOrEmpty(data.FirstName)) fields["first_name"] = data.FirstName;
        if (!string.IsNullOrEmpty(data.LastName)) fields["last_name"] = data.LastName;
        if (!string.IsNullOrEmpty(data.Username)) fields["username"] = data.Username;
        if (!string.IsNullOrEmpty(data.PhotoUrl)) fields["photo_url"] = data.PhotoUrl;

        var dataCheckString = string.Join("\n", fields.Select(kv => $"{kv.Key}={kv.Value}"));

        return Convert.ToHexString(
            HMACSHA256.HashData(SecretKey, Encoding.UTF8.GetBytes(dataCheckString))
        ).ToLowerInvariant();
    }

    private static long FreshAuthDate()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60; // 1 minute ago → valid

    private static long ExpiredAuthDate()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 86401; // 24h + 1 sec → expired

    // ─── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public void VerifyTelegramData_ValidSignature_ReturnsTrue()
    {
        var svc = CreateService();

        var data = new TelegramAuthData
        {
            Id = 123456789,
            FirstName = "Ivan",
            Username = "ivan_test",
            AuthDate = FreshAuthDate(),
            Hash = string.Empty,
        };

        var correctHash = ComputeHash(data);
        data = data with { Hash = correctHash };

        var result = svc.ValidateAuthData(data);

        Assert.True(result);
    }

    [Fact]
    public void VerifyTelegramData_InvalidSignature_ReturnsFalse()
    {
        var svc = CreateService();

        var data = new TelegramAuthData
        {
            Id = 123456789,
            FirstName = "Ivan",
            AuthDate = FreshAuthDate(),
            Hash = "0000000000000000000000000000000000000000000000000000000000000000",
        };

        var result = svc.ValidateAuthData(data);

        Assert.False(result);
    }

    [Fact]
    public void VerifyTelegramData_ExpiredAuthDate_ReturnsFalse()
    {
        var svc = CreateService();

        var data = new TelegramAuthData
        {
            Id = 987654321,
            AuthDate = ExpiredAuthDate(),
            Hash = string.Empty,
        };

        // Even with a correctly computed hash the service must reject old data
        var hash = ComputeHash(data);
        data = data with { Hash = hash };

        var result = svc.ValidateAuthData(data);

        Assert.False(result);
    }

    [Fact]
    public void VerifyTelegramData_FreshAuthDate_ReturnsTrue()
    {
        var svc = CreateService();

        var data = new TelegramAuthData
        {
            Id = 111222333,
            FirstName = "Petr",
            LastName = "Petrov",
            AuthDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), // exactly now
            Hash = string.Empty,
        };

        data = data with { Hash = ComputeHash(data) };

        var result = svc.ValidateAuthData(data);

        Assert.True(result);
    }

    [Fact]
    public void VerifyTelegramData_EmptyData_ReturnsFalse()
    {
        var svc = CreateService();

        var data = new TelegramAuthData
        {
            Id = 0,
            AuthDate = 0, // Jan 1 1970 → definitely older than 24 hours
            Hash = string.Empty,
        };

        var result = svc.ValidateAuthData(data);

        // expired auth_date alone must cause rejection
        Assert.False(result);
    }

    [Fact]
    public void VerifyTelegramData_TamperedField_ReturnsFalse()
    {
        // Подпись посчитана для одних данных, а пришли другие (например, изменили id
        // после подписи) — hash не совпадёт. Проверяет защиту от подмены payload-полей,
        // не покрытую отдельным сценарием в cashpulse-эталоне.
        var svc = CreateService();

        var original = new TelegramAuthData
        {
            Id = 555,
            Username = "original_user",
            AuthDate = FreshAuthDate(),
            Hash = string.Empty,
        };
        var hash = ComputeHash(original);

        var tampered = original with { Id = 999, Hash = hash };

        var result = svc.ValidateAuthData(tampered);

        Assert.False(result);
    }
}
