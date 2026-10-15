namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Одна строка bulk-справочника ISS <c>/iss/securities.json?engine=stock&amp;market=bonds</c>
/// (колонки secid, emitent_id, emitent_title, emitent_inn): эмитент выпуска. Любое из полей эмитента
/// может быть null — старые выпуски и иностранные эмитенты биржа отдаёт без ИНН/без эмитента вовсе.
/// Единиц измерения нет (идентификаторы и названия).
/// </summary>
public sealed record MoexSecurityEmitent(string Secid, int? EmitentId, string? EmitentTitle, string? EmitentInn);

/// <summary>
/// Результат полного обхода bulk-справочника. <see cref="Complete"/> = false, когда обход оборвался
/// на недоступной/битой странице или упёрся в предохранитель страниц: собранные строки применять можно
/// (идемпотентно), но суточный шаг обогащения не считается выполненным и повторяется позже.
/// </summary>
public sealed record MoexSecurityEmitentsResult(IReadOnlyList<MoexSecurityEmitent> Rows, bool Complete);
