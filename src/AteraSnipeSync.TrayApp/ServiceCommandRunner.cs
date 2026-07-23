using System.Diagnostics;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Carries the exit/timeout outcome of one fixed sc.exe invocation without capturing potentially unsafe command output.
/// </summary>
internal sealed class ServiceCommandResult
{
    public required bool Succeeded { get; init; }
    public required bool TimedOut { get; init; }
    public required int ExitCode { get; init; }
}

/// <summary>
/// Defines the injectable fixed-command boundary used by elevated service orchestration tests.
/// </summary>
internal interface IServiceCommandRunner
{
    Task<ServiceCommandResult> ExecuteAsync(
        string verb,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes System32 sc.exe with discrete ArgumentList entries, a finite timeout, and no shell command composition.
/// </summary>
internal sealed class ServiceCommandRunner : IServiceCommandRunner
{
    public async Task<ServiceCommandResult> ExecuteAsync(
        string verb,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var startInfo = new ProcessStartInfo(Path.Combine(systemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        startInfo.ArgumentList.Add(verb);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Windows service command.");
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            return new ServiceCommandResult
            {
                Succeeded = process.ExitCode == 0,
                TimedOut = false,
                ExitCode = process.ExitCode
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The command may have exited between timeout and the kill attempt.
            }

            return new ServiceCommandResult
            {
                Succeeded = false,
                TimedOut = true,
                ExitCode = -1
            };
        }
    }
}
