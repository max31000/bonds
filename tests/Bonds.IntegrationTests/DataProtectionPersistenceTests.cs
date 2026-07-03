using System.Security.Cryptography;
using Bonds.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Plan/13 часть A, критерий приёмки #1: "рестарт процесса не ломает расшифровку". Корневой баг
/// (plan/13 "Проблема") — <c>services.AddDataProtection()</c> без явного
/// <c>PersistKeysToFileSystem</c> пишет ключи в domain-профиль контейнера
/// (<c>/root/.aspnet/DataProtection-Keys</c>), который живёт только в текущем контейнерном слое и
/// теряется при "docker stop &amp;&amp; rm &amp;&amp; run" на каждом деплое — сохранённый в БД токен
/// T-Invest перестаёт расшифровываться молча (<see cref="TInvestTokenProvider.GetTokenAsync"/>
/// возвращает null и логирует warning вместо падения).
/// <para>
/// Юнит-тест "провайдер сконфигурирован с file-system-персистом" был бы малополезен (план прямо
/// это оговаривает) — вместо этого здесь эмулируется сам сценарий "рестарт контейнера": два
/// независимых <see cref="ServiceProvider"/> с ОДИНАКОВЫМ каталогом ключей на диске (temp dir —
/// в реальности это смонтированный volume), но БЕЗ разделяемого процесса/памяти между ними —
/// ровно то, что происходит при пересоздании контейнера с volume-маунтом.
/// </para>
/// </summary>
public class DataProtectionPersistenceTests
{
    private const string ApplicationName = "bonds-api";

    private static IDataProtectionProvider BuildProviderWithPersistedKeys(string keysPath)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IDataProtectionProvider>();
    }

    [Fact]
    public void Protect_ThenRebuildServiceProviderWithSameKeysPath_StillDecrypts()
    {
        // Общий каталог ключей на диске — аналог volume, смонтированного в оба "запуска контейнера".
        var keysPath = Path.Combine(Path.GetTempPath(), $"bonds-dp-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysPath);

        try
        {
            const string plainToken = "t.fake-readonly-token-for-persistence-test";

            // "Запуск 1" — сохраняем токен (аналог PUT /api/settings/tinvest-token).
            var providerBeforeRestart = BuildProviderWithPersistedKeys(keysPath);
            var protectorBeforeRestart = providerBeforeRestart.CreateProtector(TInvestTokenProvider.ProtectorPurpose);
            var encrypted = protectorBeforeRestart.Protect(plainToken);

            // "Рестарт контейнера" — новый ServiceProvider (новый процесс), тот же каталог ключей
            // на диске (тот же volume), без общей памяти/singleton с первым.
            var providerAfterRestart = BuildProviderWithPersistedKeys(keysPath);
            var protectorAfterRestart = providerAfterRestart.CreateProtector(TInvestTokenProvider.ProtectorPurpose);

            var decrypted = protectorAfterRestart.Unprotect(encrypted);

            decrypted.Should().Be(plainToken, "ключи персистированы на диске — расшифровка должна пережить пересоздание процесса/контейнера");
        }
        finally
        {
            Directory.Delete(keysPath, recursive: true);
        }
    }

    [Fact]
    public void Protect_WithDifferentKeysPaths_FailsToDecrypt_DemonstratingTheOriginalBug()
    {
        // Контрольный тест: без общего каталога ключей (аналог `AddDataProtection()` без
        // PersistKeysToFileSystem — каждый контейнер получает собственный эфемерный ключевой
        // материал) расшифровка после "рестарта" падает — именно так и деградировал синк до фикса.
        var keysPathBefore = Path.Combine(Path.GetTempPath(), $"bonds-dp-keys-before-{Guid.NewGuid():N}");
        var keysPathAfter = Path.Combine(Path.GetTempPath(), $"bonds-dp-keys-after-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysPathBefore);
        Directory.CreateDirectory(keysPathAfter);

        try
        {
            const string plainToken = "t.fake-readonly-token-for-persistence-test";

            var providerBeforeRestart = BuildProviderWithPersistedKeys(keysPathBefore);
            var encrypted = providerBeforeRestart.CreateProtector(TInvestTokenProvider.ProtectorPurpose).Protect(plainToken);

            var providerAfterRestart = BuildProviderWithPersistedKeys(keysPathAfter);
            var protectorAfterRestart = providerAfterRestart.CreateProtector(TInvestTokenProvider.ProtectorPurpose);

            var act = () => protectorAfterRestart.Unprotect(encrypted);

            act.Should().Throw<CryptographicException>();
        }
        finally
        {
            Directory.Delete(keysPathBefore, recursive: true);
            Directory.Delete(keysPathAfter, recursive: true);
        }
    }
}
