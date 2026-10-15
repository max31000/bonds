# ГИР БО ФНС фикстуры — провенанс (задача 45)

Все файлы — **реальные** публичные ответы `https://bo.nalog.gov.ru` (без авторизации, `User-Agent:
BondAnalytics/1.0 (personal portfolio tool)`), сняты живым `curl` 2026-10-15 с паузой ≥1 с между запросами.
Данные публичные (раскрытие бухгалтерской отчётности), анонимизация не нужна. Тесты парсера и клиента
(`Connectors/GirBo/*`) работают на этих файлах без сети. Значения в `balance`/`financialResult` — **тысячи рублей**.

| Файл | Запрос | Покрывает |
|---|---|---|
| `search_lenta_7814148471.json` | `/advanced-search/organizations/search?query=7814148471&page=0` | ООО «Лента»: выдача, ИНН обёрнут в `<strong>`, блок `bfo.isCb` |
| `search_empty.json` | то же, `query=0000000001` | пустой `content` — «не найдено» (норма, не ошибка) |
| `search_mts_7740000076.json` | то же, `query=7740000076` | ПАО «МТС» (id 7155648), свежая подача 2025 |
| `search_bank_7707083893.json` | то же, `query=7707083893` | Сбербанк: в ГИР БО не найден (банки отчитываются в ЦБ) → `not_found` |
| `bfo_list_lenta_4953069.json` | `/nbo/organizations/4953069/bfo/` | единственная подача (2021), `typeCorrections` |
| `bfo_list_mts_7155648.json` | `/nbo/organizations/7155648/bfo/` | 5 подач, порядок НЕ по году (выбор последней по `period` обязателен) |
| `details_lenta_14810112.json` | `/nbo/bfo/14810112/details` | годовая подача 2021: баланс (3 года), ОФР (current/previous), ОДДС, `auditReport` |
| `details_mts_28452021.json` | `/nbo/bfo/28452021/details` | годовая подача 2025: период определяется подачей, не деталями |
