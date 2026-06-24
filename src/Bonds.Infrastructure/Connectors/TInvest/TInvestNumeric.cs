using Tinkoff.InvestApi.V1;

namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Конвертация числовых типов T-Invest gRPC-контракта (<see cref="MoneyValue"/>, <see cref="Quotation"/>)
/// в <see cref="decimal"/>. Оба типа кодируют число как целая часть (Units) + дробная в нанодолях
/// (Nano, 10^-9) — это документированный формат protobuf-контракта T-Invest (verified §12.2,
/// см. README коннектора), SDK не предоставляет готовый helper, поэтому реализуем сами.
/// </summary>
public static class TInvestNumeric
{
    private const decimal NanoScale = 1_000_000_000m;

    public static decimal ToDecimal(this MoneyValue? value)
        => value is null ? 0m : value.Units + value.Nano / NanoScale;

    public static decimal ToDecimal(this Quotation? value)
        => value is null ? 0m : value.Units + value.Nano / NanoScale;

    public static decimal? ToNullableDecimal(this MoneyValue? value)
        => value is null ? null : value.ToDecimal();

    public static decimal? ToNullableDecimal(this Quotation? value)
        => value is null ? null : value.ToDecimal();
}
