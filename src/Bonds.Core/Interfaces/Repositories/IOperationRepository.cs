using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IOperationRepository
{
    Task<Operation?> GetByIdAsync(ulong id, ulong accountId);
    Task<Operation?> GetByExternalIdAsync(string externalId);
    Task<IEnumerable<Operation>> GetByAccountIdAsync(ulong accountId, DateOnly? from = null, DateOnly? to = null);

    /// <summary>
    /// Идемпотентный upsert по <see cref="Operation.ExternalId"/> (plan/03 §C, spec §5).
    /// Повторный синк того же ExternalId обновляет запись, а не создаёт дубль.
    /// Возвращает Id вставленной/обновлённой строки.
    /// </summary>
    Task<ulong> UpsertByExternalIdAsync(Operation operation);

    /// <summary>Батч-вставка операций синка одним вызовом (идемпотентно по ExternalId).</summary>
    Task<int> UpsertManyByExternalIdAsync(IEnumerable<Operation> operations);
}
