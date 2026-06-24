using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface ICouponScheduleRepository
{
    Task<IEnumerable<CouponSchedule>> GetByInstrumentIdAsync(ulong instrumentId);

    /// <summary>Полностью заменяет график купонов инструмента (типичный паттерн обновления справочника от MOEX).</summary>
    Task ReplaceForInstrumentAsync(ulong instrumentId, IEnumerable<CouponSchedule> schedule);
}
