using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(ulong id, ulong userId);

    /// <summary>
    /// Возвращает счёт по Id без привязки к пользователю (plan/22 часть C: <c>ICommissionRateProvider</c>
    /// резолвит эффективную ставку по AccountId и должен сам найти UserId для override из настроек —
    /// вызывающий HTTP-слой уже проверил принадлежность счёта пользователю на входе запроса, здесь
    /// повторная проверка избыточна, single-user MVP §2). Null — счёт не найден.
    /// </summary>
    Task<Account?> GetByIdAsync(ulong id);

    Task<IEnumerable<Account>> GetByUserIdAsync(ulong userId);
    Task<ulong> CreateAsync(Account account);
    Task UpdateAsync(Account account);

    /// <summary>
    /// Возвращает Id единственного брокерского счёта продукта (plan/00 §2: single-user, один
    /// счёт на MVP) — самый старый (по Id) существующий <see cref="Account"/>, без привязки к
    /// конкретному пользователю/сессии. Используется планировщиком (этап 07, plan/07
    /// "Single-user продукт... используй тот же способ в Scheduler") и будущим HTTP force-refresh
    /// (этап 08), которым неоткуда взять AccountId иначе, чем из единственной существующей
    /// записи (фоновый цикл не имеет HTTP-контекста/JWT с UserId). Null — если ни одного счёта ещё
    /// не создано (например, чистая БД до первого логина/онбординга через UI) — вызывающий код
    /// должен пропустить цикл без падения, не считать это ошибкой.
    /// </summary>
    Task<ulong?> GetPrimaryAccountIdAsync();
}
