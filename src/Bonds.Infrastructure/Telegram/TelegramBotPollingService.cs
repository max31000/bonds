using Bonds.Core.Interfaces.Repositories;
using Bonds.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Telegram;

/// <summary>
/// Long polling Telegram-бота (<c>getUpdates</c>, timeout 25 с; задача 64). Webhook не используется. Паттерн устойчивости как у остальных фоновых сервисов: ошибка итерации —
/// <c>LogWarning</c> и пауза <see cref="TelegramBotOptions.ErrorDelay"/>, процесс никогда не падает. Offset (id последнего обработанного апдейта + 1) хранится в
/// <c>bot_state</c> и сдвигается после КАЖДОГО апдейта — после рестарта обработанное не повторяется, а «ядовитый» апдейт не зацикливает бота.
/// <para>
/// <b>Конфликт (HTTP 409).</b> Другой потребитель <c>getUpdates</c> того же бота или установленный webhook: бот отступает на <see cref="TelegramBotOptions.ConflictBackoff"/>
/// и шлёт ОДИН (не чаще <see cref="TelegramBotOptions.ConflictAlertCooldown"/>) алерт владельцу через <see cref="ITelegramAlertSender"/>. Токен пустой (dev/тесты) — сервис простаивает.
/// Токен бота и url запроса в логи не попадают.
/// </para>
/// </summary>
public sealed class TelegramBotPollingService : BackgroundService
{
    public const string OffsetKey = "telegram_update_offset";

    internal const string ConflictAlertText =
        "⚠ Telegram-бот bonds: getUpdates вернул 409 Conflict — этот же бот сейчас читает другой потребитель (второй процесс или установленный webhook). " +
        "Команды и кнопки подтверждения в bonds не работают, пока конфликт не устранён. Повторные проверки идут раз в минуту.";

    private readonly IServiceScopeFactory _scopes;
    private readonly ITelegramBotClient _bot;
    private readonly ITelegramAlertSender _alerts;
    private readonly TelegramBotOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TelegramBotPollingService> _logger;

    private long? _offset;
    private DateTime? _lastConflictAlertUtc;

    public TelegramBotPollingService(
        IServiceScopeFactory scopes,
        ITelegramBotClient bot,
        ITelegramAlertSender alerts,
        IOptions<TelegramBotOptions> options,
        TimeProvider time,
        ILogger<TelegramBotPollingService> logger)
    {
        _scopes = scopes;
        _bot = bot;
        _alerts = alerts;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Не блокируем запуск хоста: первая итерация (БД, сеть) идёт уже вне StartAsync.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Сообщение исключений бота очищено от url/токена; полный объект не логируем (стек сетевого слоя может нести адрес).
                _logger.LogWarning("Telegram bot polling iteration failed: {ErrorType}: {Message}", ex.GetType().Name, ex.Message);
                delay = _options.ErrorDelay;
            }

            if (delay <= TimeSpan.Zero) continue;
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Одна итерация опроса. Возвращает паузу перед следующей (0 — сразу, long polling уже подождал). Исключение = сбой итерации (вызывающий делает паузу).</summary>
    public async Task<TimeSpan> PollOnceAsync(CancellationToken ct)
    {
        if (!_bot.IsConfigured) return _options.IdleDelay;

        var offset = await LoadOffsetAsync();
        var result = await _bot.GetUpdatesAsync(offset, _options.PollTimeoutSeconds, ct);

        if (result.Conflict)
        {
            _logger.LogWarning("Telegram getUpdates returned 409 Conflict: another consumer reads this bot or a webhook is set; backing off {Seconds} s", (int)_options.ConflictBackoff.TotalSeconds);
            await AlertConflictOnceAsync(ct);
            return _options.ConflictBackoff;
        }

        foreach (var update in result.Updates.OrderBy(u => u.UpdateId))
        {
            ct.ThrowIfCancellationRequested();
            using (var scope = _scopes.CreateScope())
            {
                // HandleAsync сам ловит сбои обработки апдейта (кроме отмены): ядовитый апдейт не должен зациклить бота.
                await scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>().HandleAsync(update, ct);
            }

            _offset = update.UpdateId + 1;
            await SaveOffsetAsync(_offset.Value);
        }

        return TimeSpan.Zero;
    }

    private async Task AlertConflictOnceAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_lastConflictAlertUtc is { } last && now - last < _options.ConflictAlertCooldown) return;

        _lastConflictAlertUtc = now;
        await _alerts.SendAsync(ConflictAlertText, ct);
    }

    private async Task<long> LoadOffsetAsync()
    {
        if (_offset is { } cached) return cached;

        using var scope = _scopes.CreateScope();
        var raw = await scope.ServiceProvider.GetRequiredService<IBotStateRepository>().GetAsync(OffsetKey);
        _offset = long.TryParse(raw, out var parsed) ? parsed : 0;
        return _offset.Value;
    }

    private async Task SaveOffsetAsync(long offset)
    {
        using var scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IBotStateRepository>().SetAsync(OffsetKey, offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
