using AteraSnipeSync.Core.Common;
using AteraSnipeSync.TrayApp;

namespace AteraSnipeSync.Tests.TrayApp;

/// <summary>
/// Verifies that whole-run progress follows weighted manual-sync work instead of completed child snapshots.
/// </summary>
public sealed class SyncProgressCalculatorTests
{
    [Fact]
    public void Calculate_RealSync_ReservesMostProgressForAssetExecution()
    {
        var calculator = new SyncProgressCalculator(previewOnly: false);

        Assert.Equal(0, Calculate(calculator, "Sync", "Starting sync run.", percent: 0));
        Assert.Equal(4, Calculate(calculator, "Sync", "Atera pull completed with 485 agent(s).", percent: 35));
        Assert.Equal(5, Calculate(calculator, "Sync", "Mapping completed with 485 asset(s).", percent: 45));
        Assert.Equal(5, Calculate(calculator, "SnipeImport", "Starting Snipe-IT planning for 485 asset(s).", 0, 485));
        Assert.Equal(10, Calculate(calculator, "SnipeImport", "Blocked Snipe-IT asset 485/485: validation.", 485, 485));
        Assert.Equal(11, Calculate(calculator, "SnipeImport", "Loaded Snipe-IT model snapshot with 485 model(s).", 485, 485));
        Assert.Equal(13, Calculate(calculator, "SnipeImport", "Planning Snipe-IT category references 1/1: Computer.", 0, 1));
        Assert.Equal(15, Calculate(calculator, "SnipeImport", "Planning Snipe-IT model references 485/485: Model.", 485, 485));
        Assert.Equal(15, Calculate(calculator, "SnipeImport", "Loaded Snipe-IT hardware snapshot with 485 asset(s).", 485, 485));
        Assert.Equal(30, Calculate(calculator, "SnipeImport", "Planned Snipe-IT asset 485/485: DEVICE-485.", 485, 485));
        Assert.Equal(35, Calculate(calculator, "SnipeImport", "No missing Snipe-IT reference records need to be created.", 0, 0));
        Assert.Equal(35, Calculate(calculator, "SnipeImport", "Executing Snipe-IT asset 1/485: DEVICE-001.", 0, 485));
        Assert.Equal(67, Calculate(calculator, "SnipeImport", "Executed Snipe-IT asset 243/485: DEVICE-243.", 243, 485));
        Assert.Equal(99, Calculate(calculator, "SnipeImport", "Executed Snipe-IT asset 485/485: DEVICE-485.", 485, 485));
        Assert.Equal(99, Calculate(calculator, "Sync", "Snipe-IT import stage completed.", percent: 95));
        Assert.Equal(100, Calculate(calculator, "Sync", "Sync run completed.", percent: 100));
    }

    [Fact]
    public void Calculate_CompletedChildSnapshot_DoesNotJumpWholeRunToNinetyFivePercent()
    {
        var calculator = new SyncProgressCalculator(previewOnly: false);

        Calculate(calculator, "SnipeImport", "Starting Snipe-IT planning for 485 asset(s).", 0, 485);
        var value = Calculate(
            calculator,
            "SnipeImport",
            "Loaded Snipe-IT hardware snapshot with 485 asset(s).",
            485,
            485);

        Assert.Equal(15, value);
    }

    [Fact]
    public void Calculate_Preview_WeightsAssetPlanningAndReservesFinalization()
    {
        var calculator = new SyncProgressCalculator(previewOnly: true);

        Assert.Equal(15, Calculate(calculator, "SnipeImport", "Matching Snipe-IT asset 1/485: DEVICE-001.", 0, 485));
        Assert.Equal(55, Calculate(calculator, "SnipeImport", "Planned Snipe-IT asset 243/485: DEVICE-243.", 243, 485));
        Assert.Equal(95, Calculate(calculator, "SnipeImport", "Planned Snipe-IT asset 485/485: DEVICE-485.", 485, 485));
        Assert.Equal(96, Calculate(calculator, "SnipeImport", "Writing manual preflight CSV files.", 485, 485));
        Assert.Equal(98, Calculate(calculator, "SnipeImport", "Applying dry-run plan without Snipe-IT writes.", 485, 485));
        Assert.Equal(99, Calculate(calculator, "SnipeImport", "Completed Snipe-IT dry-run planning.", 485, 485));
        Assert.Equal(100, Calculate(calculator, "Sync", "Sync run completed.", percent: 100));
    }

    [Fact]
    public void Calculate_OutOfOrderCallback_DoesNotReduceProgress()
    {
        var calculator = new SyncProgressCalculator(previewOnly: false);

        Assert.Equal(67, Calculate(calculator, "SnipeImport", "Executed Snipe-IT asset 243/485: DEVICE-243.", 243, 485));
        Assert.Equal(67, Calculate(calculator, "SnipeImport", "Loaded Snipe-IT model snapshot with 485 model(s).", 485, 485));
    }

    [Fact]
    public void Calculate_TerminalFailure_ReachesOneHundredPercent()
    {
        var calculator = new SyncProgressCalculator(previewOnly: false);

        Assert.Equal(100, Calculate(calculator, "Sync", "Snipe-IT import failed: rejected.", percent: 100));
    }

    private static int Calculate(
        SyncProgressCalculator calculator,
        string stage,
        string message,
        int? current = null,
        int? total = null,
        int? percent = null)
    {
        return calculator.Calculate(new SyncProgressUpdate
        {
            Stage = stage,
            Message = message,
            Current = current,
            Total = total,
            Percent = percent
        });
    }
}
