using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Moq;
using Xunit;

namespace Bonds.Tests.Sync;

/// <summary>
/// Причина промаха курса. Регрессия 2026-10: у EUR на MOEX CETS сделок нет с 2024 (CLOSE = 0) — история не заполнится никогда, а
/// классификация «истории ещё нет, самолечится» навсегда держала JournalSynced = false и блокировала бэкфилл истории стоимости.
/// </summary>
public class CurrencyRateLookupTests
{
    private readonly Mock<ICurrencyRepository> _currencies = new();
    private readonly Mock<ICurrencyMarketDataRepository> _market = new();

    private CurrencyRateLookup Create(ulong currencyId)
    {
        _currencies.Setup(r => r.GetByIsoAsync("eur")).ReturnsAsync(new Currency { Id = currencyId, IsoCode = "EUR" });
        _market.Setup(r => r.GetRatesAsync(currencyId, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CurrencyRatePoint>());
        return new CurrencyRateLookup(_currencies.Object, _market.Object);
    }

    [Fact]
    public async Task EmptyHistory_NotYetAttempted_IsSelfHealing()
    {
        var id = 900_000UL + (ulong)Random.Shared.Next(1, 99_999);
        CurrencyRateRefresher.EmptyAttemptedOn.TryRemove(id, out _);

        (await Create(id).ClassifyMissAsync("eur")).Should().Be(RateMissKind.NoHistoryYet);
    }

    [Fact]
    public async Task EmptyHistory_RefresherAlreadyGotNothing_IsNotSelfHealing()
    {
        var id = 800_000UL + (ulong)Random.Shared.Next(1, 99_999);
        CurrencyRateRefresher.EmptyAttemptedOn[id] = DateOnly.FromDateTime(DateTime.UtcNow);

        (await Create(id).ClassifyMissAsync("eur")).Should().Be(RateMissKind.OutsideHistory);
    }
}
