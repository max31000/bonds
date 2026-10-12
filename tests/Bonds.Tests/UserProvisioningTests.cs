using System.Reflection;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Services;
using FluentAssertions;
using Moq;
using MySqlConnector;
using Xunit;

namespace Bonds.Tests;

public class UserProvisioningTests
{
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IAccountRepository> _accounts = new();

    private UserProvisioning Sut() => new(_users.Object, _accounts.Object);

    [Fact]
    public async Task ExistingUser_IsReturnedByTelegramId_WithoutCreating()
    {
        _users.Setup(u => u.GetByTelegramIdAsync(42)).ReturnsAsync(new User { Id = 7, TelegramId = 42 });
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync(3UL);

        var id = await Sut().GetOrCreateAsync(42, "Owner");

        id.Should().Be(7UL);
        _users.Verify(u => u.CreateAsync(It.IsAny<User>()), Times.Never);
        _accounts.Verify(a => a.CreateAsync(It.IsAny<Account>()), Times.Never);
    }

    [Fact]
    public async Task ExistingUser_WithChangedTelegramName_IsUpdated()
    {
        var user = new User { Id = 7, TelegramId = 42, FirstName = "Старое Имя" };
        _users.Setup(u => u.GetByTelegramIdAsync(42)).ReturnsAsync(user);
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync(3UL);

        await Sut().GetOrCreateAsync(42, "Новое Имя");

        _users.Verify(u => u.UpdateAsync(It.Is<User>(x => x.Id == 7 && x.FirstName == "Новое Имя")), Times.Once);
    }

    [Fact]
    public async Task ExistingUser_WithSameOrEmptyName_IsNotUpdated()
    {
        _users.Setup(u => u.GetByTelegramIdAsync(42)).ReturnsAsync(new User { Id = 7, TelegramId = 42, FirstName = "Owner" });
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync(3UL);

        await Sut().GetOrCreateAsync(42, "Owner");
        await Sut().GetOrCreateAsync(42, "  ");

        _users.Verify(u => u.UpdateAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task NewUser_IsCreatedWithTelegramIdAndName_AndPrimaryAccountOnboarded()
    {
        _users.Setup(u => u.GetByTelegramIdAsync(42)).ReturnsAsync((User?)null);
        _users.Setup(u => u.CreateAsync(It.IsAny<User>())).ReturnsAsync(11UL);
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync((ulong?)null);

        var id = await Sut().GetOrCreateAsync(42, "Иван Петров");

        id.Should().Be(11UL);
        _users.Verify(u => u.CreateAsync(It.Is<User>(x => x.TelegramId == 42 && x.FirstName == "Иван Петров")), Times.Once);
        _accounts.Verify(a => a.CreateAsync(It.Is<Account>(x => x.UserId == 11UL)), Times.Once);
    }

    [Fact]
    public async Task DuplicateKeyOnCreate_RereadsTheRowInsertedConcurrently()
    {
        _users.SetupSequence(u => u.GetByTelegramIdAsync(42))
            .ReturnsAsync((User?)null)
            .ReturnsAsync(new User { Id = 9, TelegramId = 42 });
        _users.Setup(u => u.CreateAsync(It.IsAny<User>())).ThrowsAsync(DuplicateKey());
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync(1UL);

        var id = await Sut().GetOrCreateAsync(42, "Owner");

        id.Should().Be(9UL);
    }

    [Fact]
    public async Task OtherDatabaseErrors_AreNotSwallowed()
    {
        _users.Setup(u => u.GetByTelegramIdAsync(42)).ReturnsAsync((User?)null);
        _users.Setup(u => u.CreateAsync(It.IsAny<User>())).ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => Sut().GetOrCreateAsync(42, "Owner");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    /// <summary>MySqlConnector has no public constructor taking an error code: build the 1062 exception it throws.</summary>
    private static MySqlException DuplicateKey()
    {
        var ctor = typeof(MySqlException)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(c => c.GetParameters() is [{ ParameterType: var first }, ..] && first == typeof(MySqlErrorCode));
        var args = ctor.GetParameters()
            .Select(p => p.ParameterType == typeof(MySqlErrorCode) ? MySqlErrorCode.DuplicateKeyEntry
                : p.ParameterType == typeof(string) ? (object?)"Duplicate entry for key 'uq_users_telegram_id'"
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();
        var ex = (MySqlException)ctor.Invoke(args);
        ex.ErrorCode.Should().Be(MySqlErrorCode.DuplicateKeyEntry);
        return ex;
    }
}
