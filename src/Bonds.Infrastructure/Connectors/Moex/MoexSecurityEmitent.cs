namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Одна строка bulk-справочника ISS <c>/iss/securities.json?engine=stock&amp;market=bonds</c>
/// (колонки secid, emitent_id, emitent_title, emitent_inn): эмитент выпуска. Любое из полей эмитента
/// может быть null — старые выпуски и иностранные эмитенты биржа отдаёт без ИНН/без эмитента вовсе.
/// Единиц измерения нет (идентификаторы и названия).
/// </summary>
public sealed record MoexSecurityEmitent(string Secid, int? EmitentId, string? EmitentTitle, string? EmitentInn);
