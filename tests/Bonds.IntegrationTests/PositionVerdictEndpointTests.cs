using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Analytics;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 49 часть A — GET /api/analytics/position-verdicts: Red-бумага → ConsiderExit, дешёвая зелёная в рамках лимита →
/// BuyMoreCandidate, выгодная замена → ConsiderReplace (цифры из SwitchAnalysisService v2), флоатер без замены по выгоде,
/// бумага без скоркарты → Hold/Low, активное рыночное событие блокирует «докупить». Сеть не используется — портфель и банк
/// засеяны напрямую через репозитории; кэш снимков банка отключён фабрикой (RelativeValueCacheDuration=0).
/// </summary>
[Collection("Integration")]
public class PositionVerdictEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public PositionVerdictEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static string Marker() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<(HttpClient Client, ulong AccountId)> CreateAuthorizedClientAsync()
    {
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });
        var user = await new UserRepository(Conn).GetByIdAsync(userId);
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, user!.TelegramId);
        return (client, accountId);
    }

    private async Task SeedYieldCurveAsync() =>
        await new YieldCurveRepository(Conn).UpsertAsync(new YieldCurveSnapshot
        {
            AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
            B1 = 7.5m, B2 = -2.1m, B3 = 1.3m, T1 = 1.7m,
            G1 = 7.1m, G2 = 7.3m, G3 = 7.5m, G4 = 7.6m, G5 = 7.7m, G6 = 7.8m, G7 = 7.9m, G8 = 8.0m, G9 = 8.1m,
        });

    /// <summary>Позиция с купоном/котировкой (сходящийся YTM, дюрация и G-спред движка). Эмитент уникален на позицию.</summary>
    private async Task<(ulong InstrumentId, ulong PositionId, string Isin)> SeedPositionAsync(
        ulong accountId, string sector, decimal cleanPrice, decimal couponValueRub, int maturityYears = 2, decimal quantity = 10m,
        CouponType couponType = CouponType.Fixed, string? issuer = null)
    {
        var isin = $"RU{Guid.NewGuid():N}"[..12];
        var instrumentId = await new InstrumentRepository(Conn).UpsertAsync(new Instrument
        {
            Isin = isin, Issuer = issuer ?? "Эмитент " + isin, Sector = sector, FaceValue = 1000m, Currency = "RUB", CouponType = couponType,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(maturityYears)),
        });

        if (couponType == CouponType.Fixed)
        {
            await new CouponScheduleRepository(Conn).ReplaceForInstrumentAsync(instrumentId, new[]
            {
                new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)), ValueRub = couponValueRub, PeriodDays = 182, IsKnown = true },
                new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(5)), ValueRub = couponValueRub, PeriodDays = 182, IsKnown = true },
            });
        }

        await new MarketQuoteRepository(Conn).UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId, AsOf = DateOnly.FromDateTime(DateTime.UtcNow), CleanPrice = cleanPrice, DirtyPrice = cleanPrice,
            Accrued = 0m, Source = MarketQuoteSource.Moex,
        });

        var positionId = await new PositionRepository(Conn).UpsertAsync(new Position
        {
            AccountId = accountId, InstrumentId = instrumentId, Quantity = quantity, AvgPurchasePrice = cleanPrice,
        });

        return (instrumentId, positionId, isin);
    }

    /// <summary>Живая бумага банка: листинг 1, высокая ликвидность, оферты нет. Спред — биржевой (явный), чтобы не зависеть от кривой.</summary>
    private static BondUniverseEntry Bond(string secid, string isin, string sector, decimal gspread, decimal yield = 0.20m, decimal duration = 2m) => new()
    {
        Secid = secid, Isin = isin, ShortName = $"BOND {secid}", SecName = $"Full name {secid}",
        FaceValue = 1000m, Sector = sector, YieldFraction = yield, DurationYears = duration, PricePercent = 99.5m,
        TurnoverRub = 10_000_000m, BidPercent = 99.9m, OfferPercent = 100m, NumTrades = 50, ListLevel = 1,
        GspreadOfficialFraction = gspread,
        MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)), UpdatedAt = DateTime.UtcNow,
    };

    private static JsonElement VerdictOf(JsonElement body, ulong positionId) =>
        body.GetProperty("verdicts").EnumerateArray().Single(v => v.GetProperty("positionId").GetUInt64() == positionId);

    private static async Task<JsonElement> GetVerdictsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/analytics/position-verdicts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<string> Reasons(JsonElement verdict) =>
        verdict.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!);

    /// <summary>
    /// Корзина «дёшево»: позиция с высоким купоном (G-спред движка ≈ +490 б.п. к кривой) против пяти бумаг банка со спредом
    /// 200 б.п. Бумага позиции — тоже в банке (здоровый эмитент → Green). Возвращает ID и ISIN позиции.
    /// </summary>
    private async Task<(ulong InstrumentId, ulong PositionId, string Isin, string Secid)> SeedCheapGreenPositionAsync(
        ulong accountId, string marker, string? issuer = null)
    {
        var sector = $"Sector{marker}";
        var (instrumentId, positionId, isin) = await SeedPositionAsync(accountId, sector, cleanPrice: 1000m, couponValueRub: 60m, issuer: issuer);
        var secid = $"CG{marker}";
        var universe = new BondUniverseRepository(Conn);
        var peers = Enumerable.Range(0, 5).Select(i => Bond($"PE{i}{marker}", $"IPE{i}{marker}", sector, 0.0200m)).ToList();
        await universe.UpsertSnapshotBatchAsync([.. peers, Bond(secid, isin, sector, 0.0200m)]);
        await ReliabilitySeed.SeedHealthyIssuerAsync(Conn, secid, isin);
        return (instrumentId, positionId, isin, secid);
    }

    private async Task SeedFillerPositionsAsync(ulong accountId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await SeedPositionAsync(accountId, $"Filler{Marker()}", cleanPrice: 1000m, couponValueRub: 40m);
        }
    }

    // ─── Базовые случаи ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoToken_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/analytics/position-verdicts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoPositions_ReturnsEmptyListWithDisclaimer()
    {
        var (client, _) = await CreateAuthorizedClientAsync();

        var body = await GetVerdictsAsync(client);

        body.GetProperty("verdicts").GetArrayLength().Should().Be(0);
        body.GetProperty("disclaimer").GetString().Should().Contain("не рекомендация");
    }

    [Fact]
    public async Task PositionOutsideBank_IsHold_LowConfidence_NotEnoughData()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId, _) = await SeedPositionAsync(accountId, $"S{Marker()}", 1000m, 40m);

        var verdict = VerdictOf(await GetVerdictsAsync(client), positionId);

        verdict.GetProperty("verdict").GetString().Should().Be("Hold");
        verdict.GetProperty("confidence").GetString().Should().Be("Low");
        Reasons(verdict).Should().ContainSingle().Which.Should().Contain("мало данных");
        verdict.GetProperty("bestReplacement").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ─── ConsiderExit ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RedPosition_IssuerWithDefault_IsConsiderExit_WithReasonsAtMostThree()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var marker = Marker();
        var sector = $"Sector{marker}";
        var (_, positionId, isin) = await SeedPositionAsync(accountId, sector, 1000m, 60m);
        var secid = $"RD{marker}";
        await new BondUniverseRepository(Conn).UpsertSnapshotBatchAsync([Bond(secid, isin, sector, 0.0200m)]);
        // дефолт эмитента в карточке MOEX → критический флаг IssuerDefaulted → Red
        var emitentId = ReliabilitySeed.NewEmitentId();
        await ReliabilitySeed.AttachIssuerAsync(Conn, secid, emitentId, ReliabilitySeed.NewInn());
        await ReliabilitySeed.SeedCardAsync(Conn, secid, isin, emitentId, hasDefault: true, hasTechnicalDefault: false);

        var verdict = VerdictOf(await GetVerdictsAsync(client), positionId);

        verdict.GetProperty("verdict").GetString().Should().Be("ConsiderExit");
        Reasons(verdict).Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(3);
        Reasons(verdict).Should().Contain(r => r.Contains("дефолт"));
        verdict.GetProperty("bestReplacement").ValueKind.Should().Be(JsonValueKind.Null, "Red-бумага уходит в выход, замена не считается");
    }

    // ─── BuyMoreCandidate ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheapGreenPosition_WithinConcentrationLimit_IsBuyMoreCandidate()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var marker = Marker();
        var (_, positionId, _, _) = await SeedCheapGreenPositionAsync(accountId, marker);
        await SeedFillerPositionsAsync(accountId, 5); // эмитент ≈ 1/6 портфеля, лимит по умолчанию 25%

        var verdict = VerdictOf(await GetVerdictsAsync(client), positionId);

        verdict.GetProperty("verdict").GetString().Should().Be("BuyMoreCandidate");
        verdict.GetProperty("confidence").GetString().Should().BeOneOf("Medium", "High");
        Reasons(verdict).Should().Contain(r => r.Contains("дёшево к корзине")).And.HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task CheapGreenPosition_IssuerAlreadyOverLimit_IsHoldWithConcentrationReason()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId, _, _) = await SeedCheapGreenPositionAsync(accountId, Marker()); // единственная позиция → ≈100% эмитента

        var verdict = VerdictOf(await GetVerdictsAsync(client), positionId);

        verdict.GetProperty("verdict").GetString().Should().Be("Hold");
        Reasons(verdict).Should().Contain(r => r.Contains("лимит"));
    }

    [Fact]
    public async Task CheapGreenPosition_LimitFromUserSettings_IsRespected()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId, _, _) = await SeedCheapGreenPositionAsync(accountId, Marker());
        var userId = (await new AccountRepository(Conn).GetByIdAsync(accountId))!.UserId;
        // лимит концентрации из настроек (ПРОЦЕНТЫ) 100% — единственная позиция помещается (задача 62: target_allocations удалены)
        await new UserSettingsRepository(Conn).UpsertAsync(new UserSettings { UserId = userId, DefaultMaxConcentrationPercent = 100m });

        var verdict = VerdictOf(await GetVerdictsAsync(client), positionId);

        verdict.GetProperty("verdict").GetString().Should().Be("BuyMoreCandidate");
    }

    [Fact]
    public async Task CheapGreenPosition_WithActiveSpreadWideningEvent_IsNotBuyMore()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId, positionId, isin, _) = await SeedCheapGreenPositionAsync(accountId, Marker());
        await SeedFillerPositionsAsync(accountId, 5);
        await new SignalRepository(Conn).InsertAsync(new Signal
        {
            AccountId = accountId, Type = SignalType.SpreadWidening, Severity = SignalSeverity.Warning, InstrumentId = instrumentId,
            EventKey = Signal.BuildKey(SignalType.SpreadWidening, $"{isin}|2099-W01", null), Subject = "Бумага",
            FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        });

        var verdict = VerdictOf(await GetVerdictsAsync(client), positionId);

        verdict.GetProperty("verdict").GetString().Should().Be("Hold");
        Reasons(verdict).Should().Contain(r => r.Contains("спред"));
    }

    // ─── ConsiderReplace ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Сценарий «замена выгодна»: позиция с купоном 4% против пяти Green-соседей банка с доходностью 30–38%. Нога hold в расчёте
    /// замены — доходность БАНК-записи (<paramref name="holdBankYield"/>), поэтому её можно варьировать независимо от YTM движка.
    /// </summary>
    private async Task<(ulong PositionId, string OwnIsin, BondUniverseEntry BestNeighbor, BondUniverseEntry Own)> SeedReplaceScenarioAsync(
        ulong accountId, decimal holdBankYield = 0.04m)
    {
        var marker = Marker();
        var sector = $"Sector{marker}";
        var (_, positionId, isin) = await SeedPositionAsync(accountId, sector, cleanPrice: 1000m, couponValueRub: 20m);
        var universe = new BondUniverseRepository(Conn);
        var ownSecid = $"HD{marker}";
        var neighbors = new[] { (0, 0.30m), (1, 0.32m), (2, 0.34m), (3, 0.36m), (4, 0.38m) }
            .Select(x => Bond($"NB{x.Item1}{marker}", $"INB{x.Item1}{marker}", sector, gspread: 0.15m + x.Item1 * 0.01m, yield: x.Item2))
            .ToList();
        var own = Bond(ownSecid, isin, sector, 0.0050m, yield: holdBankYield);
        await universe.UpsertSnapshotBatchAsync([.. neighbors, own]);
        await ReliabilitySeed.SeedHealthyIssuerAsync(Conn, ownSecid, isin);
        foreach (var neighbor in neighbors)
        {
            await ReliabilitySeed.SeedHealthyIssuerAsync(Conn, neighbor.Secid, neighbor.Isin!);
        }

        return (positionId, isin, neighbors[^1], own);
    }

    [Fact]
    public async Task GreenPosition_WithMuchHigherYieldingGreenNeighbors_IsConsiderReplace_WithBenefitFigures()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (positionId, _, _, _) = await SeedReplaceScenarioAsync(accountId);

        var verdict = VerdictOf(await GetVerdictsAsync(client), positionId);

        verdict.GetProperty("verdict").GetString().Should().Be("ConsiderReplace");
        Reasons(verdict).First().Should().Contain("Кандидат на замену").And.Contain("годовых");
        var best = verdict.GetProperty("bestReplacement");
        best.GetProperty("name").GetString().Should().StartWith("BOND NB");
        best.GetProperty("targetReliability").GetString().Should().Be("Green");
        best.GetProperty("annualizedBenefitFraction").GetDecimal().Should().BeGreaterThanOrEqualTo(0.005m, "порог выгоды 50 б.п. годовых — ДОЛЯ");
        best.GetProperty("effectiveHorizonYears").GetDecimal().Should().BeGreaterThan(0m);
    }

    /// <summary>
    /// Одна методика в ногах (ревью В3): hold-нога берёт YIELD банка (0.10), а не YTM движка (≈0.04, купон 4%) — годовая выгода
    /// совпадает с прямым расчётом SwitchAnalysisService с доходностью банка и ОТЛИЧАЕТСЯ от расчёта с YTM движка.
    /// Параметры эталона: MV = 10 × 1000 = 10 000 ₽, комиссия — override 0.3% (ДОЛЯ), горизонт 2 г., налог не оценён (журнала нет).
    /// </summary>
    [Fact]
    public async Task ConsiderReplace_HoldLegUsesBankYield_NotEngineYtm()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var userId = (await new AccountRepository(Conn).GetByIdAsync(accountId))!.UserId;
        await new UserSettingsRepository(Conn).UpsertAsync(new UserSettings
        {
            UserId = userId, CommissionRateOverride = 0.003m, ReplacementHorizonYears = 2.0m,
        });
        const decimal bankYield = 0.10m;
        var (positionId, _, bestNeighbor, own) = await SeedReplaceScenarioAsync(accountId, holdBankYield: bankYield);

        var verdict = VerdictOf(await GetVerdictsAsync(client), positionId);

        verdict.GetProperty("verdict").GetString().Should().Be("ConsiderReplace");
        var actual = verdict.GetProperty("bestReplacement").GetProperty("annualizedBenefitFraction").GetDecimal();

        decimal Expected(decimal holdYield) => SwitchAnalysisService.Compare(
            new SwitchCandidate
            {
                PositionId = positionId, MarketValueRub = 10_000m, EffectiveYield = holdYield,
                MacaulayDurationYears = own.DurationYears, HalfSpreadFraction = LiquidityScoreCalculator.HalfSpreadFraction(own.BidPercent, own.OfferPercent),
                InvestedRub = null, HasUnknownLots = true,
            },
            new SwitchCandidate
            {
                PositionId = 0, MarketValueRub = 0m, EffectiveYield = bestNeighbor.YieldFraction, MacaulayDurationYears = bestNeighbor.DurationYears,
                HalfSpreadFraction = LiquidityScoreCalculator.HalfSpreadFraction(bestNeighbor.BidPercent, bestNeighbor.OfferPercent),
                TargetHorizonDate = bestNeighbor.MaturityDate,
            },
            2.0m, 0.003m, 0.003m, BusinessClock.MoscowToday()).AnnualizedBenefitFraction!.Value;

        actual.Should().BeApproximately(Expected(bankYield), 0.0001m, "нога hold — доходность банка (MOEX YIELD), как у ног-целей");
        actual.Should().NotBeApproximately(Expected(0.04m), 0.005m, "YTM движка (≈4%) — другая методика, в вердикте не используется");
    }

    [Fact]
    public async Task Floater_NeverGetsReplaceByYield()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var marker = Marker();
        var sector = $"Sector{marker}";
        var (_, positionId, isin) = await SeedPositionAsync(accountId, sector, cleanPrice: 1000m, couponValueRub: 20m, couponType: CouponType.Floating);
        var universe = new BondUniverseRepository(Conn);
        var ownSecid = $"FL{marker}";
        var own = Bond(ownSecid, isin, sector, 0.0050m, yield: 0.04m);
        own.IsFloater = true;
        var neighbors = Enumerable.Range(0, 5).Select(i => Bond($"NB{i}{marker}", $"INB{i}{marker}", sector, 0.15m + i * 0.01m, 0.30m + i * 0.02m)).ToList();
        await universe.UpsertSnapshotBatchAsync([.. neighbors, own]);
        await ReliabilitySeed.SeedHealthyIssuerAsync(Conn, ownSecid, isin);

        var verdict = VerdictOf(await GetVerdictsAsync(client), positionId);

        verdict.GetProperty("verdict").GetString().Should().NotBe("ConsiderReplace");
        verdict.GetProperty("bestReplacement").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ─── Устойчивость (ревью В3) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Эмитенты, различающиеся только регистром, не роняют запрос (раньше ToDictionary(OrdinalIgnoreCase) падал на дубликате ключа → 500)
    /// и СУММИРУЮТСЯ для концентрации: «Emit» (1/6) + «EMIT» (1/6) = 33% > лимита 25% → докупка упирается в лимит.
    /// </summary>
    [Fact]
    public async Task IssuersDifferingOnlyByCase_DoNotBreakRequest_AndConcentrationIsSummed()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var issuer = $"Emit{Marker()}";
        var (_, cheapPositionId, _, _) = await SeedCheapGreenPositionAsync(accountId, Marker(), issuer: issuer);
        var (_, sameIssuerOtherCasePositionId, _) = await SeedPositionAsync(accountId, $"S{Marker()}", 1000m, 40m, issuer: issuer.ToUpperInvariant());
        await SeedFillerPositionsAsync(accountId, 4); // 6 позиций поровну: каждый «регистр» ≈ 16.7%, вместе ≈ 33%

        var body = await GetVerdictsAsync(client);

        body.GetProperty("verdicts").GetArrayLength().Should().Be(6);
        VerdictOf(body, sameIssuerOtherCasePositionId).GetProperty("verdict").GetString().Should().Be("Hold");
        var cheap = VerdictOf(body, cheapPositionId);
        cheap.GetProperty("verdict").GetString().Should().Be("Hold", "доля эмитента с учётом обоих регистров превышает лимит 25%");
        Reasons(cheap).Should().Contain(r => r.Contains("лимит"));
    }

    /// <summary>
    /// Сбой расчёта одной позиции не роняет остальные: горизонт 0 в настройках (записан в обход валидации API) бросает
    /// ArgumentOutOfRange в SwitchAnalysisService ровно для позиции, которой считается замена; позиция вне банка считается как обычно.
    /// </summary>
    [Fact]
    public async Task FailureOfOnePosition_DegradesToHoldNotEvaluated_OthersStillComputed()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var userId = (await new AccountRepository(Conn).GetByIdAsync(accountId))!.UserId;
        await new UserSettingsRepository(Conn).UpsertAsync(new UserSettings { UserId = userId, ReplacementHorizonYears = 0m });
        var (failingPositionId, _, _, _) = await SeedReplaceScenarioAsync(accountId);
        var (_, outsideBankPositionId, _) = await SeedPositionAsync(accountId, $"S{Marker()}", 1000m, 40m);

        var body = await GetVerdictsAsync(client);

        var failed = VerdictOf(body, failingPositionId);
        failed.GetProperty("verdict").GetString().Should().Be("Hold");
        failed.GetProperty("confidence").GetString().Should().Be("Low");
        Reasons(failed).Should().ContainSingle().Which.Should().Contain("Не удалось оценить");
        failed.GetProperty("bestReplacement").ValueKind.Should().Be(JsonValueKind.Null);

        var healthy = VerdictOf(body, outsideBankPositionId);
        Reasons(healthy).Should().ContainSingle().Which.Should().Contain("мало данных");
    }

    /// <summary>
    /// Дефолт эмитента — на ВСЕ его позиции (ревью В3): событие привязано к первой бумаге, вторая бумага того же emitent_id тоже
    /// получает ConsiderExit; бумага другого эмитента — нет.
    /// </summary>
    [Fact]
    public async Task IssuerDefaultEvent_AppliesToAllPositionsOfSameIssuer_NotOtherIssuers()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var marker = Marker();
        var sector = $"Sector{marker}";
        var emitentId = ReliabilitySeed.NewEmitentId();
        var universe = new BondUniverseRepository(Conn);

        var (firstInstrumentId, firstPositionId, firstIsin) = await SeedPositionAsync(accountId, sector, 1000m, 40m);
        var (_, secondPositionId, secondIsin) = await SeedPositionAsync(accountId, sector, 1000m, 40m);
        var (_, otherPositionId, otherIsin) = await SeedPositionAsync(accountId, sector, 1000m, 40m);
        var bonds = new[] { ($"DA{marker}", firstIsin), ($"DB{marker}", secondIsin), ($"DC{marker}", otherIsin) };
        await universe.UpsertSnapshotBatchAsync(bonds.Select(b => Bond(b.Item1, b.Item2, sector, 0.0200m)).ToList());
        foreach (var (secid, isin) in bonds.Take(2))
        {
            // два выпуска ОДНОГО эмитента, оба в банке с emitent_id (карточки без флагов: событие — отдельной строкой)
            await ReliabilitySeed.AttachIssuerAsync(Conn, secid, emitentId, null);
            await ReliabilitySeed.SeedCardAsync(Conn, secid, isin, emitentId, hasDefault: false, hasTechnicalDefault: false);
        }

        await ReliabilitySeed.SeedHealthyIssuerAsync(Conn, bonds[2].Item1, bonds[2].Item2);
        await new SignalRepository(Conn).InsertAsync(new Signal
        {
            AccountId = accountId, Type = SignalType.IssuerDefaultEvent, Severity = SignalSeverity.Critical, InstrumentId = firstInstrumentId,
            EventKey = Signal.BuildKey(SignalType.IssuerDefaultEvent, $"{emitentId}:default", null), Subject = "Эмитент",
            FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        });

        var body = await GetVerdictsAsync(client);

        VerdictOf(body, firstPositionId).GetProperty("verdict").GetString().Should().Be("ConsiderExit");
        var second = VerdictOf(body, secondPositionId);
        second.GetProperty("verdict").GetString().Should().Be("ConsiderExit", "дефолт относится ко всем выпускам эмитента");
        Reasons(second).Should().Contain(r => r.Contains("дефолт"));
        VerdictOf(body, otherPositionId).GetProperty("verdict").GetString().Should().NotBe("ConsiderExit");
    }

    [Fact]
    public async Task Verdicts_CoverAllOpenPositions_InOneCall()
    {
        await SeedYieldCurveAsync();
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var positionIds = new List<ulong>();
        for (var i = 0; i < 4; i++)
        {
            positionIds.Add((await SeedPositionAsync(accountId, $"S{Marker()}", 1000m, 40m)).PositionId);
        }

        var body = await GetVerdictsAsync(client);

        body.GetProperty("verdicts").EnumerateArray().Select(v => v.GetProperty("positionId").GetUInt64())
            .Should().BeEquivalentTo(positionIds);
    }
}
