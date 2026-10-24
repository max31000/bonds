using System.Globalization;
using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Xunit.Abstractions;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Execution;

/// <summary>
/// Задача 66, ОПЦИОНАЛЬНАЯ боевая проверка Freedom — запускает ВЛАДЕЛЕЦ (агенты и CI — никогда; исключена фильтром <c>Category!=Sandbox</c>). Сервис здесь заявок НЕ выставляет:
/// владелец сам ставит ОДНУ лимитную заявку в терминале Freedom (1 шт., лимит далеко от рынка), тест на РЕАЛЬНОМ <see cref="FreedomOrderGateway"/> находит её в списке заявок,
/// показывает разбор (stat → статус, oper, p, q, date → UTC, userOrderId), проверяет смещение пояса (<c>Freedom:OrderTimeUtcOffsetHours</c>: date − смещение ≈ момент
/// выставления), что РУЧНАЯ заявка (userOrderId не пуст и не в форме api) НЕ усыновляется сверкой по реквизитам, и, при <c>FREEDOM_LIVE_CANCEL=1</c>, снимает её через шлюз и
/// перечитывает статус.
/// <para>
/// Env: <c>FREEDOM_TRADE_PUBLIC_KEY</c>, <c>FREEDOM_TRADE_PRIVATE_KEY</c> (ключ с правом торговли), <c>FREEDOM_LIVE_ORDER_ID</c> (номер заявки из терминала),
/// опционально <c>FREEDOM_LIVE_PLACED_AT_UTC</c> (момент выставления, ISO UTC; по умолчанию — «сейчас», тогда запускать в пределах 2 мин после выставления),
/// <c>FREEDOM_LIVE_UTC_OFFSET_HOURS</c> (по умолчанию 3), <c>FREEDOM_LIVE_CANCEL=1</c>. Без ключей и номера тест ничего не делает. Вывод — только в консоль теста;
/// реальные ответы брокера в репозиторий НЕ коммитить (публичный репо).
/// </para>
/// </summary>
[Trait("Category", "Sandbox")]
public class FreedomLiveCheckTests
{
    private const ulong UserId = 1;
    private readonly ITestOutputHelper _out;

    public FreedomLiveCheckTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ManualTerminalOrder_ParsedReconciledAndCancelledByService()
    {
        var pub = Environment.GetEnvironmentVariable("FREEDOM_TRADE_PUBLIC_KEY");
        var priv = Environment.GetEnvironmentVariable("FREEDOM_TRADE_PRIVATE_KEY");
        var orderId = Environment.GetEnvironmentVariable("FREEDOM_LIVE_ORDER_ID")?.Trim();
        if (string.IsNullOrWhiteSpace(pub) || string.IsNullOrWhiteSpace(priv) || string.IsNullOrWhiteSpace(orderId))
        {
            _out.WriteLine("FREEDOM_TRADE_PUBLIC_KEY / FREEDOM_TRADE_PRIVATE_KEY / FREEDOM_LIVE_ORDER_ID не заданы — боевая проверка пропущена.");
            return;
        }

        var offsetHours = double.TryParse(Environment.GetEnvironmentVariable("FREEDOM_LIVE_UTC_OFFSET_HOURS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 3;
        var placedAt = DateTime.TryParse(
            Environment.GetEnvironmentVariable("FREEDOM_LIVE_PLACED_AT_UTC"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p)
            ? DateTime.SpecifyKind(p, DateTimeKind.Utc)
            : DateTime.UtcNow;

        var http = new HttpClient { BaseAddress = new Uri(new FreedomOptions().BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(FreedomApiClient.HttpClientName)).Returns(http);
        var store = new Mock<IUserCredentialStore>();
        store.Setup(s => s.GetSecretResultAsync(UserId, CredentialProviders.FreedomTrade)).ReturnsAsync(
            new CredentialSecretResult(CredentialReadOutcome.Ok, new Dictionary<string, string> { ["publicKey"] = pub, ["privateKey"] = priv }));
        // Ворота закрыты: размещение заявки этим тестом невозможно даже по ошибке (PostLimitOrderAsync откажет до сети).
        var gate = new Mock<IExecutionGateResolver>();
        gate.Setup(g => g.ResolveForBrokerAsync(UserId, BrokerIds.Freedom))
            .ReturnsAsync(ExecutionGate.Evaluate(false, ExecutionModes.Observe, false, false, ExecutionGate.NoFreedomTradeCredential));
        var gateway = new FreedomOrderGateway(
            store.Object, gate.Object, new FreedomApiClient(factory.Object), factory.Object, TimeProvider.System, NullLogger<FreedomOrderGateway>.Instance,
            Options.Create(new FreedomOptions { OrderTimeUtcOffsetHours = offsetHours }));

        var all = await gateway.GetOrdersAsync(UserId, activeOnly: false);
        _out.WriteLine($"getNotifyOrderJson: {all.Count} заявок");
        var mine = all.SingleOrDefault(o => o.BrokerOrderId == orderId);
        mine.Should().NotBeNull($"заявка {orderId} должна быть в списке заявок брокера");
        _out.WriteLine(
            $"order {mine!.BrokerOrderId}: status {mine.Status}, {mine.Side} {mine.LotsRequested} pc {mine.Ticker} @ {mine.LimitPrice}, executed {mine.LotsExecuted}, " +
            $"user_order_id {mine.ClientOrderId ?? "null"}, placedAtUtc {mine.PlacedAtUtc:O} (offset {offsetHours} h), now {DateTime.UtcNow:O}");
        mine.Status.Should().Be(TradeOrderStatuses.Submitted, "активная лимитная заявка, stat 10");
        mine.Side.Should().NotBeNull("oper 1/3");

        var orders = new InMemoryOrders();
        var row = new TradeOrder
        {
            PlanId = 1, LineNo = 1, Attempt = 1, ClientOrderId = FreedomOrderRules.NewUserOrderId(), Broker = BrokerIds.Freedom, Status = TradeOrderStatuses.New,
            LimitPrice = mine.LimitPrice!.Value, LotsRequested = mine.LotsRequested, Currency = mine.Currency ?? "USD", CreatedAt = placedAt, UpdatedAt = placedAt,
        };
        await orders.InsertAsync(row);
        var line = new TradePlanLine
        {
            LineNo = 1, Broker = BrokerIds.Freedom, InstrumentKey = mine.Ticker!, Ticker = mine.Ticker!, Side = mine.Side!, QuantityLots = mine.LotsRequested, LotSize = 1m,
            LimitPrice = mine.LimitPrice!.Value, Currency = row.Currency,
        };
        var venue = new FreedomExecutionVenue(
            gateway, gate.Object, Mock.Of<IAccountRepository>(), Mock.Of<IUserCredentialRepository>(), orders, NullLogger.Instance);
        var session = new VenueSession { Broker = BrokerIds.Freedom, BrokerAccountId = "live-check" };

        // Смещение пояса: date заявки (МСК) − смещение должно совпасть с моментом выставления (окно сверки по реквизитам — [−30 с, +2 мин]).
        mine.PlacedAtUtc.Should().NotBeNull("date заявки должна разбираться");
        (mine.PlacedAtUtc!.Value - placedAt).Duration().Should().BeLessThan(
            FreedomExecutionVenue.ReconcileWindowAfter, "date − смещение пояса = момент выставления; иначе смещение неверно");

        // Ручная заявка терминала/приложения (userOrderId вида «i-6:ios:…», «web:…») НЕ должна усыновляться сервисом по реквизитам; пустой/api — должна.
        var mayAdopt = FreedomOrderRules.MayBeOursByAttributes(mine.ClientOrderId, row.ClientOrderId, row.CreatedAt);
        var reconciled = await venue.StateAsync(session, UserId, row, line, true, CancellationToken.None);
        _out.WriteLine($"userOrderId {mine.ClientOrderId ?? "null"}: по правилам {(mayAdopt ? "может быть нашей" : "чужая (ручная)")}, сверка без id — " +
                       (reconciled is null ? "НЕ опознана" : $"опознана как {reconciled.BrokerOrderId} (по реквизитам: {reconciled.MatchedByAttributes})"));
        if (mayAdopt) reconciled?.BrokerOrderId.Should().Be(orderId, "опознана должна быть именно эта заявка");
        else reconciled.Should().BeNull("ручная заявка не должна усыновляться исполнителем");

        if (Environment.GetEnvironmentVariable("FREEDOM_LIVE_CANCEL") != "1")
        {
            _out.WriteLine("FREEDOM_LIVE_CANCEL не задан — заявка НЕ снята, снимите её в терминале.");
            return;
        }

        row.BrokerOrderId = orderId;
        await venue.CancelAsync(session, UserId, row, CancellationToken.None);
        GatewayOrderState? after = null;
        for (var i = 0; i < 10 && (after is null || TradeOrderStatuses.IsActive(after.Status)); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            after = await venue.StateAsync(session, UserId, row, line, false, CancellationToken.None);
        }

        _out.WriteLine($"после снятия: {after?.Status ?? "не найдена"}, executed {after?.LotsExecuted}");
        after.Should().NotBeNull();
        after!.Status.Should().Be(TradeOrderStatuses.Cancelled, "stat 31 — отменен");
    }
}
