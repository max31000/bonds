using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Services;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;

namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Реализация <see cref="ITInvestPortfolioClient"/> над официальным gRPC SDK
/// <c>Tinkoff.InvestApi</c>. Контракт верифицирован отражением сборки SDK 0.6.22.1
/// (см. README.md в этом каталоге для деталей §12.2). Только облигации (InstrumentType
/// "bond") интересны этому продукту — другие типы инструментов отфильтровываются здесь же,
/// чтобы Bonds.Core не знал о деталях контракта T-Invest.
/// <para>
/// <b>Токен резолвится лениво через <see cref="ITInvestTokenProvider"/></b>, а не передаётся
/// готовым <c>InvestApiClient</c> через DI (как было до пересмотра при подготовке к этапу 10) —
/// токен хранится ТОЛЬКО в БД на пользователя (<c>PUT /api/settings/tinvest-token</c>), без
/// ENV-фолбэка (см. <see cref="ITInvestTokenProvider"/> doc-comment и `BACKEND_DECISIONS.md`,
/// решение про single-account-only хранение). gRPC-клиент создаётся через
/// <c>InvestApiClientFactory.Create(InvestApiSettings)</c> один раз на скоуп (на цикл синка) и
/// кэшируется в этом экземпляре — пересоздавать канал на каждый вызов было бы избыточно, а сам
/// экземпляр класса живёт ровно один scoped-вызов (один цикл синка/один HTTP-запрос).
/// </para>
/// </summary>
public sealed class TInvestPortfolioClient : ITInvestPortfolioClient
{
    private readonly ITInvestTokenProvider _tokenProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TInvestPortfolioClient> _logger;
    private InvestApiClient? _client;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    public TInvestPortfolioClient(
        ITInvestTokenProvider tokenProvider,
        IConfiguration configuration,
        ILogger<TInvestPortfolioClient> logger)
    {
        _tokenProvider = tokenProvider;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Резолвит токен (БД на пользователя — единственный источник, см. doc-comment класса) и
    /// лениво создаёт gRPC-клиент. Бросает <see cref="InvalidOperationException"/> с понятным
    /// сообщением, если токен не задан — <c>BondSyncService</c>/<c>SyncCycleService</c> уже умеют
    /// деградировать на ошибке одного шага синка без падения всего цикла (spec §4.4).
    /// </summary>
    private async Task<InvestApiClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;

        await _clientLock.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;

            var token = await _tokenProvider.GetTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "T-Invest token is not configured. Set it via PUT /api/settings/tinvest-token before syncing.");
            }

            _client = InvestApiClientFactory.Create(new InvestApiSettings
            {
                AccessToken = token,
                AppName = "bonds-portfolio-analytics",
                Sandbox = _configuration.GetValue<bool?>("TInvest:Sandbox") ?? false,
            });
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    public async Task<string?> GetPrimaryAccountIdAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var response = await client.Users.GetAccountsAsync(new GetAccountsRequest(), cancellationToken: ct);
        var account = response.Accounts.FirstOrDefault(a => a.Status == AccountStatus.Open);
        if (account is null)
        {
            _logger.LogWarning("T-Invest: no open account found for configured token");
            return null;
        }

        return account.Id;
    }

    public async Task<TInvestPortfolio> GetPortfolioAsync(string accountId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var response = await client.Operations.GetPortfolioAsync(
            new PortfolioRequest { AccountId = accountId, Currency = PortfolioRequest.Types.CurrencyRequest.Rub },
            cancellationToken: ct);

        var result = new List<TInvestPortfolioPosition>();
        var funds = new List<TInvestPortfolioPosition>();
        foreach (var position in response.Positions)
        {
            // InstrumentType приходит как нижнерегистровая строка протокола ("bond", "share", ...) —
            // не enum (verified §12.2, см. README.md). Облигации — основной домен; фонды ("etf", задача 51) —
            // отдельный домен; акции/валюта по-прежнему пропускаются.
            var isBond = string.Equals(position.InstrumentType, "bond", StringComparison.OrdinalIgnoreCase);
            var isFund = string.Equals(position.InstrumentType, "etf", StringComparison.OrdinalIgnoreCase);
            if (!isBond && !isFund)
            {
                continue;
            }

            var mapped = new TInvestPortfolioPosition
            {
                Figi = position.Figi,
                InstrumentUid = position.InstrumentUid,
                Quantity = position.Quantity.ToDecimal(),
                AveragePositionPrice = position.AveragePositionPrice.ToDecimal(),
                CurrentPrice = position.CurrentPrice.ToNullableDecimal(),
                CurrentNkd = position.CurrentNkd.ToNullableDecimal(),
            };
            (isBond ? result : funds).Add(mapped);
        }

        return new TInvestPortfolio
        {
            Bonds = result,
            Funds = funds,
            CashRub = response.TotalAmountCurrencies.ToNullableDecimal(),
            TotalPortfolioRub = response.TotalAmountPortfolio.ToNullableDecimal(),
        };
    }

    public async Task<IReadOnlyList<TInvestOperation>> GetOperationsAsync(string accountId, DateTime? from, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var result = new List<TInvestOperation>();
        string? cursor = null;

        // GetOperationsByCursor — пагинированный курсорный эндпоинт (предпочтительнее устаревшего
        // GetOperations без курсора, verified §12.2): поддерживает инкрементальный синк через
        // параметр From и постраничный обход через Cursor/HasNext.
        do
        {
            var request = new GetOperationsByCursorRequest
            {
                AccountId = accountId,
                From = from.HasValue ? Timestamp.FromDateTime(DateTime.SpecifyKind(from.Value, DateTimeKind.Utc)) : null,
                Limit = 1000,
            };
            if (cursor is not null)
            {
                request.Cursor = cursor;
            }

            var response = await client.Operations.GetOperationsByCursorAsync(request, cancellationToken: ct);

            foreach (var item in response.Items)
            {
                // Только исполненные операции — отменённые/в обработке не должны попадать в журнал
                // истины для XIRR (spec §6.9).
                if (item.State != OperationState.Executed)
                {
                    continue;
                }

                result.Add(new TInvestOperation
                {
                    Id = item.Id,
                    OperationType = item.Type.ToString(),
                    InstrumentKind = ResolveInstrumentKind(item.InstrumentKind, item.InstrumentType),
                    Figi = string.IsNullOrEmpty(item.Figi) ? null : item.Figi,
                    InstrumentUid = string.IsNullOrEmpty(item.InstrumentUid) ? null : item.InstrumentUid,
                    Date = item.Date.ToDateTime(),
                    PaymentRub = item.Payment.ToDecimal(),
                    Quantity = item.Quantity == 0 ? null : item.Quantity,
                    AccruedInterest = item.AccruedInt.ToNullableDecimal(),
                });
            }

            cursor = response.HasNext ? response.NextCursor : null;
        } while (!string.IsNullOrEmpty(cursor));

        return result;
    }

    /// <summary>
    /// Тип инструмента операции в виде имени enum <c>Tinkoff.InvestApi.V1.InstrumentType</c>
    /// (Bond/Share/Currency/Etf/...; имена сверены рефлексией по сборке SDK 0.6.22). Если enum-поле
    /// <c>InstrumentKind</c> не заполнено, берём строковое <c>InstrumentType</c> ("bond", "share"...)
    /// и нормализуем регистр. Null — брокер тип не указал (операции по счёту: комиссии, пополнения).
    /// </summary>
    internal static string? ResolveInstrumentKind(Tinkoff.InvestApi.V1.InstrumentType kind, string? rawType)
    {
        if (kind != Tinkoff.InvestApi.V1.InstrumentType.Unspecified) return kind.ToString();
        if (string.IsNullOrWhiteSpace(rawType)) return null;
        var t = rawType.Trim();
        return char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant();
    }

    public async Task<string?> GetIsinByFigiAsync(string figi, CancellationToken ct = default)
    {
        try
        {
            var client = await GetClientAsync(ct);
            var response = await client.Instruments.BondByAsync(
                new InstrumentRequest { IdType = InstrumentIdType.Figi, Id = figi }, cancellationToken: ct);
            return string.IsNullOrEmpty(response.Instrument.Isin) ? null : response.Instrument.Isin;
        }
        catch (Grpc.Core.RpcException ex)
        {
            _logger.LogWarning(ex, "T-Invest: failed to resolve ISIN for FIGI {Figi}", figi);
            return null;
        }
    }

    public async Task<TInvestTariffInfo?> GetUserTariffAsync(CancellationToken ct = default)
    {
        try
        {
            var client = await GetClientAsync(ct);
            var response = await client.Users.GetInfoAsync(new GetInfoRequest(), cancellationToken: ct);
            if (string.IsNullOrEmpty(response.Tariff))
            {
                return null;
            }

            return new TInvestTariffInfo
            {
                Tariff = response.Tariff,
                PremStatus = response.PremStatus,
            };
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException)
        {
            // InvalidOperationException — токен не настроен (см. GetClientAsync); RpcException —
            // сбой gRPC. Тариф — необязательный контекст для UI, не должен ронять GET /api/settings.
            _logger.LogWarning(ex, "T-Invest: failed to fetch user tariff");
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, TInvestQuote>> GetQuotesAsync(IReadOnlyCollection<string> figis, CancellationToken ct = default)
    {
        var result = new Dictionary<string, TInvestQuote>();
        if (figis.Count == 0) return result;

        var client = await GetClientAsync(ct);
        var lastPricesRequest = new GetLastPricesRequest();
        // InstrumentId (не устаревший Figi) принимает FIGI как валидное значение в этом контракте —
        // используем его, чтобы не зависеть от deprecated поля (verified §12.2, см. README.md).
        lastPricesRequest.InstrumentId.AddRange(figis);
        var lastPrices = await client.MarketData.GetLastPricesAsync(lastPricesRequest, cancellationToken: ct);

        var lastByFigi = lastPrices.LastPrices.ToDictionary(p => p.Figi, p => p.Price.ToNullableDecimal());

        foreach (var figi in figis)
        {
            decimal? bestBid = null, bestAsk = null;
            try
            {
                var orderBook = await client.MarketData.GetOrderBookAsync(
                    new GetOrderBookRequest { InstrumentId = figi, Depth = 1 }, cancellationToken: ct);
                bestBid = orderBook.Bids.Count > 0 ? orderBook.Bids[0].Price.ToNullableDecimal() : null;
                bestAsk = orderBook.Asks.Count > 0 ? orderBook.Asks[0].Price.ToNullableDecimal() : null;
            }
            catch (Grpc.Core.RpcException ex)
            {
                // Стакан может быть недоступен (бумага не торгуется сегодня и т.п.) — это не повод
                // валить весь синк котировок, последняя цена всё равно может быть полезна.
                _logger.LogWarning(ex, "T-Invest: order book unavailable for {Figi}", figi);
            }

            result[figi] = new TInvestQuote
            {
                Figi = figi,
                LastPrice = lastByFigi.GetValueOrDefault(figi),
                BestBid = bestBid,
                BestAsk = bestAsk,
            };
        }

        return result;
    }

    public async Task<IReadOnlyList<TInvestBondRiskLevel>> GetBondRiskLevelsAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var response = await client.Instruments.BondsAsync(
            new InstrumentsRequest { InstrumentStatus = InstrumentStatus.Base }, cancellationToken: ct);

        var result = new List<TInvestBondRiskLevel>(response.Instruments.Count);
        foreach (var bond in response.Instruments)
        {
            if (string.IsNullOrWhiteSpace(bond.Isin)) continue;
            result.Add(new TInvestBondRiskLevel(bond.Isin.Trim(), MapRiskLevel(bond.RiskLevel)));
        }

        return result;
    }

    /// <summary>
    /// Протобуф-enum <c>RiskLevel</c> (UNSPECIFIED=0, LOW=1, MODERATE=2, HIGH=3) → доменный
    /// <see cref="Bonds.Core.Models.BrokerRiskLevel"/>; неизвестное значение (новые версии API) → Unspecified.
    /// </summary>
    internal static Bonds.Core.Models.BrokerRiskLevel MapRiskLevel(RiskLevel level) => level switch
    {
        RiskLevel.Low => Bonds.Core.Models.BrokerRiskLevel.Low,
        RiskLevel.Moderate => Bonds.Core.Models.BrokerRiskLevel.Moderate,
        RiskLevel.High => Bonds.Core.Models.BrokerRiskLevel.High,
        _ => Bonds.Core.Models.BrokerRiskLevel.Unspecified,
    };

    public async Task<TInvestFundInfo?> GetFundInfoAsync(string figi, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        Etf etf;
        try
        {
            var response = await client.Instruments.EtfByAsync(
                new InstrumentRequest { IdType = InstrumentIdType.Figi, Id = figi }, cancellationToken: ct);
            etf = response.Instrument;
        }
        catch (Grpc.Core.RpcException ex)
        {
            _logger.LogWarning(ex, "T-Invest: failed to resolve fund for FIGI {Figi}", figi);
            return null;
        }

        if (string.IsNullOrWhiteSpace(etf.Isin)) return null;

        decimal? ter = null;
        string? company = null;
        if (!string.IsNullOrEmpty(etf.AssetUid))
        {
            try
            {
                var asset = await client.Instruments.GetAssetByAsync(
                    new AssetRequest { Id = etf.AssetUid }, cancellationToken: ct);
                company = string.IsNullOrWhiteSpace(asset.Asset?.Brand?.Company) ? null : asset.Asset.Brand.Company;
                var raw = asset.Asset?.Security?.Etf?.TotalExpense.ToNullableDecimal();
                if (raw is not null)
                {
                    ter = TInvestNumeric.ExpenseRatioToFraction(raw.Value);
                    if (ter is null)
                    {
                        _logger.LogWarning(
                            "T-Invest: implausible TotalExpense {Raw} for fund {Figi} — TER ignored", raw, figi);
                    }
                }
            }
            catch (Grpc.Core.RpcException ex)
            {
                // TER — необязательный атрибут справочника: сбой GetAssetBy не должен ронять резолв фонда.
                _logger.LogWarning(ex, "T-Invest: failed to fetch asset (TER) for fund {Figi}", figi);
            }
        }

        return new TInvestFundInfo
        {
            Isin = etf.Isin.Trim(),
            Ticker = string.IsNullOrWhiteSpace(etf.Ticker) ? null : etf.Ticker.Trim(),
            Name = string.IsNullOrWhiteSpace(etf.Name) ? null : etf.Name,
            FocusType = string.IsNullOrWhiteSpace(etf.FocusType) ? null : etf.FocusType,
            ManagementCompany = company,
            TotalExpenseFraction = ter,
        };
    }

    public async Task<IReadOnlyList<TInvestFundDistribution>> GetFundDistributionsAsync(
        string figi, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        try
        {
            var response = await client.Instruments.GetDividendsAsync(new GetDividendsRequest
            {
                InstrumentId = figi,
                From = Timestamp.FromDateTime(DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)),
                To = Timestamp.FromDateTime(DateTime.SpecifyKind(to.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)),
            }, cancellationToken: ct);

            var result = new List<TInvestFundDistribution>();
            foreach (var d in response.Dividends)
            {
                if (d.RecordDate is null || d.RecordDate.Seconds == 0) continue;
                var amount = d.DividendNet.ToDecimal(); // рубли за пай, конверсии нет
                if (amount <= 0m) continue;
                DateOnly? payment = d.PaymentDate is { Seconds: > 0 } p ? DateOnly.FromDateTime(p.ToDateTime()) : null;
                result.Add(new TInvestFundDistribution(DateOnly.FromDateTime(d.RecordDate.ToDateTime()), payment, amount));
            }

            return result;
        }
        catch (Grpc.Core.RpcException ex)
        {
            _logger.LogWarning(ex, "T-Invest: failed to fetch distributions for fund {Figi}", figi);
            return [];
        }
    }
}
