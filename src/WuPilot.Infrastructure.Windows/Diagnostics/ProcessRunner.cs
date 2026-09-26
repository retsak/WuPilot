using System.Diagnostics;

namespace WuPilot.Infrastructure.Windows.Diagnostics;

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Process exited between the check and kill. */ }
            // Observe both readers; inherited pipe handles must not extend the timeout indefinitely.
            try { await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(-1, string.Empty, $"Command timed out after {timeout}.");
        }

        return new ProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
    }

    public static Task<ProcessResult> PowerShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], timeout, cancellationToken);
}
