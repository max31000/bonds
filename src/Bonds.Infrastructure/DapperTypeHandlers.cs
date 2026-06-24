using System.Data;
using System.Runtime.CompilerServices;
using Dapper;

namespace Bonds.Infrastructure;

/// <summary>
/// Dapper TypeHandler для System.DateOnly. Dapper из коробки не поддерживает DateOnly,
/// нужно явно конвертировать в/из SQL DATE. Порт из cashpulse (Infrastructure/DapperTypeHandlers.cs).
/// </summary>
public class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value)
    {
        return value switch
        {
            DateTime dt => DateOnly.FromDateTime(dt),
            DateOnly d => d,
            string s => DateOnly.Parse(s),
            _ => DateOnly.FromDateTime(Convert.ToDateTime(value)),
        };
    }
}

/// <summary>Dapper TypeHandler для System.DateOnly? (nullable).</summary>
public class NullableDateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly?>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly? value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.HasValue
            ? value.Value.ToDateTime(TimeOnly.MinValue)
            : DBNull.Value;
    }

    public override DateOnly? Parse(object value)
    {
        if (value == null || value == DBNull.Value) return null;
        return value switch
        {
            DateTime dt => DateOnly.FromDateTime(dt),
            DateOnly d => d,
            string s => DateOnly.Parse(s),
            _ => DateOnly.FromDateTime(Convert.ToDateTime(value)),
        };
    }
}

/// <summary>
/// Dapper TypeHandler для enum'ов, хранимых в MySQL как ENUM(...) (приходят из MySqlConnector
/// как string, а не int). Сериализует/десериализует по имени значения enum'а.
/// </summary>
public class EnumStringTypeHandler<T> : SqlMapper.TypeHandler<T> where T : struct, Enum
{
    public override void SetValue(IDbDataParameter parameter, T value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString();
    }

    public override T Parse(object value)
    {
        return value switch
        {
            string s => Enum.Parse<T>(s, ignoreCase: true),
            T t => t,
            _ => Enum.Parse<T>(Convert.ToString(value) ?? string.Empty, ignoreCase: true),
        };
    }
}

/// <summary>Nullable вариант <see cref="EnumStringTypeHandler{T}"/>.</summary>
public class NullableEnumStringTypeHandler<T> : SqlMapper.TypeHandler<T?> where T : struct, Enum
{
    public override void SetValue(IDbDataParameter parameter, T? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.HasValue ? value.Value.ToString() : DBNull.Value;
    }

    public override T? Parse(object value)
    {
        if (value == null || value == DBNull.Value) return null;
        return value switch
        {
            string s => Enum.Parse<T>(s, ignoreCase: true),
            T t => t,
            _ => Enum.Parse<T>(Convert.ToString(value) ?? string.Empty, ignoreCase: true),
        };
    }
}

/// <summary>
/// Точка регистрации всех кастомных TypeHandler'ов Dapper. Вызывается явно из
/// DependencyInjection.AddInfrastructure (как в cashpulse) и дополнительно подстраховывается
/// [ModuleInitializer]: интеграционные тесты этого этапа конструируют репозитории напрямую,
/// минуя DI (см. tests/Bonds.IntegrationTests/*RepositoryTests.cs), поэтому регистрация должна
/// произойти при простой загрузке сборки Bonds.Infrastructure, а не только через явный вызов.
/// Register() остаётся идемпотентным (флаг + lock), поэтому повторный вызов из DI безопасен.
/// </summary>
public static class DapperTypeHandlers
{
    private static bool _registered;
    private static readonly object Lock = new();

#pragma warning disable CA2255 // намеренно: библиотека сама гарантирует регистрацию Dapper-хендлеров
                               // при загрузке, чтобы репозитории работали корректно независимо от того,
                               // вызывался ли явно AddInfrastructure (актуально для тестов, см. summary выше).
    [ModuleInitializer]
    public static void Register()
    {
        if (_registered) return;
        lock (Lock)
        {
            if (_registered) return;

            SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
            SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());

            // ВАЖНО: эти хендлеры покрывают только ЧТЕНИЕ (десериализацию ENUM(...)-строки MySQL в
            // C#-enum-свойство POCO) — это подтверждено и работает корректно. На ЗАПИСЬ Dapper для
            // enum-свойств, переданных как часть POCO/анонимного объекта параметров, использует
            // встроенный fast-path и шлёт значение как ЧИСЛО (Enum.GetUnderlyingType), полностью
            // игнорируя зарегистрированный ITypeHandler — это подтверждено эмпирически (general query
            // log MySQL показывал buffer "VALUES (0)" вместо "VALUES ('Fixed')"), независимо от
            // SqlMapper.RemoveTypeMap (он не помогает, т.к. typeMap и typeHandlers — разные словари).
            // Поэтому на запись во всех репозиториях enum-поля передаются явно через .ToString()
            // в параметрах запроса (см. *Repository.cs) — здесь регистрация остаётся только для чтения.
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<Bonds.Core.Models.CouponType>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<Bonds.Core.Models.OfferType>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<Bonds.Core.Models.MarketQuoteSource>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<Bonds.Core.Models.OperationType>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<Bonds.Core.Models.CashFlowType>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<Bonds.Core.Models.SignalType>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<Bonds.Core.Models.SignalSeverity>());

            SqlMapper.AddTypeHandler(new NullableEnumStringTypeHandler<Bonds.Core.Models.OfferType>());

            _registered = true;
        }
    }
#pragma warning restore CA2255
}
