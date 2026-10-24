using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bonds.Core.Telegram;

/// <summary>Действие над планом из кнопки Telegram.</summary>
public enum BotPlanAction
{
    Approve,
    Reject,

    /// <summary>Снять висящие заявки исполняемого плана (задача 65, кнопка «Отменить»).</summary>
    CancelExecution,
}

/// <summary>Разобранный <c>callback_data</c> кнопки плана.</summary>
public sealed record BotCallbackData(ulong PlanId, BotPlanAction Action, string Nonce);

/// <summary>
/// Формат <c>callback_data</c> кнопок плана (задача 64): <c>p:{planId}:{a|r|c}:{nonce}</c> (c — снять заявки исполняемого плана, задача 65), nonce — случайные 8 байт в base64url (11 символов, хранится в
/// <c>trade_plans.bot_nonce</c>). Лимит Telegram на callback_data — 64 байта: максимум около 2 + 20 + 2 + 11 = 35. Чистые функции, без БД и сети.
/// </summary>
public static partial class BotCallback
{
    public const string Prefix = "p";
    private const string ApproveCode = "a";
    private const string RejectCode = "r";
    private const string CancelCode = "c";

    [GeneratedRegex("^[A-Za-z0-9_-]{8,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex NonceRegex();

    /// <summary>Новый случайный nonce: 8 криптослучайных байт, base64url без заполнения.</summary>
    public static string NewNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(8)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Build(ulong planId, BotPlanAction action, string nonce) =>
        $"{Prefix}:{planId}:{CodeOf(action)}:{nonce}";

    private static string CodeOf(BotPlanAction action) => action switch
    {
        BotPlanAction.Approve => ApproveCode,
        BotPlanAction.Reject => RejectCode,
        _ => CancelCode,
    };

    /// <summary>Разбор; любой мусор/чужой формат — false.</summary>
    public static bool TryParse(string? data, out BotCallbackData? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(data) || data.Length > 64) return false;
        var parts = data.Split(':');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return false;
        BotPlanAction action;
        if (parts[2] == ApproveCode) action = BotPlanAction.Approve;
        else if (parts[2] == RejectCode) action = BotPlanAction.Reject;
        else if (parts[2] == CancelCode) action = BotPlanAction.CancelExecution;
        else return false;
        if (!NonceRegex().IsMatch(parts[3])) return false;
        parsed = new BotCallbackData(id, action, parts[3]);
        return true;
    }

    /// <summary>Сравнение nonce за постоянное время (не даёт подобрать по таймингу).</summary>
    public static bool NonceEquals(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }
}
