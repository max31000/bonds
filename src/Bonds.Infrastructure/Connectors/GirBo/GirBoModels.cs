namespace Bonds.Infrastructure.Connectors.GirBo;

/// <summary>
/// Организация из поиска ГИР БО. <see cref="Id"/> — внутренний id реестра (не ИНН), <see cref="IsCb"/> —
/// признак кредитной организации по последней подаче из поисковой выдачи (<c>null</c> — блока подачи нет).
/// </summary>
public sealed record GirBoOrganization(long Id, string Inn, string? ShortName, bool? IsCb);

/// <summary>Подача бухгалтерской отчётности: <see cref="Id"/> (bfoId), отчётный год, признак ЦБ-отчётности.</summary>
public sealed record GirBoFiling(long Id, int Period, bool IsCb);

/// <summary>
/// Источник ГИР БО недоступен (сеть, таймаут, 5xx/429, нечитаемый ответ) — в отличие от «не найдено»
/// (404/пустая выдача), которое является нормальным результатом и исключением не выражается.
/// Вызывающий код трактует это как «источник недоступен → не засчитывать шаг» (паттерн T-44fix).
/// </summary>
public sealed class GirBoUnavailableException : Exception
{
    public GirBoUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
