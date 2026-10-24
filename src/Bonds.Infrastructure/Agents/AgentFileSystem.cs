using System.Runtime.InteropServices;

namespace Bonds.Infrastructure.Agents;

/// <summary>
/// Права на файловой системе для изоляции процесса агента (задача 68, ревью M1). API работает от root, CLI — от непривилегированного <c>agent</c> (uid 10001):
/// <list type="bullet">
/// <item>корень запусков (<c>/var/lib/bonds-agent</c>) — root, 0711: агент может войти в СВОЙ каталог по имени, но не перечислить соседние;</item>
/// <item>каталог запуска (<c>run-&lt;guid&gt;</c>) — 0700, владелец uid агента (создаётся root'ом и передаётся <c>chown</c> через libc);</item>
/// <item>MCP-конфиг с bearer запуска — 0600, тот же владелец;</item>
/// <item>ключи DataProtection — 0700 root (<see cref="EnsureOwnerOnlyDirectory"/>): процесс агента их не читает.</item>
/// </list>
/// На Windows (локальная разработка) права и владелец не меняются — только создание каталогов/файлов.
/// </summary>
public static class AgentFileSystem
{
    public const string RunDirPrefix = "run-";

    /// <summary>rwx--x--x: владелец (root) — всё, остальные — только проход по известному имени.</summary>
    public const UnixFileMode RunRootMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>rwx------.</summary>
    public const UnixFileMode OwnerOnlyDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>rw-------.</summary>
    public const UnixFileMode OwnerOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Создаёт (если нет) корень каталогов запусков и выставляет 0711 (Unix).</summary>
    public static void EnsureRunRoot(string root)
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, RunRootMode);
    }

    /// <summary>
    /// Каталог (создаёт, если нет) доступен только владельцу: 0700 (Unix). Для ключей DataProtection — процесс агента (другой uid) не может в него войти.
    /// Возвращает итоговый режим (на Windows — null).
    /// </summary>
    public static UnixFileMode? EnsureOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) return null;
        File.SetUnixFileMode(path, OwnerOnlyDirMode);
        return File.GetUnixFileMode(path);
    }

    /// <summary>Удаляет каталоги прошлых запусков (<c>run-*</c>), оставшиеся после падения/рестарта API. Возвращает число удалённых.</summary>
    public static int CleanupStaleRunDirectories(string root)
    {
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root, RunDirPrefix + "*"))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Следующий старт попробует снова; содержимое — только MCP-конфиг с уже недействительным bearer (токены живут в памяти процесса).
            }
        }

        return removed;
    }

    /// <summary>
    /// Каталог нового запуска внутри <paramref name="root"/>: 0700; при <paramref name="ownerUid"/> — передаётся этому uid/gid (Linux, API от root).
    /// </summary>
    public static string CreateRunDirectory(string root, int? ownerUid, int? ownerGid)
    {
        var path = Path.Combine(root, RunDirPrefix + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return path;
        }

        Directory.CreateDirectory(path, OwnerOnlyDirMode);
        if (ownerUid is { } uid) Chown(path, uid, ownerGid ?? uid);
        return path;
    }

    /// <summary>Новый файл (не перезаписывает) с режимом 0600; при <paramref name="ownerUid"/> — владелец этот uid/gid.</summary>
    public static async Task WritePrivateFileAsync(string path, string content, int? ownerUid, int? ownerGid, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFileMode;
        await using (var stream = new FileStream(path, options))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(content.AsMemory(), ct);
        }

        if (!OperatingSystem.IsWindows() && ownerUid is { } uid) Chown(path, uid, ownerGid ?? uid);
    }

    /// <summary><c>chown(2)</c> через libc (в .NET 8 нет управляемого API). Ошибка — <see cref="IOException"/> с errno.</summary>
    public static void Chown(string path, int uid, int gid)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("chown — только Unix");
        if (NativeMethods.chown(path, (uint)uid, (uint)gid) != 0)
            throw new IOException($"chown({uid}:{gid}) failed, errno {Marshal.GetLastPInvokeError()}");
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // DllImport вместо LibraryImport: без unsafe-кода в сборке
        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int chown(string path, uint owner, uint group);
#pragma warning restore SYSLIB1054
    }
}
