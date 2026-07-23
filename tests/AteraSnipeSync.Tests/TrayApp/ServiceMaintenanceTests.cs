using System.ComponentModel;
using System.Diagnostics;
using AteraSnipeSync.Core.Runtime.Windows;
using AteraSnipeSync.TrayApp;

namespace AteraSnipeSync.Tests.TrayApp;

/// <summary>
/// Verifies fixed helper arguments, UAC cancellation, preflight, and stage-ordered service commands with fakes only.
/// </summary>
public sealed class ServiceMaintenanceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "AteraSnipeSyncTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("restart", (int)ServiceMaintenanceOperation.Restart)]
    [InlineData("reinstall-and-restart", (int)ServiceMaintenanceOperation.ReinstallAndRestart)]
    public void Arguments_ParseOnlyExactOperations(string argument, int expectedValue)
    {
        Assert.True(ServiceMaintenanceArguments.TryParse(
            ["--service-maintenance", argument],
            out var operation));
        Assert.Equal((ServiceMaintenanceOperation)expectedValue, operation);
        Assert.False(ServiceMaintenanceArguments.TryParse(
            ["--service-maintenance", argument, "extra"],
            out _));
    }

    [Fact]
    public async Task Launcher_ReinstallMissingWorker_FailsBeforeUac()
    {
        var launches = 0;
        var launcher = new WorkerServiceMaintenanceLauncher(
            () => Path.Combine(_tempDirectory, WorkerServiceIdentity.ExecutableFileName),
            (_, _) =>
            {
                launches++;
                return Task.FromResult(0);
            });

        var result = await launcher.ExecuteElevatedAsync(
            ServiceMaintenanceOperation.ReinstallAndRestart,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("WorkerExecutableMissing", result.Stage);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Launcher_UacCancellation_IsNormalCancelledResult()
    {
        var launcher = new WorkerServiceMaintenanceLauncher(
            () => "unused",
            (_, _) => throw new Win32Exception(1223));

        var result = await launcher.ExecuteElevatedAsync(
            ServiceMaintenanceOperation.Restart,
            CancellationToken.None);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Launcher_UsesOnlyFixedHelperArguments()
    {
        ProcessStartInfo? captured = null;
        var launcher = new WorkerServiceMaintenanceLauncher(
            () => "unused",
            (startInfo, _) =>
            {
                captured = startInfo;
                return Task.FromResult(ServiceMaintenanceExitCodes.Success);
            });

        var result = await launcher.ExecuteElevatedAsync(
            ServiceMaintenanceOperation.Restart,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(captured);
        Assert.Equal("runas", captured.Verb);
        Assert.Equal(["--service-maintenance", "restart"], captured.ArgumentList.ToArray());
    }

    [Fact]
    public async Task Runner_RestartRunning_StopsThenStartsFixedService()
    {
        var commands = new RecordingCommandRunner();
        var runner = Runner(commands, new SequenceStatusReader(
            WorkerWindowsServiceState.Running,
            WorkerWindowsServiceState.Stopped,
            WorkerWindowsServiceState.Running));

        var exitCode = await runner.RunAsync(ServiceMaintenanceOperation.Restart, CancellationToken.None);

        Assert.Equal(ServiceMaintenanceExitCodes.Success, exitCode);
        Assert.Equal(["stop", "start"], commands.Calls.Select(call => call.Verb));
        Assert.All(commands.Calls, call => Assert.Contains(WorkerServiceIdentity.ServiceName, call.Arguments));
    }

    [Fact]
    public async Task Runner_RestartStopped_StartsOnly()
    {
        var commands = new RecordingCommandRunner();
        var runner = Runner(commands, new SequenceStatusReader(
            WorkerWindowsServiceState.Stopped,
            WorkerWindowsServiceState.Running));

        var exitCode = await runner.RunAsync(ServiceMaintenanceOperation.Restart, CancellationToken.None);

        Assert.Equal(ServiceMaintenanceExitCodes.Success, exitCode);
        Assert.Equal(["start"], commands.Calls.Select(call => call.Verb));
    }

    [Fact]
    public async Task Runner_RestartMissing_DoesNotCreateService()
    {
        var commands = new RecordingCommandRunner();
        var runner = Runner(commands, new SequenceStatusReader(WorkerWindowsServiceState.NotInstalled));

        var exitCode = await runner.RunAsync(ServiceMaintenanceOperation.Restart, CancellationToken.None);

        Assert.Equal(ServiceMaintenanceExitCodes.ServiceNotInstalled, exitCode);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task Runner_Reinstall_UsesStopDeleteCreateStartAndFixedIdentity()
    {
        Directory.CreateDirectory(_tempDirectory);
        var workerPath = Path.Combine(_tempDirectory, WorkerServiceIdentity.ExecutableFileName);
        await File.WriteAllTextAsync(workerPath, "test");
        var commands = new RecordingCommandRunner();
        var statuses = new SequenceStatusReader(
            WorkerWindowsServiceState.Running,
            WorkerWindowsServiceState.Stopped,
            WorkerWindowsServiceState.Stopped,
            WorkerWindowsServiceState.NotInstalled,
            WorkerWindowsServiceState.Running);
        var runner = new ElevatedServiceMaintenanceRunner(
            commands,
            statuses,
            () => true,
            () => workerPath,
            _ => Task.CompletedTask);

        var exitCode = await runner.RunAsync(
            ServiceMaintenanceOperation.ReinstallAndRestart,
            CancellationToken.None);

        Assert.Equal(ServiceMaintenanceExitCodes.Success, exitCode);
        Assert.Equal(["stop", "delete", "create", "start"], commands.Calls.Select(call => call.Verb));
        var create = Assert.Single(commands.Calls, call => call.Verb == "create");
        Assert.Contains(WorkerServiceIdentity.ServiceName, create.Arguments);
        Assert.Contains(WorkerServiceIdentity.DisplayName, create.Arguments);
        Assert.Contains($"\"{workerPath}\"", create.Arguments);
    }

    [Fact]
    public async Task Runner_CreateFailure_StopsBeforeStart()
    {
        Directory.CreateDirectory(_tempDirectory);
        var workerPath = Path.Combine(_tempDirectory, WorkerServiceIdentity.ExecutableFileName);
        await File.WriteAllTextAsync(workerPath, "test");
        var commands = new RecordingCommandRunner(failingVerb: "create");
        var runner = new ElevatedServiceMaintenanceRunner(
            commands,
            new SequenceStatusReader(WorkerWindowsServiceState.NotInstalled),
            () => true,
            () => workerPath,
            _ => Task.CompletedTask);

        var exitCode = await runner.RunAsync(
            ServiceMaintenanceOperation.ReinstallAndRestart,
            CancellationToken.None);

        Assert.Equal(ServiceMaintenanceExitCodes.CreateFailed, exitCode);
        Assert.Equal(["create"], commands.Calls.Select(call => call.Verb));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static ElevatedServiceMaintenanceRunner Runner(
        IServiceCommandRunner commands,
        IWorkerServiceStatusReader statuses)
    {
        return new ElevatedServiceMaintenanceRunner(
            commands,
            statuses,
            () => true,
            () => "unused",
            _ => Task.CompletedTask);
    }

    /// <summary>
    /// Returns deterministic SCM observations and repeats the final state when exhausted.
    /// </summary>
    private sealed class SequenceStatusReader(params WorkerWindowsServiceState[] states)
        : IWorkerServiceStatusReader
    {
        private readonly Queue<WorkerWindowsServiceState> _states = new(states);
        private WorkerWindowsServiceState _last = states.LastOrDefault();

        public Task<WorkerWindowsServiceState> GetStatusAsync(CancellationToken cancellationToken)
        {
            if (_states.Count > 0)
            {
                _last = _states.Dequeue();
            }

            return Task.FromResult(_last);
        }
    }

    /// <summary>
    /// Records discrete sc.exe verbs/arguments and can fail one selected stage without starting a process.
    /// </summary>
    private sealed class RecordingCommandRunner(string? failingVerb = null) : IServiceCommandRunner
    {
        public List<(string Verb, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ServiceCommandResult> ExecuteAsync(
            string verb,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add((verb, arguments));
            return Task.FromResult(new ServiceCommandResult
            {
                Succeeded = !string.Equals(verb, failingVerb, StringComparison.Ordinal),
                TimedOut = false,
                ExitCode = string.Equals(verb, failingVerb, StringComparison.Ordinal) ? 1 : 0
            });
        }
    }
}
