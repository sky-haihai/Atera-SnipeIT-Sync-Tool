using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows.Forms;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Runtime.Ipc;
using AteraSnipeSync.Core.Runtime.Windows;
using AteraSnipeSync.TrayApp;
using AntButton = AntdUI.Button;
using AntCheckbox = AntdUI.Checkbox;
using AntPageHeader = AntdUI.PageHeader;
using AntLabel = AntdUI.Label;
using AntPanel = AntdUI.Panel;
using AntProgress = AntdUI.Progress;
using AntInput = AntdUI.Input;
using AntSelect = AntdUI.Select;
using AntSwitch = AntdUI.Switch;
using AntTabPage = AntdUI.TabPage;
using AntTabs = AntdUI.Tabs;

namespace AteraSnipeSync.Tests.TrayApp;

/// <summary>
/// Verifies the minimum-size Dashboard layout without showing a window or contacting Worker, SCM, or external systems.
/// </summary>
public sealed class DashboardLayoutTests
{
    [Fact]
    public void MinimumSize_KeepsActionsVisible_AndSeparatesActivityCard()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifyMinimumSizeLayout();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Dashboard layout test thread timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void ConfigurationNavigation_ReusesEmbeddedPage_AndCanOpenAgainAfterCancel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifyConfigurationNavigation();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Configuration navigation test thread timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void PreviewResult_DoesNotReplaceLatestRun()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifyPreviewDoesNotReplaceLatestRun();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Latest run Preview regression test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// Builds and lays out the real control tree at minimum and expanded sizes, then checks the visual and ownership regressions.
    /// </summary>
    private static void VerifyMinimumSizeLayout()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "AteraSnipeSyncTests", Guid.NewGuid().ToString("N"));
        var writer = new DailyLogWriter(testRoot);
        try
        {
            using var form = new TrayDashboardForm(
                new LocalAppSettingsStore(Path.Combine(testRoot, LocalAppSettingsStore.DefaultFileName)),
                new WorkerIpcClient("layout-test-pipe", TimeSpan.FromMilliseconds(50)),
                new FakeServiceStatusReader(),
                new WorkerServiceMaintenanceLauncher(
                    () => Path.Combine(testRoot, "worker.exe"),
                    (_, _) => Task.FromResult(0)),
                writer);
            Assert.Equal(new System.Drawing.Size(920, 650), form.MinimumSize);
            form.Size = form.MinimumSize;
            form.CreateControl();
            PerformLayoutRecursively(form);
            PerformLayoutRecursively(form);

            Assert.Equal(typeof(AntdUI.BorderlessForm), typeof(TrayDashboardForm).BaseType);
            Assert.False(form.UseDwm);
            Assert.Equal(TrayUiTheme.WindowRadius, form.Radius);
            Assert.Equal(TrayUiTheme.WindowShadow, form.Shadow);
            Assert.Equal(1, form.BorderWidth);
            Assert.Equal(TrayUiTheme.Border, form.BorderColor);
            Assert.Equal(TrayUiTheme.WindowShadowColor, form.ShadowColor);

            var dashboardHeader = Assert.Single(
                Descendants<AntPageHeader>(form),
                header => header.Text == "Auto Sync");
            Assert.Equal("Dashboard", dashboardHeader.SubText);
            Assert.DoesNotContain(
                Descendants<AntLabel>(form),
                label => label.Text?.Contains("→", StringComparison.Ordinal) is true);
            Assert.Single(
                Descendants<AntLabel>(form),
                label => label.Text == "Detailed activity is available in the log folder.");
            Assert.DoesNotContain(
                Descendants<AntLabel>(form),
                label => label.Text == "Detailed activity is written to daily log files.");

            var syncNowButton = Assert.Single(
                Descendants<AntButton>(form),
                button => button.Text == "Sync Now");
            var actionSurface = Ancestors<AntPanel>(syncNowButton).First();
            Assert.True(actionSurface.Height <= 100, $"Action surface is unexpectedly tall: {actionSurface.Height}px.");
            Assert.Equal(new Padding(14, 18, 14, 18), actionSurface.Padding);
            var minimumActionSizes = AssertCompactActionLayout(actionSurface);

            foreach (var text in new[] { "Configuration", "Open Log Folder" })
            {
                var button = Assert.Single(Descendants<AntButton>(form), value => value.Text == text);
                var parent = Assert.IsAssignableFrom<Control>(button.Parent);
                Assert.True(
                    button.Right <= parent.ClientSize.Width && button.Bottom <= parent.ClientSize.Height,
                    $"{text} is clipped. Bounds={button.Bounds}; ParentClient={parent.ClientSize}.");
                Assert.True(button.Width >= button.MinimumSize.Width, $"{text} is narrower than its minimum width.");
                Assert.Equal(TrayUiTheme.ControlRadius, button.Radius);
                Assert.False(string.IsNullOrWhiteSpace(button.IconSvg));
            }

            var cards = Descendants<AntPanel>(form).ToArray();
            var statusCard = Assert.Single(cards, card => CardTitle(card) == "System status");
            Assert.DoesNotContain(
                Descendants<AntLabel>(statusCard),
                label => label.Text == "Current operation");
            var activityCard = Assert.Single(cards, card => CardTitle(card) == "Current activity");
            var progress = Assert.Single(Descendants<AntProgress>(form));
            Assert.Same(activityCard, Ancestors<AntPanel>(progress).First());

            var scheduleCard = Assert.Single(cards, card => CardTitle(card) == "Schedule");
            Assert.DoesNotContain(cards, card => CardTitle(card) == "Automation");
            Assert.DoesNotContain(
                Descendants<AntLabel>(form),
                label => label.Text == "Run and service actions");
            Assert.Null(CardTitle(actionSurface));

            var latestRunCard = Assert.Single(cards, card => CardTitle(card) == "Latest run");
            foreach (var surface in new[] { statusCard, activityCard, scheduleCard, latestRunCard, actionSurface })
            {
                Assert.True(surface.Shadow >= 6, "Dashboard surface does not retain the shared soft shadow.");
                Assert.True(surface.ShadowOpacity > 0F, "Dashboard surface shadow is fully transparent.");
                Assert.True(surface.BorderWidth >= 1F, "Dashboard surface does not retain its visible border.");
                Assert.NotNull(surface.BorderColor);
                Assert.Equal(new Padding(5), surface.Margin);
                Assert.Equal(TrayUiTheme.SurfaceRadius, surface.Radius);
            }

            foreach (var card in new[] { statusCard, activityCard, scheduleCard, latestRunCard })
            {
                Assert.Equal(new Padding(14, 10, 14, 12), card.Padding);
            }

            var metricCaptions = new[] { "Created", "Updated", "No change", "Deleted" };
            var metricTiles = Descendants<AntPanel>(latestRunCard)
                .Where(panel => Descendants<AntLabel>(panel).Any(label => metricCaptions.Contains(label.Text)))
                .ToArray();
            Assert.Equal(4, metricTiles.Length);
            Assert.All(metricTiles, tile => Assert.Equal(TrayUiTheme.NestedSurfaceRadius, tile.Radius));

            foreach (var text in new[] { "Preview", "Restart Service", "Open Log Folder" })
            {
                var button = Assert.Single(Descendants<AntButton>(form), value => value.Text == text);
                Assert.True(button.BorderWidth >= 1F, $"{text} does not retain a visible secondary border.");
                Assert.NotNull(button.DefaultBorderColor);
                Assert.Equal(TrayUiTheme.ControlRadius, button.Radius);
            }

            var latestRunLabels = Descendants<AntLabel>(latestRunCard)
                .Select(label => label.Text)
                .ToArray();
            Assert.Contains("Created", latestRunLabels);
            Assert.Contains("Updated", latestRunLabels);
            Assert.Contains("No change", latestRunLabels);
            Assert.Contains("Deleted", latestRunLabels);
            Assert.DoesNotContain("Pulled", latestRunLabels);
            Assert.DoesNotContain("Mapped", latestRunLabels);
            Assert.DoesNotContain("Failed", latestRunLabels);

            form.Size = new System.Drawing.Size(1560, 1187);
            PerformLayoutRecursively(form);
            PerformLayoutRecursively(form);
            AssertCompactActionLayout(actionSurface, minimumActionSizes);
        }
        finally
        {
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Verifies that Dashboard run actions retain one centered DPI-scaled size instead of stretching with their table cells.
    /// </summary>
    private static IReadOnlyDictionary<string, System.Drawing.Size> AssertCompactActionLayout(
        AntPanel actionSurface,
        IReadOnlyDictionary<string, System.Drawing.Size>? expectedSizes = null)
    {
        var actions = Assert.Single(actionSurface.Controls.OfType<TableLayoutPanel>());
        var columnWidths = actions.GetColumnWidths();
        var rowHeights = actions.GetRowHeights();
        Assert.Equal(4, columnWidths.Length);
        Assert.Single(rowHeights);

        var observedSizes = new Dictionary<string, System.Drawing.Size>(StringComparer.Ordinal);
        foreach (var text in new[] { "Sync Now", "Preview", "Cancel", "Restart Service" })
        {
            var button = Assert.Single(Descendants<AntButton>(actionSurface), value => value.Text == text);
            Assert.Same(actions, button.Parent);
            Assert.Equal(DockStyle.None, button.Dock);
            Assert.Equal(AnchorStyles.None, button.Anchor);
            Assert.Equal(Padding.Empty, button.Margin);
            Assert.Equal(button.MinimumSize, button.Size);
            Assert.Equal(button.MaximumSize, button.Size);
            Assert.Equal(TrayUiTheme.ControlRadius, button.Radius);
            Assert.True(button.Left >= 0 && button.Top >= 0, $"{text} starts outside its action table.");
            Assert.True(
                button.Right <= actions.ClientSize.Width && button.Bottom <= actions.ClientSize.Height,
                $"{text} is clipped. Bounds={button.Bounds}; ParentClient={actions.ClientSize}.");

            var column = actions.GetColumn(button);
            var row = actions.GetRow(button);
            var cellLeft = actions.DisplayRectangle.Left + columnWidths.Take(column).Sum();
            var cellTop = actions.DisplayRectangle.Top + rowHeights.Take(row).Sum();
            var horizontalOffset = Math.Abs(
                (button.Left + (button.Width / 2D)) - (cellLeft + (columnWidths[column] / 2D)));
            var verticalOffset = Math.Abs(
                (button.Top + (button.Height / 2D)) - (cellTop + (rowHeights[row] / 2D)));
            Assert.InRange(horizontalOffset, 0D, 1D);
            Assert.InRange(verticalOffset, 0D, 1D);

            if (expectedSizes is not null)
            {
                Assert.Equal(expectedSizes[text], button.Size);
            }

            observedSizes.Add(text, button.Size);
        }

        Assert.Single(observedSizes.Values.Distinct());
        return observedSizes;
    }

    /// <summary>
    /// Renders a real sync followed by Preview and proves only Current activity changes for the dry-run result.
    /// </summary>
    private static void VerifyPreviewDoesNotReplaceLatestRun()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "AteraSnipeSyncTests", Guid.NewGuid().ToString("N"));
        var writer = new DailyLogWriter(testRoot);
        try
        {
            using var form = new TrayDashboardForm(
                new LocalAppSettingsStore(Path.Combine(testRoot, LocalAppSettingsStore.DefaultFileName)),
                new WorkerIpcClient("preview-latest-run-test-pipe", TimeSpan.FromMilliseconds(50)),
                new FakeServiceStatusReader(),
                new WorkerServiceMaintenanceLauncher(
                    () => Path.Combine(testRoot, "worker.exe"),
                    (_, _) => Task.FromResult(0)),
                writer);
            var latestRunCard = Assert.Single(
                Descendants<AntPanel>(form),
                card => CardTitle(card) == "Latest run");
            var renderSyncResult = typeof(TrayDashboardForm).GetMethod(
                "RenderSyncResult",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("TrayDashboardForm.RenderSyncResult was not found.");

            renderSyncResult.Invoke(form, [CreateSyncResult(dryRun: false, 1, 2, 3, 4)]);
            var latestRealRunLabels = Descendants<AntLabel>(latestRunCard)
                .Select(label => label.Text)
                .ToArray();

            renderSyncResult.Invoke(form, [CreateSyncResult(dryRun: true, 91, 92, 93, 94)]);
            var labelsAfterPreview = Descendants<AntLabel>(latestRunCard)
                .Select(label => label.Text)
                .ToArray();

            Assert.Equal(latestRealRunLabels, labelsAfterPreview);
            Assert.DoesNotContain("91", labelsAfterPreview);
            Assert.Contains(
                Descendants<AntLabel>(form),
                label => label.Text == "Completed. 0 warning(s); details are available in Logs.");
        }
        finally
        {
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Creates a sanitized terminal result used only by the local Latest run rendering regression.
    /// </summary>
    private static WorkerSyncResultSummary CreateSyncResult(
        bool dryRun,
        int created,
        int updated,
        int noChange,
        int deleted)
        => new()
        {
            Success = true,
            StartedAt = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 7, 23, dryRun ? 13 : 12, 5, 0, TimeSpan.Zero),
            DryRun = dryRun,
            Cancelled = false,
            Pulled = 10,
            Mapped = 10,
            Created = created,
            Updated = updated,
            Deleted = deleted,
            Skipped = noChange,
            Failed = 0,
            WarningCount = 0,
            Failures = []
        };

    /// <summary>
    /// Exercises Dashboard-to-configuration navigation twice on one form and raises the real Cancel button click between visits.
    /// </summary>
    private static void VerifyConfigurationNavigation()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "AteraSnipeSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var settingsPath = Path.Combine(testRoot, LocalAppSettingsStore.DefaultFileName);
        File.WriteAllText(settingsPath, "{ invalid-json");
        var writer = new DailyLogWriter(testRoot);
        try
        {
            using var form = new TrayDashboardForm(
                new LocalAppSettingsStore(settingsPath),
                new WorkerIpcClient("navigation-test-pipe", TimeSpan.FromMilliseconds(50)),
                new FakeServiceStatusReader(),
                new WorkerServiceMaintenanceLauncher(
                    () => Path.Combine(testRoot, "worker.exe"),
                    (_, _) => Task.FromResult(0)),
                writer);
            form.CreateControl();

            var configurationPage = Assert.Single(Descendants<SyncConfigurationPage>(form));
            Assert.False(typeof(Form).IsAssignableFrom(configurationPage.GetType()));
            Assert.Same(form, Assert.Single(Ancestors<Form>(configurationPage)));
            Assert.Empty(Descendants<AntCheckbox>(configurationPage));
            var switches = Descendants<AntSwitch>(configurationPage).ToArray();
            var expectedSwitchNames = new[]
            {
                "CreateMissingCompanies",
                "CreateMissingModels",
                "ScheduleEnabled",
                "RunOnLastDayOfMonth",
                "NotificationsEnabled",
                "NotifySyncCompleted",
                "NotifySyncFailed",
                "SmtpUseSsl"
            };
            Assert.Equal(
                expectedSwitchNames.Order(StringComparer.Ordinal),
                switches.Select(value => value.Name).Order(StringComparer.Ordinal));
            Assert.All(
                switches,
                value => Assert.Equal(new System.Drawing.Size(56, 32), value.MinimumSize));
            Assert.DoesNotContain(
                switches,
                value => value.Name == "DryRun");
            Assert.DoesNotContain(
                Descendants<AntLabel>(configurationPage),
                label => label.Text?.Contains("Dry run", StringComparison.OrdinalIgnoreCase) is true);
            Assert.All(
                Descendants<AntInput>(configurationPage),
                input => Assert.True(
                    input.Height >= (input.Multiline ? 96 : 44),
                    $"{input.Name} is vertically compressed at {input.Height}px."));
            foreach (var text in new[] { "Cancel", "Save changes" })
            {
                var button = Assert.Single(
                    Descendants<AntButton>(configurationPage),
                    value => value.Text == text);
                Assert.True(button.MinimumSize.Height >= 46, $"{text} has a compressed minimum height.");
            }
            var removedFallbackKeys = new[] { "DefaultCompany", "DefaultManufacturer", "DefaultModel" };
            Assert.DoesNotContain(
                Descendants<AntInput>(configurationPage),
                input => removedFallbackKeys.Contains(input.Name, StringComparer.Ordinal));
            Assert.DoesNotContain(
                Descendants<AntInput>(configurationPage),
                input => input.Name == "NotificationEvents");
            Assert.Contains(Descendants<AntLabel>(configurationPage), label => label.Text == "Sync completed");
            Assert.Contains(Descendants<AntLabel>(configurationPage), label => label.Text == "Sync failed");

            configurationPage.ApplyNotificationEvents(
            [
                " scheduledsynccompleted ",
                NotificationEventTypes.ManualPreviewFailed.ToUpperInvariant(),
                "UnknownLegacyEvent"
            ]);
            Assert.Equal(
            [
                NotificationEventTypes.ScheduledSyncCompleted,
                NotificationEventTypes.ManualSyncCompleted,
                NotificationEventTypes.ManualPreviewCompleted,
                NotificationEventTypes.SyncCompleted,
                NotificationEventTypes.ScheduledSyncFailed,
                NotificationEventTypes.ManualSyncFailed,
                NotificationEventTypes.ManualPreviewFailed,
                NotificationEventTypes.SyncFailed
            ],
                configurationPage.ReadSelectedNotificationEvents());
            configurationPage.ApplyNotificationEvents([]);
            Assert.Empty(configurationPage.ReadSelectedNotificationEvents());

            WaitWithMessagePump(form.ShowConfigurationPageAsync());

            Assert.True(form.IsConfigurationPageActive);
            var tabs = Assert.Single(Descendants<AntTabs>(configurationPage));
            tabs.SelectedTab = Assert.Single(
                tabs.Pages.Cast<AntTabPage>(),
                page => page.Text == "Schedule");
            Application.DoEvents();
            PerformLayoutRecursively(configurationPage);
            var frequency = Assert.Single(
                Descendants<AntSelect>(configurationPage),
                value => value.Name == "ScheduleFrequency");
            var timeZone = Assert.Single(
                Descendants<AntInput>(configurationPage),
                value => value.Name == "TimeZone");
            var weekDays = Assert.Single(
                Descendants<AntInput>(configurationPage),
                value => value.Name == "WeekDays");
            var monthDays = Assert.Single(
                Descendants<AntInput>(configurationPage),
                value => value.Name == "MonthDays");
            var lastDay = Assert.Single(
                Descendants<AntSwitch>(configurationPage),
                value => value.Name == "RunOnLastDayOfMonth");
            AssertScheduleVisibility(
                configurationPage,
                frequency,
                timeZone,
                weekDays,
                monthDays,
                lastDay,
                "Daily",
                weeklyVisible: false,
                monthlyVisible: false);
            AssertScheduleVisibility(
                configurationPage,
                frequency,
                timeZone,
                weekDays,
                monthDays,
                lastDay,
                "Weekly",
                weeklyVisible: true,
                monthlyVisible: false);
            AssertScheduleVisibility(
                configurationPage,
                frequency,
                timeZone,
                weekDays,
                monthDays,
                lastDay,
                "Monthly",
                weeklyVisible: false,
                monthlyVisible: true);
            Assert.DoesNotContain(
                Descendants<AntButton>(configurationPage),
                button => button.Text == "Back to Dashboard");
            var cancel = Assert.Single(
                Descendants<AntButton>(configurationPage),
                button => button.Text == "Cancel");
            Assert.True(cancel.Enabled);
            RaiseClick(cancel);

            Assert.False(form.IsConfigurationPageActive);
            var configurationButton = Assert.Single(
                Descendants<AntButton>(form),
                button => button.Text == "Configuration");
            Assert.True(configurationButton.Enabled);

            File.WriteAllText(
                settingsPath,
                """
                {
                  "Atera": {
                    "BaseUrl": "https://app.atera.com/api/v3/",
                    "ApiKey": "synthetic-key-one"
                  }
                }
                """);

            WaitWithMessagePump(form.ShowConfigurationPageAsync());

            Assert.True(form.IsConfigurationPageActive);
            Assert.Same(configurationPage, Assert.Single(Descendants<SyncConfigurationPage>(form)));
            var ateraApiKey = Assert.Single(
                Descendants<AntInput>(configurationPage),
                input => input.Name == "AteraApiKey");
            Assert.Equal("synthetic-key-one", ateraApiKey.Text);

            form.ShowDashboardPage();
            File.WriteAllText(
                settingsPath,
                """
                {
                  "SnipeIt": {
                    "BaseUrl": "https://snipe.example.test/api/v1/"
                  }
                }
                """);

            WaitWithMessagePump(form.ShowConfigurationPageAsync());

            Assert.Empty(ateraApiKey.Text);
            ateraApiKey.Text = "synthetic-key-two";
            Assert.Single(
                Descendants<AntInput>(configurationPage),
                input => input.Name == "SnipeItApiToken").Text = "synthetic-token";
            var save = Assert.Single(
                Descendants<AntButton>(configurationPage),
                button => button.Text == "Save changes");
            RaiseClick(save);
            WaitUntilWithMessagePump(
                () => !form.IsConfigurationPageActive && configurationButton.Enabled,
                "Configuration save did not return to an enabled Dashboard.");

            Assert.False(form.IsConfigurationPageActive);
            Assert.True(configurationButton.Enabled);
        }
        finally
        {
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Changes the real frequency selector and verifies each conditional value/label row plus the always-visible time-zone row.
    /// </summary>
    private static void AssertScheduleVisibility(
        Control configurationPage,
        AntSelect frequency,
        AntInput timeZone,
        AntInput weekDays,
        AntInput monthDays,
        AntSwitch lastDay,
        string frequencyText,
        bool weeklyVisible,
        bool monthlyVisible)
    {
        frequency.SelectedValue = frequencyText;
        Application.DoEvents();
        PerformLayoutRecursively(configurationPage);

        Assert.True(HasLocalVisibleState(timeZone));
        Assert.Equal(weeklyVisible, HasLocalVisibleState(weekDays));
        Assert.Equal(monthlyVisible, HasLocalVisibleState(monthDays));
        Assert.Equal(monthlyVisible, HasLocalVisibleState(lastDay));

        var scheduleGrid = Assert.IsType<TableLayoutPanel>(weekDays.Parent);
        Assert.Same(scheduleGrid, monthDays.Parent);
        Assert.Same(scheduleGrid, lastDay.Parent);
        Assert.Equal(
            weeklyVisible,
            HasLocalVisibleState(Assert.IsAssignableFrom<Control>(
                scheduleGrid.GetControlFromPosition(0, scheduleGrid.GetRow(weekDays)))));
        Assert.Equal(
            monthlyVisible,
            HasLocalVisibleState(Assert.IsAssignableFrom<Control>(
                scheduleGrid.GetControlFromPosition(0, scheduleGrid.GetRow(monthDays)))));
        Assert.Equal(
            monthlyVisible,
            HasLocalVisibleState(Assert.IsAssignableFrom<Control>(
                scheduleGrid.GetControlFromPosition(0, scheduleGrid.GetRow(lastDay)))));
    }

    /// <summary>
    /// Reads a control's own Visible bit without requiring the containing top-level test form to be shown.
    /// </summary>
    private static bool HasLocalVisibleState(Control control)
    {
        const int stateVisible = 0x00000002;
        var getState = typeof(Control)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "GetState" && method.GetParameters().Length == 1)
            ?? throw new InvalidOperationException("WinForms Control.GetState was not found.");
        var parameterType = getState.GetParameters()[0].ParameterType;
        var visibleFlag = parameterType.IsEnum
            ? Enum.ToObject(parameterType, stateVisible)
            : Convert.ChangeType(stateVisible, parameterType);
        return (bool)(getState.Invoke(control, [visibleFlag])
            ?? throw new InvalidOperationException("WinForms Control.GetState returned no value."));
    }

    private static void RaiseClick(Control control)
    {
        var onClick = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WinForms Control.OnClick was not found.");
        onClick.Invoke(control, [EventArgs.Empty]);
    }

    private static void WaitWithMessagePump(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }

        Assert.True(task.IsCompleted, "Embedded configuration activation timed out.");
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntilWithMessagePump(Func<bool> condition, string failureMessage)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }

        Assert.True(condition(), failureMessage);
    }

    private static void PerformLayoutRecursively(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls)
        {
            PerformLayoutRecursively(child);
        }
    }

    private static IEnumerable<TControl> Descendants<TControl>(Control root)
        where TControl : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is TControl match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<TControl>(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<TControl> Ancestors<TControl>(Control control)
        where TControl : Control
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is TControl match)
            {
                yield return match;
            }
        }
    }

    private static string? CardTitle(AntPanel card)
    {
        var layout = card.Controls.OfType<TableLayoutPanel>().SingleOrDefault();
        return layout?.GetControlFromPosition(0, 0) is AntLabel title ? title.Text : null;
    }

    /// <summary>
    /// Supplies a deterministic read-only service state if a future layout path requests it unexpectedly.
    /// </summary>
    private sealed class FakeServiceStatusReader : IWorkerServiceStatusReader
    {
        public Task<WorkerWindowsServiceState> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(WorkerWindowsServiceState.Running);
    }
}
