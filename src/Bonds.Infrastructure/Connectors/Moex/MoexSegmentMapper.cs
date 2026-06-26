namespace Bonds.Infrastructure.Connectors.Moex;

public static class MoexSegmentMapper
{
    public static string? MapTypeToSegment(string? typeCode) => typeCode switch
    {
        "ofz_bond" => "Гособлигации",
        "subfederal_bond" or "municipal_bond" => "Муниципальные",
        "corporate_bond" or "exchange_bond" => "Корпоративные",
        _ => null,
    };
}
