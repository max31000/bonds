using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Agents;

/// <summary>Итог дочернего процесса агента: код выхода, вывод (обрезан по пределу), признак таймаута (процесс убит вместе с деревом).</summary>
public sealed record AgentProcessOutcome(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>Запуск дочернего процесса агента (отдельный интерфейс — чтобы раннер тестировался без настоящего CLI).</summary>
public interface IAgentProcessExecutor
{
    /// <summary>
    /// Запускает <paramref name="psi"/>, пишет <paramref name="stdin"/> и закрывает вход, ждёт выхода не дольше <paramref name="timeout"/>; по таймауту или отмене
    /// убивает ДЕРЕВО процессов. Отмена <paramref name="ct"/> пробрасывается после kill.
    /// </summary>
    Task<AgentProcessOutcome> RunAsync(ProcessStartInfo psi, string stdin, TimeSpan timeout, int maxOutputBytes, CancellationToken ct);
}

/// <summary>Реализация через <see cref="Process"/>: kill — <c>Kill(entireProcessTree: true)</c>. Вывод читается асинхронно и обрезается по пределу (память).</summary>
public sealed class AgentProcessExecutor : IAgentProcessExecutor
{
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);
    private readonly ILogger<AgentProcessExecutor> _logger;

    public AgentProcessExecutor(ILogger<AgentProcessExecutor> logger) => _logger = logger;

    public async Task<AgentProcessOutcome> RunAsync(ProcessStartInfo psi, string stdin, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, maxOutputBytes);
        var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, Math.Min(maxOutputBytes, 64 * 1024));

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            if (psi.RedirectStandardInput)
            {
                try
                {
                    await process.StandardInput.WriteAsync(stdin.AsMemory(), linked.Token);
                    await process.StandardInput.FlushAsync(linked.Token);
                }
                catch (IOException)
                {
                    // Процесс закрыл вход раньше (упал на старте) — код выхода и stderr скажут остальное.
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch (IOException) { }
                }
            }

            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (ct.IsCancellationRequested) throw;
            return new AgentProcessOutcome(-1, await SafeAwait(stdoutTask, KillDrainWait), await SafeAwait(stderrTask, KillDrainWait), TimedOut: true);
        }

        // Внук процесса может держать пайп открытым — вывод дочитываем с ограничением по времени.
        return new AgentProcessOutcome(process.ExitCode, await SafeAwait(stdoutTask, DrainWait), await SafeAwait(stderrTask, DrainWait), TimedOut: false);
    }

    private void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit((int)KillWait.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogWarning("Agent process kill failed: {ErrorType}", ex.GetType().Name);
        }
    }

    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KillDrainWait = TimeSpan.FromSeconds(2);

    private static async Task<string> SafeAwait(Task<string> task, TimeSpan wait)
    {
        var done = await Task.WhenAny(task, Task.Delay(wait));
        return done == task && task.IsCompletedSuccessfully ? task.Result : string.Empty;
    }

    private static async Task<string> ReadCappedAsync(Stream stream, int maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        try
        {
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                var room = maxBytes - (int)buffer.Length;
                if (room > 0) buffer.Write(chunk, 0, Math.Min(room, read));
                // Сверх предела — дочитываем и выбрасываем: иначе процесс встанет на полном буфере пайпа.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Пайп закрыт при kill — возвращаем прочитанное.
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
