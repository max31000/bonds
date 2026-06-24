# MOEX ISS фикстуры — провенанс (этап 04)

Все файлы в этой папке, кроме явно помеченных `SYNTHETIC`, — **реальные** ответы публичного API
MOEX ISS (`https://iss.moex.com`), сохранённые живым HTTP-запросом в ходе реализации этапа 04
(сеть была доступна в среде выполнения агента). Запросы делались один раз, вручную, чтобы
получить образцы; сами тесты парсеров запускаются на этих сохранённых файлах без сетевых вызовов.

| Файл | SECID / запрос | Реальный? | Покрывает |
|---|---|---|---|
| `securities_fixed_ofz_26238.json` | `SU26238RMFS4` (ОФЗ-ПД 26238, фикс. купон) | да | обычная фикс-купонная гособлигация |
| `bondization_fixed_ofz_26238.json` | `SU26238RMFS4` bondization | да | купоны без амортизации/оферт |
| `securities_floater_ofz_29025.json` | `SU29025RMFS2` (ОФЗ-ПК 29025, флоатер) | да | флоатер, две строки (BOARDID SPOB/TQOB) |
| `bondization_floater_ofz_29025.json` | `SU29025RMFS2` bondization | да | известные исторические купоны флоатера |
| `securities_amortizing_offer_gtlk_1p16.json` | `RU000A101GD3` (ГТЛК БО 001P-16) | да | амортизируемая корп. бумага с офертой |
| `bondization_amortizing_offer_gtlk_1p16.json` | `RU000A101GD3` bondization | да | реальный график амортизации (8 платежей) + 1 put-оферта |
| `zcyc_gcurve.json` | `/iss/engines/stock/zcyc/securities.json` | да | параметры NSS безрисковой кривой на дату запроса |
| `securities_search_by_isin.json` | `/iss/securities.json?q=RU000A1038V6` | да | резолвер ISIN→SECID |
| `bondization_incomplete_coupons_SYNTHETIC.json` | — | **нет, синтетика** | см. ниже |

## Синтетическая фикстура неполных купонов

`bondization_incomplete_coupons_SYNTHETIC.json` получена программно из реального
`bondization_fixed_ofz_26238.json` путём удаления среднего и последнего купона из массива
`coupons.data` (см. историю реализации этапа 04). В ходе живых запросов этой сессии не был
найден реальный SECID с подтверждённой и воспроизводимой неполнотой купонного графика на MOEX
ISS (это документированный в spec §4.4 риск, но он не гарантированно воспроизводим на любой
конкретной активно торгуемой бумаге в произвольный момент). Фикстура явно помечена `SYNTHETIC`
в имени файла и в комментарии теста, который её использует
(`MoexBondizationParserTests.IncompleteCoupons_SyntheticFixture_DetectedAsDataIncomplete`),
чтобы не выдавать её за реальный ответ MOEX.
