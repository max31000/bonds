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
/// идут по одному (лимиты в сутки считаются без гонок). Плановые и событийные задания дедуплицируются по (пользователь, вид), пока не завершатся.
/// Переполнение — задание отбрасывается (<see cref="TryEnqueue"/> = false). Ворота проверяются при запуске (<see cref="AgentService.RunAsync"/>).
/// </summary>
public sealed class AgentWorkQueue : IAgentEventTrigger
{
    private const int Capacity = 32;
    private readonly Channel<AgentWorkItem> _channel = Channel.CreateBounded<AgentWorkItem>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private readonly ConcurrentDictionary<(ulong, AgentTaskKind), byte> _pending = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _lastAsk = new();
    private readonly TimeProvider _time;
    private readonly IOptionsMonitor<AgentOptions> _options;

    public AgentWorkQueue(TimeProvider time, IOptionsMonitor<AgentOptions> options)
    {
        _time = time;
        _options = options;
    }

    public ChannelReader<AgentWorkItem> Reader => _channel.Reader;

    public bool IsPending(ulong userId, AgentTaskKind kind) => _pending.ContainsKey((userId, kind));

    /// <summary>В очередь. Неинтерактивные виды — не более одного ждущего/идущего на пользователя.</summary>
    public bool TryEnqueue(AgentWorkItem item)
    {
        var dedup = item.Kind != AgentTaskKind.OnDemand;
        if (dedup && !_pending.TryAdd((item.UserId, item.Kind), 0)) return false;
        if (_channel.Writer.TryWrite(item)) return true;
        if (dedup) _pending.TryRemove((item.UserId, item.Kind), out _);
        return false;
    }

    /// <summary>Задание обработано (успешно или нет) — снимает отметку дедупликации.</summary>
    public void Completed(AgentWorkItem item) => _pending.TryRemove((item.UserId, item.Kind), out _);

    /// <summary>
    /// Пауза <c>/ask</c>: true — можно (отметка времени ставится сразу), false — с прошлого вопроса прошло меньше <see cref="AgentOptions.AskCooldown"/>.
    /// </summary>
    public bool TryMarkAsk(ulong userId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
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

    public void CriticalSignalsDetected(ulong userId, IReadOnlyList<string> subjects)
    {
        var details = string.Join("\n", subjects.Take(10).Select(s => "• " + (s.Length > 300 ? s[..300] : s)));
        TryEnqueue(new AgentWorkItem(userId, AgentTaskKind.EventReview, details));
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
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AgentService>().RunAsync(item.UserId, item.Kind, item.Question, stoppingToken);
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
