using Bonds.Core.Telegram;

namespace Bonds.Infrastructure.Execution;

/// <summary>Уведомления исполнителя пользователю (реализация — Telegram-бот). НЕ бросает исключений: сбой уведомления не влияет на исполнение.</summary>
public interface IExecutionNotifier
{
    Task NotifyAsync(ulong userId, BotMessage message, CancellationToken ct = default);
}

/// <summary>Ничего не делает (тесты без Telegram).</summary>
public sealed class NullExecutionNotifier : IExecutionNotifier
{
    public Task NotifyAsync(ulong userId, BotMessage message, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Синк портфеля сразу после исполнения (без ожидания — цикл синка сам защищён от параллельного запуска).</summary>
public interface IPortfolioSyncTrigger
{
    void Request();
}

public sealed class NullPortfolioSyncTrigger : IPortfolioSyncTrigger
{
    public void Request() { }
}
