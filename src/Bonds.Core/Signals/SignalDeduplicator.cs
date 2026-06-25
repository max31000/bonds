using Bonds.Core.Models;

namespace Bonds.Core.Signals;

/// <summary>
/// Дедупликация кандидатов сигналов против уже существующих непрочитанных (plan/07 Часть A
/// "Дедупликация"). Архитектурное решение (самостоятельное, документируется здесь, не "ТРЕБУЕТ
/// СОГЛАСОВАНИЯ"): движок генерирует ВСЕ кандидаты на каждый прогон (без знания, что уже есть в
/// БД), а эта чистая функция в Core сравнивает список кандидатов со списком существующих
/// непрочитанных сигналов и убирает дубли. Альтернатива — добавить
/// <c>ISignalRepository.ExistsActiveAsync(...)</c> и дедуплицировать через I/O — отклонена, т.к.
/// (а) Signals Engine должен оставаться чистым (без I/O) согласно заданию, и (б) объём сигналов на
/// одного пользователя (single-user продукт, единицы-десятки позиций) мал, поэтому
/// "загрузить все непрочитанные и сравнить в памяти" не создаёт проблем производительности —
/// тот же принцип, что уже применён в Infrastructure (например, BondSyncService полностью
/// держит позиции в памяти на один синк).
/// <para>
/// <b>Смысловой ключ дубликата</b> — Type + PositionId + InstrumentId + Date. Совпадение по всем
/// четырём полям означает "тот же самый факт о том же событии на ту же дату" — например,
/// "приближается купон ПозицииId=5 датированный 2026-07-01" не должен дублироваться, но другой
/// купон той же позиции с другой датой — это другое событие и должен завестись новый сигнал
/// (см. тест-кейсы). IsRead уже отфильтрован на уровне входа (только непрочитанные передаются) —
/// прочитанный сигнал не блокирует создание нового кандидата с тем же ключом (пользователь явно
/// отметил его прочитанным, значит может появиться новый по тому же факту, если правило снова
/// его произведёт, что для большинства правил тут не бывает, т.к. ключ включает конкретную дату,
/// но это не a-priori исключается, например, после ручного "сбрасывания" истории).
/// </para>
/// </summary>
public static class SignalDeduplicator
{
    /// <summary>Возвращает только те кандидаты, для которых нет совпадения по смысловому ключу среди уже существующих непрочитанных сигналов.</summary>
    public static IReadOnlyList<Signal> FilterNew(
        IReadOnlyList<Signal> candidates,
        IReadOnlyList<Signal> existingUnreadSignals)
    {
        if (candidates.Count == 0) return candidates;
        if (existingUnreadSignals.Count == 0) return candidates;

        var existingKeys = existingUnreadSignals
            .Select(Key)
            .ToHashSet();

        return candidates
            .Where(c => !existingKeys.Contains(Key(c)))
            .ToList();
    }

    private static (SignalType Type, ulong? PositionId, ulong? InstrumentId, DateOnly Date) Key(Signal signal) =>
        (signal.Type, signal.PositionId, signal.InstrumentId, signal.Date);
}
