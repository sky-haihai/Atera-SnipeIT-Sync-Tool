using AteraSnipeSync.TrayApp;

namespace AteraSnipeSync.Tests.TrayApp;

/// <summary>
/// Verifies the one operator-facing folder target without opening Explorer or touching ProgramData.
/// </summary>
public sealed class ControlledPathValidatorTests
{
    [Fact]
    public void ProgramDataRoot_TargetsAteraSnipeSyncDirectory()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AteraSnipeSync");

        Assert.Equal(expected, ControlledPathValidator.ProgramDataRoot);
        Assert.True(ControlledPathValidator.IsUnderRoot(
            ControlledPathValidator.LogsRoot,
            ControlledPathValidator.ProgramDataRoot));
        Assert.True(ControlledPathValidator.IsUnderRoot(
            ControlledPathValidator.HistoryRoot,
            ControlledPathValidator.ProgramDataRoot));
        Assert.True(ControlledPathValidator.IsUnderRoot(
            ControlledPathValidator.PreflightRoot,
            ControlledPathValidator.ProgramDataRoot));
    }
}
