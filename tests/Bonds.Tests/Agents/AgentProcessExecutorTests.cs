using System.Diagnostics;
using Bonds.Infrastructure.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bonds.Tests.Agents;

/// <summary>Настоящий дочерний процесс (без CLI): вывод, stdin и kill дерева по таймауту.</summary>
public class AgentProcessExecutorTests
{
    private static ProcessStartInfo Shell(string unixScript, string windowsScript)
    {
        var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("/bin/sh");
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(windowsScript);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(unixScript);
        }

        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        return psi;
    }

    [Fact]
    public async Task Run_CapturesStdout_AndReadsStdin()
    {
        var executor = new AgentProcessExecutor(NullLogger<AgentProcessExecutor>.Instance);
        var psi = Shell("read line; echo \"got:$line\"", "set /p line= & call echo got:%line%");

        var outcome = await executor.RunAsync(psi, "hello\n", TimeSpan.FromSeconds(30), 1024, CancellationToken.None);

        outcome.TimedOut.Should().BeFalse();
        outcome.ExitCode.Should().Be(0);
        outcome.Stdout.Should().Contain("got:hello");
    }

    [Fact]
    public async Task Run_Timeout_KillsProcessTree_Quickly()
    {
        var executor = new AgentProcessExecutor(NullLogger<AgentProcessExecutor>.Instance);
        // Оболочка запускает дочерний долгий процесс: kill должен снять и его, иначе пайп держался бы до конца sleep.
        var psi = Shell("sleep 60 & sleep 60; wait", "ping -n 60 127.0.0.1 >nul");

        var sw = Stopwatch.StartNew();
        var outcome = await executor.RunAsync(psi, string.Empty, TimeSpan.FromSeconds(1), 1024, CancellationToken.None);
        sw.Stop();

        outcome.TimedOut.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Run_OutputIsCapped()
    {
        var executor = new AgentProcessExecutor(NullLogger<AgentProcessExecutor>.Instance);
        var psi = Shell("i=0; while [ $i -lt 2000 ]; do echo 0123456789; i=$((i+1)); done", "for /L %i in (1,1,2000) do @echo 0123456789");

        var outcome = await executor.RunAsync(psi, string.Empty, TimeSpan.FromSeconds(60), 100, CancellationToken.None);

        outcome.Stdout.Length.Should().BeLessThanOrEqualTo(100);
    }
}
