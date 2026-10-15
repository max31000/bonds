using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Задача 43 часть B: фоновое наполнение кэша карточек бумаг (<c>security_details</c>). За один вызов
/// обрабатывает до <c>batchSize</c> бумаг из очереди
/// <see cref="ISecurityDetailsRepository.GetEnrichmentQueueAsync"/> (портфель/watchlist → банк без
/// карточки → карточки старше порога). Различаем два вида неудач (ревью В1, MINOR-1):
/// транспортная ошибка клиента (исключение) = «источник недоступен» — пропуск без записи, серия подряд
/// прерывает тик; null-карточка (404/пустая/битая) = «карточки нет» — пишется негативный кэш (строка со
/// всеми null-флагами и свежим fetched_at), чтобы мёртвый SECID ушёл из головы очереди и не блокировал
/// остальных. Единицы: см. <see cref="SecurityDetails"/> (спред купона — ДОЛЯ).
/// </summary>
public sealed class SecurityDetailsEnricher
{
    /// <summary>Сколько карточек накапливаем до записи в БД — прогресс не теряется при отмене/падении тика.</summary>
    private const int FlushSize = 50;

    /// <summary>Подряд идущих неудач, после которых считаем источник недоступным и прерываем тик
    /// (вместо сотни заведомо провальных запросов).</summary>
    public const int MaxConsecutiveFailures = 10;

    private readonly IMoexIssClient _moex;
    private readonly ISecurityDetailsRepository _repo;
    private readonly ILogger<SecurityDetailsEnricher> _logger;

    public SecurityDetailsEnricher(IMoexIssClient moex, ISecurityDetailsRepository repo, ILogger<SecurityDetailsEnricher> logger)
    {
        _moex = moex;
        _repo = repo;
        _logger = logger;
    }

    /// <summary>Возвращает число успешно записанных карточек.</summary>
    public async Task<int> EnrichBatchAsync(
        int batchSize, TimeSpan staleAfter, TimeSpan requestDelay, DateTime utcNow, CancellationToken ct = default)
    {
        var queue = await _repo.GetEnrichmentQueueAsync(batchSize, utcNow - staleAfter, ct);
        if (queue.Count == 0) return 0;

        var pending = new List<SecurityDetails>(FlushSize);
        var saved = 0;
        var consecutiveFailures = 0;

        foreach (var secid in queue)
        {
            ct.ThrowIfCancellationRequested();

            MoexSecurityDescription? card = null;
            var sourceFailed = false;
            try
            {
                card = await _moex.GetSecurityDescriptionAsync(secid, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                sourceFailed = true;
                _logger.LogWarning(ex, "Security details: источник недоступен для {Secid} — пропуск без записи", secid);
            }

            if (sourceFailed)
            {
                if (++consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _logger.LogWarning(
                        "Security details: {Count} неудач подряд — источник считаем недоступным, тик прерван", consecutiveFailures);
                    break;
                }
            }
            else
            {
                consecutiveFailures = 0;
                if (card is null)
                {
                    // Негативный кэш: карточки нет (404/пустая/битая) — пишем строку со всеми null-флагами,
                    // чтобы SECID ушёл из бакета «банк без карточки» и вернулся в очередь только протухшим.
                    _logger.LogInformation("Security details: карточки {Secid} нет — негативный кэш", secid);
                }

                pending.Add(new SecurityDetails
                {
                    Secid = secid,
                    Isin = card?.Isin,
                    EmitentId = card?.EmitterId,
                    HasDefault = card?.HasDefault,
                    HasTechnicalDefault = card?.HasTechnicalDefault,
                    IsQualifiedInvestors = card?.IsQualifiedInvestors,
                    CouponBenchmark = card?.CouponBenchmark,
                    CouponBenchmarkSpreadFraction = card?.CouponBenchmarkSpreadFraction,
                    EarlyRepayment = card?.EarlyRepayment,
                    FetchedAt = utcNow,
                });

                if (pending.Count >= FlushSize)
                {
                    saved += await FlushAsync(pending, ct);
                }
            }

            if (requestDelay > TimeSpan.Zero)
            {
                await Task.Delay(requestDelay, ct);
            }
        }

        saved += await FlushAsync(pending, ct);
        _logger.LogInformation("Security details: из очереди {Queue} записано карточек {Saved}", queue.Count, saved);
        return saved;
    }

    private async Task<int> FlushAsync(List<SecurityDetails> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return 0;
        var count = pending.Count;
        await _repo.UpsertBatchAsync(pending.ToList(), ct);
        pending.Clear();
        return count;
    }
}
