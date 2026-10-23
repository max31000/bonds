using System.Globalization;
using System.Text.RegularExpressions;
using Bonds.Core.Capital;

namespace Bonds.Core.Telegram;

public enum BotCommandKind
{
    /// <summary>Не команда (обычный текст) — бот отвечает подсказкой.</summary>
    NotCommand,

    /// <summary>Команда распознана, но аргументы неверны (<see cref="BotCommand.Error"/> — подсказка).</summary>
    Invalid,
    Help,
    Status,
    Plans,
    Inflow,
    Withdraw,
    Exit,
    Stop,
    Resume,
}

/// <summary>
/// Разобранная команда. <b>Единицы:</b> <see cref="Amount"/> — сумма в валюте портфеля <see cref="Portfolio"/> (rub — рубли, usd — доллары);
/// <see cref="Fraction"/> — ДОЛЯ (0.15 = «15%» из сообщения; конверсия ÷100 ровно один раз, в парсере).
/// </summary>
public sealed record BotCommand(
    BotCommandKind Kind,
    decimal? Amount = null,
    decimal? Fraction = null,
    string? Portfolio = null,
    string? Code = null,
    string? Error = null);

/// <summary>
/// Парсер команд Telegram-бота (задача 64) — чистая функция. Поддерживает <c>/cmd@BotName</c>, запятую как десятичный разделитель, суффиксы валюты
/// (<c>rub/руб/₽</c>, <c>usd/$/долл</c>) и «%» для вывода долей. Любая неоднозначность (проценты в /inflow, две суммы, незнакомая валюта) — <see cref="BotCommandKind.Invalid"/>
/// с понятной подсказкой, а не угадывание: сумма и валюта определяют будущий план.
/// </summary>
public static partial class BotCommandParser
{
    public const string InflowUsage = "/inflow 50000 или /inflow 500 usd";
    public const string WithdrawUsage = "/withdraw 15% · /withdraw 50000 · /withdraw 200 usd";
    public const string ExitUsage = "/exit temporary (код корзины без цели)";

    [GeneratedRegex(@"^(?<num>\d+(?:[.,]\d+)?)(?<suffix>%|[\p{L}$₽]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ValueRegex();

    [GeneratedRegex(@"^[a-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    public static BotCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new BotCommand(BotCommandKind.NotCommand);
        var tokens = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var head = tokens[0];
        if (!head.StartsWith('/')) return new BotCommand(BotCommandKind.NotCommand);

        var name = head[1..];
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        name = name.ToLowerInvariant();
        var args = tokens.Skip(1).ToArray();

        return name switch
        {
            "start" or "help" => new BotCommand(BotCommandKind.Help),
            "status" => new BotCommand(BotCommandKind.Status),
            "plans" => new BotCommand(BotCommandKind.Plans),
            "stop" => new BotCommand(BotCommandKind.Stop),
            "resume" => new BotCommand(BotCommandKind.Resume),
            "inflow" => ParseMoney(args, allowFraction: false, InflowUsage, BotCommandKind.Inflow),
            "withdraw" => ParseMoney(args, allowFraction: true, WithdrawUsage, BotCommandKind.Withdraw),
            "exit" => ParseExit(args),
            _ => Invalid($"Не знаю команду «{BotFormat.Echo(head)}». Список команд — /help."),
        };
    }

    private static BotCommand Invalid(string error) => new(BotCommandKind.Invalid, Error: error);

    private static BotCommand ParseExit(string[] args)
    {
        if (args.Length != 1 || !CodeRegex().IsMatch(args[0].ToLowerInvariant())) return Invalid($"Укажите код корзины без цели. Пример: {ExitUsage}");
        return new BotCommand(BotCommandKind.Exit, Code: args[0].ToLowerInvariant());
    }

    private static BotCommand ParseMoney(string[] args, bool allowFraction, string usage, BotCommandKind kind)
    {
        if (args.Length is 0 or > 2) return Invalid($"Нужна сумма. Примеры: {usage}");

        var m = ValueRegex().Match(args[0]);
        if (!m.Success) return Invalid($"Не понял сумму «{BotFormat.Echo(args[0])}». Примеры: {usage}");

        if (!decimal.TryParse(m.Groups["num"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value <= 0m || value > 1_000_000_000_000m)
            return Invalid($"Сумма должна быть положительным числом. Примеры: {usage}");

        var suffix = m.Groups["suffix"].Value;
        var isPercent = suffix == "%";
        var currencyToken = isPercent ? string.Empty : suffix;

        if (args.Length == 2)
        {
            if (currencyToken.Length > 0) return Invalid($"Валюта указана дважды. Примеры: {usage}");
            currencyToken = args[1];
        }

        string? portfolio = null;
        if (currencyToken.Length > 0)
        {
            portfolio = PortfolioOf(currencyToken);
            if (portfolio is null) return Invalid($"Не знаю валюту «{BotFormat.Echo(currencyToken)}»: поддерживаются rub и usd. Примеры: {usage}");
        }

        if (isPercent)
        {
            if (!allowFraction) return Invalid($"Проценты здесь не поддерживаются — укажите сумму. Пример: {usage}");
            // «15%» → ДОЛЯ 0.15 (из процентов в доли — единственная конверсия в бот-слое). 100% = вывести всё.
            if (value > 100m) return Invalid($"Доля не может быть больше 100%. Примеры: {usage}");
            return new BotCommand(kind, Fraction: value / 100m, Portfolio: portfolio ?? CapitalPortfolios.Rub);
        }

        // Пол суммы — 1 единица валюты: «/inflow 0.01» не должен порождать план-пустышку.
        if (value < 1m) return Invalid($"Сумма должна быть не меньше 1. Примеры: {usage}");
        return new BotCommand(kind, Amount: value, Portfolio: portfolio ?? CapitalPortfolios.Rub);
    }

    private static string? PortfolioOf(string token) => token.Trim().ToLowerInvariant() switch
    {
        "rub" or "rur" or "руб" or "рубл" or "рублей" or "₽" or "р" => CapitalPortfolios.Rub,
        "usd" or "$" or "долл" or "доллар" or "долларов" => CapitalPortfolios.Usd,
        _ => null,
    };
}
