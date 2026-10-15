using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.GirBo;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Universe;

/// <summary>Итог прогона <see cref="IssuerFinancialsEnricher"/>. <see cref="Completed"/> = false — источник
/// ГИР БО признан недоступным (серия сбоев подряд), шаг НЕ засчитывается и будет повторён.</summary>
public sealed record IssuerFinancialsRunResult(int Queued, int Ok, int NoData, int Errors, bool Completed);

/// <summary>
/// Задача 45 часть B.4: суточное наполнение отчётности эмитентов из ГИР БО. За вызов обрабатывает до
/// <c>batchSize</c> ИНН эмитентов позиций портфеля и watchlist
/// (<see cref="IIssuerFinancialsRepository.GetRefreshQueueAsync"/>: без статуса → самые давно проверенные;
/// статус свежее 30 дней, 'error' — свежее ~суток не перепроверяются).
/// <para>
/// Цепочка по ИНН: поиск организации → список подач → детали последней подачи
/// (<see cref="GirBoClient"/>). Исход пишется в <c>issuer_financials_status</c>:
/// <c>not_found</c> (в реестре нет), <c>cb</c> (кредитная организация), <c>hidden</c> (подач/годовых
/// деталей нет), <c>ok</c> (+ до трёх годовых записей в <c>issuer_financials</c>, РУБЛИ).
/// </para>
/// <para>
/// <b>Устойчивость (паттерн <see cref="SecurityDetailsEnricher"/>, T-44fix).</b> Сбой по одному ИНН —
/// LogWarning и продолжение. Статус 'error' пишется только когда сбой был ИЗОЛИРОВАННЫМ (после него
/// был успешный запрос — источник жив), чтобы упавший сервис не метил эмитентов ошибкой впрок. Серия из
/// <see cref="MaxConsecutiveFailures"/> сбоев подряд = «источник недоступен»: тик прерывается, статусы этой
/// серии НЕ пишутся, <see cref="IssuerFinancialsRunResult.Completed"/> = false (шаг не засчитан).
/// Пауза <c>requestDelay</c> — перед КАЖДЫМ запросом к ГИР БО, кроме самого первого (бережём сервис).
/// </para>
/// </summary>
public sealed class IssuerFinancialsEnricher
{
    /// <summary>Сбоев подряд, после которых ГИР БО считается недоступным и тик прерывается.</summary>
    public const int MaxConsecutiveFailures = 5;

    private readonly IGirBoClient _girBo;
    private readonly IIssuerFinancialsRepository _repo;
    private readonly ILogger<IssuerFinancialsEnricher> _logger;

    public IssuerFinancialsEnricher(IGirBoClient girBo, IIssuerFinancialsRepository repo, ILogger<IssuerFinancialsEnricher> logger)
    {
        _girBo = girBo;
        _repo = repo;
        _logger = logger;
    }

    public async Task<IssuerFinancialsRunResult> EnrichBatchAsync(
        int batchSize, TimeSpan staleAfter, TimeSpan errorRetryAfter, TimeSpan requestDelay, DateTime utcNow,
        CancellationToken ct = default)
    {
        var queue = await _repo.GetRefreshQueueAsync(batchSize, utcNow - staleAfter, utcNow - errorRetryAfter, ct);
        if (queue.Count == 0) return new IssuerFinancialsRunResult(0, 0, 0, 0, Completed: true);

        var requested = 0;
        var ok = 0;
        var noData = 0;
        var errors = 0;
        var trailingFailures = new List<string>();

        async Task PauseAsync()
        {
            if (requested++ > 0 && requestDelay > TimeSpan.Zero)
            {
                await Task.Delay(requestDelay, ct);
            }
        }

        foreach (var inn in queue)
        {
            ct.ThrowIfCancellationRequested();

            (string Status, IReadOnlyList<IssuerFinancials> Rows) outcome;
            try
            {
                outcome = await CheckAsync(inn, utcNow, PauseAsync, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Issuer financials: сбой по ИНН {Inn} — пропуск", inn);
                trailingFailures.Add(inn);
                if (trailingFailures.Count >= MaxConsecutiveFailures)
                {
                    _logger.LogWarning(
                        "Issuer financials: {Count} сбоев подряд — ГИР БО считаем недоступным, тик прерван (шаг не засчитан)",
                        trailingFailures.Count);
                    return new IssuerFinancialsRunResult(queue.Count, ok, noData, errors, Completed: false);
                }

                continue;
            }

            // Успешный запрос доказывает, что источник жив: накопленные до него сбои — изолированные.
            errors += await FlushErrorsAsync(trailingFailures, utcNow, ct);

            if (outcome.Rows.Count > 0)
            {
                await _repo.UpsertAsync(outcome.Rows, ct);
            }

            await _repo.UpsertStatusAsync(new IssuerFinancialsStatus(inn, outcome.Status, utcNow), ct);
            if (outcome.Status == IssuerFinancialsStatuses.Ok) ok++; else noData++;
        }

        errors += await FlushErrorsAsync(trailingFailures, utcNow, ct);
        _logger.LogInformation(
            "Issuer financials: из очереди {Queue} — ok {Ok}, нет данных {NoData}, ошибки {Errors}", queue.Count, ok, noData, errors);
        return new IssuerFinancialsRunResult(queue.Count, ok, noData, errors, Completed: true);
    }

    private async Task<int> FlushErrorsAsync(List<string> failedInns, DateTime utcNow, CancellationToken ct)
    {
        var count = failedInns.Count;
        foreach (var inn in failedInns)
        {
            await _repo.UpsertStatusAsync(new IssuerFinancialsStatus(inn, IssuerFinancialsStatuses.Error, utcNow), ct);
        }

        failedInns.Clear();
        return count;
    }

    private async Task<(string Status, IReadOnlyList<IssuerFinancials> Rows)> CheckAsync(
        string inn, DateTime utcNow, Func<Task> pause, CancellationToken ct)
    {
        await pause();
        var org = await _girBo.FindOrganizationAsync(inn, ct);
        if (org is null) return (IssuerFinancialsStatuses.NotFound, []);
        if (org.IsCb == true) return (IssuerFinancialsStatuses.Cb, []);

        await pause();
        var filings = await _girBo.GetFilingsAsync(org.Id, ct);
        if (filings.Count == 0) return (IssuerFinancialsStatuses.Hidden, []);

        var latest = filings.OrderByDescending(f => f.Period).First();
        if (latest.IsCb) return (IssuerFinancialsStatuses.Cb, []);

        await pause();
        var rows = await _girBo.GetAnnualFinancialsAsync(latest.Id, inn, latest.Period, utcNow, ct);
        return rows.Count == 0 ? (IssuerFinancialsStatuses.Hidden, []) : (IssuerFinancialsStatuses.Ok, rows);
    }
}
