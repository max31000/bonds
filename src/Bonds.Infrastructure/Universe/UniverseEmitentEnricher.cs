using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Задача 43 часть A: суточное обогащение банка облигаций эмитентами. Берёт bulk-справочник ISS
/// (<see cref="IMoexIssClient.GetSecurityEmitentsAsync"/>, ~10 тыс. строк, включая выпуски вне банка),
/// мапит по SECID в строки <c>bond_universe</c> (<see cref="IBondUniverseRepository.UpdateEmitentsAsync"/>
/// — только UPDATE существующих строк). Бумага без совпадения в справочнике или без эмитента — поля
/// остаются null, не ошибка. Единиц измерения нет (ID/ИНН/название).
/// </summary>
public sealed class UniverseEmitentEnricher
{
    private readonly IMoexIssClient _moex;
    private readonly IBondUniverseRepository _universeRepo;
    private readonly ILogger<UniverseEmitentEnricher> _logger;

    public UniverseEmitentEnricher(IMoexIssClient moex, IBondUniverseRepository universeRepo, ILogger<UniverseEmitentEnricher> logger)
    {
        _moex = moex;
        _universeRepo = universeRepo;
        _logger = logger;
    }

    /// <summary>
    /// Выполняет обогащение. Возвращает число обновлённых строк банка либо <c>null</c>, если справочник
    /// ISS недоступен или собран не полностью (<see cref="MoexSecurityEmitentsResult.Complete"/> = false) —
    /// вызывающий код тогда НЕ считает шаг выполненным и повторит позже; частично собранные строки при
    /// этом всё равно применяются (идемпотентно, улучшает данные). Исключения БД пробрасываются
    /// (логирует и глотает вызывающий тик).
    /// </summary>
    public async Task<int?> EnrichAsync(CancellationToken ct = default)
    {
        var result = await _moex.GetSecurityEmitentsAsync(ct);
        if (result.Rows.Count == 0)
        {
            _logger.LogWarning("Universe emitent enrichment: MOEX bulk справочник пуст/недоступен — пропуск (банк не меняем)");
            return null;
        }

        var mapped = result.Rows
            .Select(e => new UniverseEmitent(e.Secid, e.EmitentId, e.EmitentInn, e.EmitentTitle))
            .ToList();

        var updated = await _universeRepo.UpdateEmitentsAsync(mapped, ct);
        if (!result.Complete)
        {
            _logger.LogWarning(
                "Universe emitent enrichment: справочник собран не полностью ({Total} выпусков, обновлено {Updated}) — шаг будет повторён",
                mapped.Count, updated);
            return null;
        }

        _logger.LogInformation(
            "Universe emitent enrichment: справочник {Total} выпусков, обновлено строк банка {Updated}", mapped.Count, updated);
        return updated;
    }
}
