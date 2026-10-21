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
| `bond_universe_with_yields_2026-10-15.json` | `/iss/engines/stock/markets/bonds/securities.json?iss.only=securities,marketdata,marketdata_yields&iss.meta=off` (срез 19 бумаг из ~3500) | да | блок `marketdata_yields` (G-/Z-спред в б.п., EFFECTIVEYIELD в %, YIELDDATETYPE MATDATE/OFFER/MBS), ACCRUEDINT/ISSUESIZE, бумаги БЕЗ строки в `marketdata_yields`, SECID на нескольких board'ах (PACT/SPOB/TQOB) |
| `securities_emitents_page_2026-10-15.json` | `/iss/securities.json?engine=stock&market=bonds&iss.only=securities&securities.columns=secid,emitent_id,emitent_title,emitent_inn&iss.meta=off&start=3100` (одна страница, 100 строк из ~10 700) | да | bulk-справочник эмитентов (задача 43): полные эмитенты, эмитент без ИНН (иностранный), старые выпуски без эмитента вовсе |
| `security_description_default_RU000A10BVW6.json` | `/iss/securities/RU000A10BVW6.json?iss.only=description&iss.meta=off` (ЕвроТранс 01) | да | карточка с `HASDEFAULT=1` и `HASTECHNICALDEFAULT=1` |
| `security_description_floater_ruonia_RU000A10FV69.json` | то же, `RU000A10FV69` (РЖД БО 001P-54R) | да | `COUPON_BENCHMARK=RUONIA`, `COUPON_BENCHMARK_SPREAD="1.90"` (проценты) |
| `security_description_ofz_floater_SU29025RMFS2.json` | то же, `SU29025RMFS2` | да | флоатер ОФЗ со спредом `"0.00"` (валидный ноль) |
| `security_description_cpi_early_repayment_RU000A0JVCB5.json` | то же, `RU000A0JVCB5` (ФСК Россети сер.37) | да | `EARLYREPAYMENT=0`, бенчмарк `CPI` |
| `security_description_ofz_fixed_SU26238RMFS4.json` | то же, `SU26238RMFS4` | да | ОФЗ без флагов дефолта и без бенчмарка |
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

## Фонды (задача 51)

| Файл | Эндпоинт | Реальный ответ | Что проверяет |
|---|---|---|---|
| `index_history_RGBI_2026-10.json` | `/iss/history/engines/stock/markets/index/securities/RGBI.json?from=2026-10-01&till=2026-10-20` (снято 2026-10-21) | да | `YIELD` (проценты → доля), `DURATION` (дни → годы), `CLOSE` индекса |
| `shares_history_TQBR_TPAY_2026-10.json` | `/iss/history/engines/stock/markets/shares/boards/TQBR/securities/TPAY.json?from=2026-10-01&till=2026-10-20` (снято 2026-10-21) | да | `CLOSE` пая — рубли за пай (не % номинала) |
