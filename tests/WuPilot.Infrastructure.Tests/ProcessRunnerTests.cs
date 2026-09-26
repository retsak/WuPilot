using WuPilot.Infrastructure.Windows.Diagnostics;

namespace WuPilot.Infrastructure.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task DrainsBothPipesWithoutDeadlock()
    {
        var result = await ProcessRunner.PowerShellAsync("1..3000 | ForEach-Object { [Console]::Out.WriteLine('output'); [Console]::Error.WriteLine('error') }", TimeSpan.FromSeconds(15), default).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("output", result.Output);
        Assert.Contains("error", result.Error);
    }

    [Fact]
    public async Task TimeoutAndCancellationReturnPromptly()
    {
        var result = await ProcessRunner.PowerShellAsync("Start-Sleep -Seconds 60", TimeSpan.FromMilliseconds(300), default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(-1, result.ExitCode);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.PowerShellAsync("Start-Sleep -Seconds 60", TimeSpan.FromSeconds(30), cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
