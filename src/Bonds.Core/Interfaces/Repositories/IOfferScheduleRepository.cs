using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IOfferScheduleRepository
{
    Task<IEnumerable<OfferSchedule>> GetByInstrumentIdAsync(ulong instrumentId);
    Task ReplaceForInstrumentAsync(ulong instrumentId, IEnumerable<OfferSchedule> schedule);
}
