using Bonds.Infrastructure.Repositories;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace Bonds.Tests.Capital;

/// <summary>Задача 62: какие ошибки MySQL считаются гонкой первого посева (откат + повторное чтение, а не 500). Сам повтор — в интеграционных тестах.</summary>
public class CapitalBasketRepositoryRaceTests
{
    [Theory]
    [InlineData(MySqlErrorCode.DuplicateKeyEntry, true)]
    [InlineData(MySqlErrorCode.LockDeadlock, true)]
    [InlineData(MySqlErrorCode.LockWaitTimeout, false)]
    [InlineData(MySqlErrorCode.ParseError, false)]
    public void IsSeedRaceError_DuplicateKeyAndDeadlockOnly(MySqlErrorCode code, bool expected) =>
        CapitalBasketRepository.IsSeedRaceError(code).Should().Be(expected);
}
