# Задача 13 — Токен, переживающий передеплой + видимое здоровье синка

> **Кому адресовано.** Агент-исполнитель. Задача самодостаточна: весь нужный контекст — в этом файле
> и по ссылкам. Общие конвенции репо — [CLAUDE.md](../CLAUDE.md), обзор архитектуры —
> [00-overview-and-architecture.md](00-overview-and-architecture.md).
>
> **Зависимости:** нет. Эта задача — первая в очереди: пока она не сделана, автосинк ломается при
> каждом передеплое, и все остальные фичи (реалтайм, XIRR-история) не имеют смысла.
>
> **Рабочий процесс:** TDD там, где есть логика (юнит/интеграционные тесты xUnit в `tests/`,
> vitest на фронте). Перед пушем — **обязательно** скилл `pre-push-check` на зелёном. Секреты
> в код/логи не попадают.

---

## Проблема

**Корневой баг:** T-Invest токен шифруется ASP.NET DataProtection —
[DependencyInjection.cs:102](../src/Bonds.Infrastructure/DependencyInjection.cs) вызывает голый
`services.AddDataProtection()`. Ключи шифрования падают в файловую систему контейнера
(`/root/.aspnet/DataProtection-Keys`), контейнер при деплое пересоздаётся (`docker stop && rm && run`
в [.github/workflows/backend.yml](../.github/workflows/backend.yml), стр. ~94) → ключи теряются →
сохранённый в БД токен перестаёт расшифровываться →
[TInvestTokenProvider.cs:64](../src/Bonds.Infrastructure/Services/TInvestTokenProvider.cs) молча
возвращает `null` → синк тихо деградирует. Пользователь узнаёт об этом только по пустым данным.

Вторая часть проблемы — **тишина**: упавший/деградировавший автосинк никак не виден в UI
(в шапке только кнопка «Обновить данные»), а невалидный токен принимается настройками без проверки.

## Что сделать

### A. Персист ключей DataProtection (backend + deploy)

1. В `DependencyInjection.cs` заменить `services.AddDataProtection()` на вариант с
   `.PersistKeysToFileSystem(new DirectoryInfo(keysPath))`, где `keysPath` берётся из конфигурации
   (`DataProtection:KeysPath`), дефолт — `/app/dataprotection-keys`. `SetApplicationName("bonds-api")`
   тоже задать явно (стабильность на случай смены имени сборки).
2. В [.github/workflows/backend.yml](../.github/workflows/backend.yml) в `docker run` добавить
   volume: `-v /var/lib/bonds/dataprotection-keys:/app/dataprotection-keys \`. Каталог на VDS
   создаётся `mkdir -p` строкой перед `docker run` (идемпотентно).
3. В [docker-compose.yml](../docker-compose.yml) (локальная разработка) добавить аналогичный
   named volume, чтобы локальный цикл вёл себя как прод.
4. **Миграция уже протухшего токена не нужна**: после первого же деплоя с volume пользователь один
   раз пересохраняет токен через настройки — дальше он живёт вечно. Написать это в описании PR.

**Тест:** юнит-тест на то, что провайдер DataProtection сконфигурирован с file-system-персистом
малополезен; вместо этого — интеграционный тест в `tests/Bonds.IntegrationTests`: зашифровать строку
протектором с purpose `TInvestTokenProvider.ProtectorPurpose`, пересоздать `ServiceProvider` с тем же
каталогом ключей (temp dir), расшифровать — значение совпало. Это ровно сценарий «рестарт контейнера».

### B. Здоровье синка в UI (backend + frontend)

Бэкенд уже отдаёт всё нужное: `GET /api/sync/status` →
[SyncEndpoints.cs](../src/Bonds.Api/Endpoints/SyncEndpoints.cs) (`LastSuccessAtUtc`,
`LastFailureAtUtc`, `LastRunErrors`, `IsRunning`). Одно расширение:

1. В `SyncStatusDto` и [SyncCycleStatus.cs](../src/Bonds.Infrastructure/Scheduling/SyncCycleStatus.cs)
   добавить флаг `TokenMissingOrInvalid` — выставляется циклом
   ([SyncCycleService.cs](../src/Bonds.Infrastructure/Scheduling/SyncCycleService.cs)), когда
   `ITInvestTokenProvider.GetTokenAsync()` вернул `null` при заведённом пользователе (т.е. токен есть
   в БД, но не расшифровался — или его нет вовсе; различать эти два случая текстом ошибки).
2. Фронт, [AppLayout.tsx](../bonds-web/src/components/AppLayout.tsx) + существующий
   [useSyncStore.ts](../bonds-web/src/store/useSyncStore.ts): рядом с кнопкой «Обновить данные»
   показать компактный статус:
   - зелёная точка + «синк N мин/ч назад» (`LastSuccessAtUtc`, форматировать относительно, dayjs);
   - красный бейдж «Ошибка синка» с tooltip'ом первой ошибки из `LastRunErrors`, если
     `LastFailureAtUtc > LastSuccessAtUtc`;
   - оранжевый бейдж «Токен не подключён / недействителен» при `TokenMissingOrInvalid` — кликабельный,
     ведёт на `/settings`.
   Статус обновлять при маунте и после ручного синка (уже есть `refreshStatus`).

### C. Валидация токена при сохранении (backend + frontend)

1. В `PUT /api/settings/tinvest-token`
   ([SettingsEndpoints.cs](../src/Bonds.Api/Endpoints/SettingsEndpoints.cs)) перед сохранением
   делать пробный вызов T-Invest (`ITInvestPortfolioClient.GetPrimaryAccountIdAsync` с этим токеном —
   смотри, как клиент создаётся в
   [TInvestPortfolioClient.cs](../src/Bonds.Infrastructure/Connectors/TInvest/TInvestPortfolioClient.cs);
   вероятно, потребуется фабричный метод «клиент из явного токена»). Невалидный токен → 422 с
   человекочитаемым сообщением, токен НЕ сохраняется. Валидный → сохранить и вернуть в ответе
   маскированный идентификатор счёта (последние 4 символа) как подтверждение.
2. Фронт, [Settings.tsx](../bonds-web/src/pages/Settings.tsx): показать результат — успех с
   подтверждением счёта / ошибка «токен не прошёл проверку». Токен, как и раньше, обратно на фронт
   никогда не отдаётся.
3. Интеграционный тест: PUT с токеном, на котором фейковый клиент кидает `UnauthenticatedException`,
   → 422, в БД ничего не записано.

### D. Telegram-алерт при падении автосинка

Бот уже используется для авторизации и CI-алертов (`Telegram__BotToken` в ENV контейнера).
1. В [SyncSchedulerHostedService.cs](../src/Bonds.Infrastructure/Scheduling/SyncSchedulerHostedService.cs):
   если плановый (не ручной) цикл завершился с `HasErrors` или `TokenMissingOrInvalid` — отправить
   сообщение владельцу (`Telegram__OwnerId`) через простой HTTP-вызов Bot API (sendMessage). Не чаще
   одного алерта на уникальный набор ошибок в сутки (антиспам: хранить в памяти хеш последнего
   отправленного + дату).
2. Текст сообщения — без токена и без чувствительных данных: «Bonds: автосинк упал: {первая ошибка}».
3. Юнит-тест на антиспам-логику (выделить её в чистый класс, например
   `Bonds.Infrastructure/Scheduling/SyncAlertThrottle.cs`).

## Критерии приёмки

- [ ] Интеграционный тест «рестарт процесса не ломает расшифровку» зелёный; ключи в контейнере
      пишутся в путь из конфига.
- [ ] `backend.yml` монтирует volume для ключей; деплой-скрипт идемпотентен.
- [ ] В шапке виден статус последнего синка; при протухшем/отсутствующем токене — явный бейдж
      со ссылкой на настройки (vitest-тест на все три состояния).
- [ ] Невалидный токен не сохраняется и возвращает 422; валидный — подтверждение с маской счёта.
- [ ] Упавший автосинк шлёт Telegram-алерт, повторные идентичные ошибки в тот же день — нет.
- [ ] `dotnet build`/`dotnet test`, `yarn typecheck`/`test:run`/`build` — зелёные; `pre-push-check` пройден.
