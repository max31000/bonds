using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IAmortizationScheduleRepository
{
    Task<IEnumerable<AmortizationSchedule>> GetByInstrumentIdAsync(ulong instrumentId);
    Task ReplaceForInstrumentAsync(ulong instrumentId, IEnumerable<AmortizationSchedule> schedule);
}
