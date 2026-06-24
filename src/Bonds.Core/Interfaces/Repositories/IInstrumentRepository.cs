using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IInstrumentRepository
{
    Task<Instrument?> GetByIdAsync(ulong id);
    Task<Instrument?> GetByIsinAsync(string isin);
    Task<Instrument?> GetBySecidAsync(string secid);
    Task<Instrument?> GetByFigiAsync(string figi);
    Task<IEnumerable<Instrument>> GetAllAsync();

    /// <summary>Создаёт инструмент, если ISIN не существует; иначе обновляет существующую запись (upsert по ISIN).</summary>
    Task<ulong> UpsertAsync(Instrument instrument);
}
