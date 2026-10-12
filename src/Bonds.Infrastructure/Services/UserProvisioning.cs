using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using MySqlConnector;

namespace Bonds.Infrastructure.Services;

/// <summary>
/// Finds or creates the local <c>users</c> row for an mvv42 user (key: Telegram id — the shared auth's
/// <c>sub</c> is never used as a local id) and runs the Account onboarding the old Telegram login did:
/// the rest of the API resolves "the" account via <see cref="IAccountRepository.GetPrimaryAccountIdAsync"/>
/// (single-user product, spec §2), so without it sync/positions would never find one.
/// Callers must have checked that the Telegram id is the owner (bonds is single-owner).
/// </summary>
public sealed class UserProvisioning(IUserRepository users, IAccountRepository accounts)
{
    // One API instance: serialises the first-request burst so onboarding cannot create two primary accounts.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ulong> GetOrCreateAsync(long telegramId, string? name, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var displayName = string.IsNullOrWhiteSpace(name) ? null : name;
            ulong userId;
            if (await users.GetByTelegramIdAsync(telegramId) is { } existing)
            {
                userId = existing.Id;
                // The token carries the current Telegram name: keep the local row in step (callers cache the
                // result for 10 minutes, so this runs at most that often). Only the name travels in the token.
                if (displayName is not null && existing.FirstName != displayName)
                {
                    existing.FirstName = displayName;
                    await users.UpdateAsync(existing);
                }
            }
            else
            {
                try
                {
                    userId = await users.CreateAsync(new User
                    {
                        TelegramId = telegramId,
                        FirstName = displayName,
                    });
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
                {
                    // Another instance/process inserted the same telegram_id first (uq_users_telegram_id).
                    userId = (await users.GetByTelegramIdAsync(telegramId))?.Id
                             ?? throw new InvalidOperationException(
                                 $"users.telegram_id={telegramId}: duplicate key but no row", ex);
                }
            }

            // Same onboarding as the former TelegramLogin (GetPrimaryAccountIdAsync is global, not per user);
            // checked on every provisioning so an existing user without an account self-heals.
            if (await accounts.GetPrimaryAccountIdAsync() is null)
                await accounts.CreateAsync(new Account { UserId = userId });

            return userId;
        }
        finally
        {
            Gate.Release();
        }
    }
}
