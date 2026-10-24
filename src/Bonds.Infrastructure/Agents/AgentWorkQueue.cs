using System.Collections.Concurrent;
using System.Threading.Channels;
using Bonds.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Agents;

/// <summary>Задание в очереди агента. <see cref="Question"/> — вопрос <c>/ask</c> или описание новых событий (недоверенный текст).</summary>
public sealed record AgentWorkItem(ulong UserId, AgentTaskKind Kind, string? Question = null);

/// <summary>Сигнал о новых Critical-событиях пользователя (вызывается шагом событий синка; не блокирует цикл).</summary>
public interface IAgentEventTrigger
{
    void CriticalSignalsDetected(ulong userId, IReadOnlyList<string> subjects);
}

/// <summary>
/// Очередь запусков агента (задача 68, singleton): ограниченный канал, один потребитель (<see cref="AgentWorkerHostedService"/>) — запуски одного процесса
/// идут по одному (лимиты в сутки считаются без гонок). Справедливость между пользователями (ревью MINOR 8): у пользователя не больше ОДНОГО ждущего задания
/// каждого вида, включая <c>/ask</c> (второй вопрос, пока первый в очереди, отклоняется); новые Critical-события, пока разбор ещё в очереди, ДОПИСЫВАЮТСЯ
/// в него (не теряются и не плодят запуски). Переполнение — задание отбрасывается (<see cref="TryEnqueue"/> = false). Ворота — при запуске (<see cref="AgentService.RunAsync"/>).
/// </summary>
public sealed class AgentWorkQueue : IAgentEventTrigger
{
    private const int Capacity = 32;

    /// <summary>Сколько событий держится в одном разборе (больше — отбрасываются самые новые).</summary>
    public const int MaxEventSubjects = 10;

    private readonly Channel<AgentWorkItem> _channel = Channel.CreateBounded<AgentWorkItem>(new BoundedChannelOptions(Capacity)
    {
        // Wait (а не DropWrite): при DropWrite TryWrite ВСЕГДА возвращает true и молча выбрасывает задание — переполнение не было бы видно, а отметка
        // «ждёт» выброшенного задания оставалась навсегда (вид задачи пользователя блокировался до рестарта). С Wait неблокирующий TryWrite возвращает false.
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private readonly ConcurrentDictionary<(ulong, AgentTaskKind), byte> _pending = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _lastAsk = new();
    private readonly Dictionary<ulong, List<string>> _eventSubjects = new();
    private readonly object _eventLock = new();
    private readonly TimeProvider _time;
    private readonly IOptionsMonitor<AgentOptions> _options;

    public AgentWorkQueue(TimeProvider time, IOptionsMonitor<AgentOptions> options)
    {
        _time = time;
        _options = options;
    }

    public ChannelReader<AgentWorkItem> Reader => _channel.Reader;

    /// <summary>Ждёт ли (или идёт) задание этого вида у пользователя. Разбор событий перестаёт «ждать», как только взят в работу (<see cref="Begin"/>).</summary>
    public bool IsPending(ulong userId, AgentTaskKind kind) => _pending.ContainsKey((userId, kind));

    /// <summary>В очередь: не более одного ждущего задания каждого вида на пользователя (иначе false), переполнение канала — false.</summary>
    public bool TryEnqueue(AgentWorkItem item)
    {
        if (!_pending.TryAdd((item.UserId, item.Kind), 0)) return false;
        if (_channel.Writer.TryWrite(item)) return true;
        _pending.TryRemove((item.UserId, item.Kind), out _);
        return false;
    }

    /// <summary>
    /// Потребитель берёт задание в работу. Для разбора событий — собирает накопленные события в описание и снимает отметку «ждёт»: события, пришедшие во
    /// время запуска, встанут следующим разбором (их ограничивает <see cref="AgentOptions.EventReviewMaxPerDay"/>).
    /// </summary>
    public AgentWorkItem Begin(AgentWorkItem item)
    {
        if (item.Kind != AgentTaskKind.EventReview) return item;
        lock (_eventLock)
        {
            _eventSubjects.Remove(item.UserId, out var subjects);
            _pending.TryRemove((item.UserId, item.Kind), out _);
            var details = subjects is { Count: > 0 } ? string.Join("\n", subjects.Select(x => "• " + x)) : item.Question;
            return item with { Question = details };
        }
    }

    /// <summary>Задание обработано (успешно или нет) — снимает отметку «ждёт» (разбор событий снял её уже в <see cref="Begin"/>).</summary>
    public void Completed(AgentWorkItem item)
    {
        if (item.Kind != AgentTaskKind.EventReview) _pending.TryRemove((item.UserId, item.Kind), out _);
    }

    /// <summary>
    /// Пауза <c>/ask</c>: true — можно (отметка времени <paramref name="markedAt"/> ставится сразу), false — с прошлого вопроса прошло меньше
    /// <see cref="AgentOptions.AskCooldown"/>. Если вопрос в итоге не принят очередью — <see cref="ReleaseAsk"/> (пауза не «сгорает»).
    /// </summary>
    public bool TryMarkAsk(ulong userId, out DateTime markedAt)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        markedAt = now;
        var cooldown = _options.CurrentValue.AskCooldown;
        while (true)
        {
            if (_lastAsk.TryGetValue(userId, out var last))
            {
                if (now - last < cooldown) return false;
                if (_lastAsk.TryUpdate(userId, now, last)) return true;
            }
            else if (_lastAsk.TryAdd(userId, now))
            {
                return true;
            }
        }
    }

    /// <summary>Снимает отметку <see cref="TryMarkAsk"/>, если она всё ещё та же (вопрос не принят очередью).</summary>
    public void ReleaseAsk(ulong userId, DateTime markedAt) => _lastAsk.TryRemove(new KeyValuePair<ulong, DateTime>(userId, markedAt));

    public void CriticalSignalsDetected(ulong userId, IReadOnlyList<string> subjects)
    {
        lock (_eventLock)
        {
            if (!_eventSubjects.TryGetValue(userId, out var list)) _eventSubjects[userId] = list = [];
            foreach (var subject in subjects)
            {
                if (list.Count >= MaxEventSubjects) break;
                var trimmed = subject.Length > 300 ? subject[..300] : subject;
                if (!list.Contains(trimmed, StringComparer.Ordinal)) list.Add(trimmed);
            }

            // Разбор уже ждёт — события дописаны в него; иначе новое задание (описание соберёт Begin). Под той же блокировкой, что и Begin:
            // событие либо попадёт в уже взятый разбор, либо поставит новый — не потеряется и не даст пустого разбора.
            TryEnqueue(new AgentWorkItem(userId, AgentTaskKind.EventReview));
        }
    }

    /// <summary>Накопленные события пользователя для следующего разбора (для тестов).</summary>
    internal IReadOnlyList<string> PendingEventSubjects(ulong userId)
    {
        lock (_eventLock) return _eventSubjects.TryGetValue(userId, out var list) ? list.ToList() : [];
    }
}

/// <summary>Потребитель очереди агента: каждое задание — в своём DI-скоупе, сбой — только лог (процесс не падает).</summary>
public sealed class AgentWorkerHostedService : BackgroundService
{
    private readonly AgentWorkQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentWorkerHostedService> _logger;

    public AgentWorkerHostedService(AgentWorkQueue queue, IServiceScopeFactory scopeFactory, ILogger<AgentWorkerHostedService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var work = _queue.Begin(item);
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AgentService>().RunAsync(work.UserId, work.Kind, work.Question, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Agent work item {Kind} for user {UserId} failed: {ErrorType}", item.Kind, item.UserId, ex.GetType().Name);
            }
            finally
            {
                _queue.Completed(item);
            }
        }
    }
}

/// <summary>
/// Расписание агента (задача 68): раз в минуту — дайджест по будням в 19:00 МСК и обзор 1-го числа, только пользователям с <c>agent_enabled</c>, подключением
/// <c>claude_code</c> и не исчерпанным лимитом; не чаще раза в сутки на вид (по журналу <c>agent_runs</c>).
/// </summary>
public sealed class AgentScheduler
{
    private static readonly AgentTaskKind[] ScheduledKinds = [AgentTaskKind.DailyDigest, AgentTaskKind.MonthlyReview];

    private readonly Bonds.Core.Interfaces.Repositories.IAgentSettingsRepository _settings;
    private readonly Bonds.Core.Interfaces.Repositories.IAgentRunRepository _runs;
    private readonly AgentService _service;
    private readonly IOptionsMonitor<AgentOptions> _options;

    public AgentScheduler(
        Bonds.Core.Interfaces.Repositories.IAgentSettingsRepository settings,
        Bonds.Core.Interfaces.Repositories.IAgentRunRepository runs,
        AgentService service,
        IOptionsMonitor<AgentOptions> options)
    {
        _settings = settings;
        _runs = runs;
        _service = service;
        _options = options;
    }

    /// <summary>Задания, которым пора сейчас (ворота уже проверены; повторная проверка — при запуске).</summary>
    public async Task<IReadOnlyList<AgentWorkItem>> CollectDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled) return [];
        var kinds = ScheduledKinds.Where(k => AgentRunGate.IsScheduledDue(k, nowUtc, options)).ToList();
        if (kinds.Count == 0) return [];

        var dayStart = AgentRunGate.MoscowDayStartUtc(nowUtc);
        var due = new List<AgentWorkItem>();
        foreach (var userId in await _settings.ListEnabledUserIdsAsync())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var kind in kinds)
            {
                if (await _runs.CountStartedSinceAsync(userId, dayStart, kind) > 0) continue;
                if (!(await _service.EvaluateGateAsync(userId, kind)).Allowed) continue;
                due.Add(new AgentWorkItem(userId, kind));
            }
        }

        return due;
    }
}

/// <summary>Тик расписания агента (раз в минуту). Сбой тика — <c>LogWarning</c> и следующая итерация.</summary>
public sealed class AgentSchedulerHostedService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private readonly AgentWorkQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentSchedulerHostedService> _logger;

    public AgentSchedulerHostedService(AgentWorkQueue queue, IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<AgentSchedulerHostedService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var due = await scope.ServiceProvider.GetRequiredService<AgentScheduler>().CollectDueAsync(_time.GetUtcNow().UtcDateTime, stoppingToken);
                foreach (var item in due) _queue.TryEnqueue(item);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Agent scheduler tick failed: {ErrorType}: {Message}", ex.GetType().Name, ex.Message);
            }
        }
    }
}
