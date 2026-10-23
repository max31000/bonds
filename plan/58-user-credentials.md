# Задача 58 — «Подключения»: токены брокеров и ИИ-провайдеров в настройках пользователя

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md).
> Общий дизайн: [docs/2026-10-23-capital-automation-design.md](../docs/2026-10-23-capital-automation-design.md).
>
> **Зависимости:** нет. **Порядок в волне:** первая; задачи 59–60 регистрируют в её реестре своих провайдеров.
>
> **Рамки (что НЕ делать):** не реализовывать Freedom-клиент (задача 59), торговлю, агентов.
> Не удалять колонки `user_settings.tinvest_token_*` (удалим отдельной миграцией после выката).
> Никакого хардкода «владельца»: всё по `user_id` из `ICurrentUser` — сервисом в будущем будут
> пользоваться 1–2 друга.

---

## Проблема

Сейчас у пользователя ровно один секрет — read-only токен T-Invest в `user_settings.tinvest_token_encrypted`
(`SettingsEndpoints.PutTInvestToken`, `TInvestTokenProvider`). Дальше нужны ещё: ключи Freedom
(пара public/private), торговый токен T-Invest (фаза 3), токен ИИ-агента (Claude Code OAuth; позже,
возможно, OpenAI и др.). Каждый секрет **включает свою часть функционала**: нет ключей Freedom — нет
раздела Freedom; нет токена ИИ — нет агентов. Нужен единый расширяемый механизм. Добавление нового
провайдера не должно требовать новой миграции или нового эндпоинта.

## Что сделать

### A. Backend

1. **Миграция `029_user_credentials.sql`**:
   ```sql
   CREATE TABLE user_credentials (
     id                BIGINT       NOT NULL AUTO_INCREMENT,
     user_id           BIGINT       NOT NULL,
     provider          VARCHAR(48)  NOT NULL,   -- id провайдера из реестра, напр. 'tinvest_read'
     secret_encrypted  TEXT         NOT NULL,   -- DataProtection(JSON полей секрета)
     secret_last4      VARCHAR(8)   NULL,       -- маска для UI (последние 4 символа «главного» поля)
     meta_json         JSON         NULL,       -- НЕсекретные данные от валидатора (тариф, id счёта, scope)
     status            VARCHAR(16)  NOT NULL DEFAULT 'unverified', -- ok | invalid | unverified
     status_message    VARCHAR(255) NULL,       -- человекочитаемая причина invalid (без секрета!)
     verified_at       DATETIME     NULL,
     created_at        DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
     updated_at        DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
     PRIMARY KEY (id),
     UNIQUE KEY uq_user_credentials_user_provider (user_id, provider),
     CONSTRAINT fk_user_credentials_user FOREIGN KEY (user_id) REFERENCES users(id)
   ) ENGINE=InnoDB ...;
   ```
   Типы колонок `users.id` сверить с 003 и выровнять. В той же миграции — перенос существующего
   токена: `INSERT IGNORE INTO user_credentials (user_id, provider, secret_encrypted, secret_last4, status)
   SELECT user_id, 'tinvest_read', tinvest_token_encrypted, tinvest_token_last4, 'ok' FROM user_settings
   WHERE tinvest_token_encrypted IS NOT NULL`. Шифротекст переносится **как есть**. Поэтому для `tinvest_read`
   используется **тот же** purpose протектора `TInvestTokenProvider.ProtectorPurpose`, а секрет этого
   провайдера хранится как **голая строка токена**, а не JSON — это легаси-формат, отметить в doc-comment.
   Остальные провайдеры: purpose `bonds.credentials.v1.{provider}`, секрет — JSON `{ "field": "value" }`.
   Обновить `MigrationIdempotencyTests` (счётчик + список; миграция должна быть идемпотентной).
2. **Core — реестр провайдеров** (`Bonds.Core/Credentials/`):
   - `CredentialProviderDescriptor` (sealed record): `Id`, `Title`, `Category` (`Broker` | `Ai`),
     `Fields` (список `CredentialField(Name, Title, IsSecret, IsMaskSource)`), `Unlocks` (строка: какой
     функционал включает), `Experimental` (bool).
   - `CredentialProviders` — статический список **только известных сейчас**: `tinvest_read`
     (поле `token`); `tinvest_trade` (поле `token`; Unlocks «исполнение сделок, фаза 3», Experimental);
     `claude_code` (Category Ai, поле `oauthToken`; Unlocks «ИИ-агенты: мониторинг и разборы»). Freedom
     добавит задача 59 (`freedom`: `publicKey`, `privateKey`). Расширение — новая запись плюс
     валидатор, без миграций.
3. **Infrastructure**:
   - `IUserCredentialRepository` (Dapper, по образцу `InstrumentRepository`): Get(userId, provider),
     List(userId), Upsert, Delete.
   - `IUserCredentialStore` — единственная точка шифрования и дешифрования.
     - Методы: `GetSecretAsync(userId, provider) → IReadOnlyDictionary<string,string>?`, `SaveAsync(...)`,
       `DeleteAsync`, `ListStatusesAsync(userId)`, `HasValidAsync(userId, provider)`.
     - Ключи DataProtection сменились и дешифровка падает → статус `invalid` + `status_message` «нужно
       ввести заново». Исключение не пробрасывается, секрет не логируется.
   - `ICredentialValidator` (keyed по provider id; DI — `IEnumerable<ICredentialValidator>`):
     `ValidateAsync(fields, ct) → CredentialValidationResult(Ok, Message?, MetaJson?, Last4)`.
     - `tinvest_read`: обёртка существующего `TInvestTokenValidator`, поведение не менять.
     - `tinvest_trade`: формальная проверка (непустой, без пробелов) плюс `Experimental`. Сетевую
       проверку прав сделает фаза 3; отметить TODO-комментарием со ссылкой на дизайн §5.
     - `claude_code`: формальная проверка — непустой, без пробелов, длина ≥ 20, префикс `sk-ant-oat`
       (иначе invalid «ожидается токен из `claude setup-token`»). В сеть не ходить.
   - `TInvestTokenProvider` читает токен из `IUserCredentialStore` (`tinvest_read`), а при отсутствии
     записи — из `user_settings` (страховка на время выката, пометить `// TODO удалить после 029+1`).
4. **API** (`CredentialsEndpoints`, регистрация в `Program.cs`, защищены FallbackPolicy):
   - `GET /api/credentials` →
     `[{ provider, title, category, fields:[{name,title,isSecret}], unlocks, experimental, status, statusMessage, last4, verifiedAt, meta }]`
     — по **всем** провайдерам реестра; незаданные приходят со `status: "missing"`. Секретов в ответе нет никогда.
   - `PUT /api/credentials/{provider}` с телом `{ fields: { name: value } }`.
     - Неизвестный provider → 404. Нет обязательного поля → 422.
     - Валидатор не прошёл → 422 `{error: message, type:"ValidationException"}`, **ничего не сохраняется**.
     - Валидатор прошёл → сохранить (status ok, meta, last4) и вернуть DTO строки.
   - `DELETE /api/credentials/{provider}` → 204.
   - `GET /api/capabilities` → `{ tinvestRead, tinvestTrade, freedom, aiAgents }`. Флаг true только при
     status ok. Строится по реестру: ключ = провайдер, а `aiAgents` = любой провайдер категории Ai со
     статусом ok. Фронт по этому ответу показывает или прячет разделы.
   - `PUT /api/settings/tinvest-token` оставить: внутри он делегирует в store (`tinvest_read`), контракт
     не меняется. `GET /api/settings` продолжает отдавать маску из нового хранилища.
5. **Тесты.**
   - Юнит: реестр (уникальные id, у каждого провайдера ровно одно `IsMaskSource`-поле); валидатор
     claude (префикс, пробелы, длина); store — round-trip шифрования через ephemeral DataProtection,
     legacy-формат `tinvest_read`, порча шифротекста → invalid без исключения.
   - Интеграционные: PUT, затем GET (секрета нет в теле: проверить строкой, что значения поля нет в
     JSON), 422 без сохранения, DELETE, capabilities, изоляция двух пользователей (`Mvv42TestAuth` с
     разными tg), миграция переносит существующий токен (засеять `user_settings` до прогона —
     по образцу других миграционных тестов, если есть; иначе юнит на SQL-скрипт не нужен, достаточно
     интеграционного кейса «старый PUT tinvest-token → видно в /api/credentials»).

### B. Frontend

1. Страница настроек (`pages/Settings.tsx`): новый `Section` **«Подключения»** — карточка на каждого
   провайдера из `GET /api/credentials`, сгруппированно «Брокеры» / «ИИ».
   - В карточке: название, бейдж статуса (Подключено / Ошибка: message / Не задано), маска `••••last4`,
     дата проверки, строка «Включает: …».
   - Кнопки «Задать / Заменить» (раскрывает форму: поля `PasswordInput` для `isSecret`) и «Удалить»
     (с подтверждением).
   - Существующий блок токена T-Invest **заменить** карточкой `tinvest_read`, сохранив тексты-подсказки
     о read-only токене.
   - Для `claude_code` подсказка: «Выполните `claude setup-token` на своём компьютере и вставьте токен.
     Используется подписка Claude, а не API-ключ».
2. `api/credentials.ts`, типы в `api/types.ts` (буквально по DTO), стор `useCredentialsStore`
   (+ `capabilities`). Capabilities грузятся один раз в `ServiceLayout` и доступны остальным страницам.
3. Vitest (MSW): список рендерится по ответу; при сохранении 422 показывается сообщение и форма не
   закрывается; удаление вызывает DELETE; секрет не отображается после сохранения (только маска).
   Мобильная ширина: карточки в колонку.

## Критерии приёмки

- [ ] Существующий токен T-Invest после миграции работает: синк проходит, маска видна в «Подключениях».
- [ ] Ни один ответ API не содержит секретов (тест на каждом эндпоинте).
- [ ] Новый провайдер добавляется записью в реестре плюс валидатором, без миграций и новых эндпоинтов.
- [ ] Данные двух пользователей изолированы (интеграционный тест).
- [ ] `scripts/pre-push-check.sh --all` — зелёный.
