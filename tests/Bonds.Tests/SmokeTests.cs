using Xunit;

namespace Bonds.Tests;

/// <summary>
/// Smoke-тест этапа 01: подтверждает, что тестовый проект собирается и xUnit-раннер работает.
/// Реальные юнит-тесты Calculation Engine появятся в этапе 05.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Arithmetic_Sanity_Check()
    {
        Assert.Equal(4, 2 + 2);
    }
}
