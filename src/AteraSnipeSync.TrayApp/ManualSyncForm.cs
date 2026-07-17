using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Provides a local manual sync test window that runs the Core pipeline directly without requiring WorkerService.
/// </summary>
public sealed class ManualSyncForm : Form
{
    private const string DefaultAteraBaseUrl = "https://app.atera.com/api/v3";
    private const string ExampleSnipeHost = "snipe.example.com";
    private static readonly string[] CommonDeviceTypes = ["Server", "PC", "Workstation", "Mac", "Linux", "SNMP", "TCP"];

    private readonly LocalAppSettingsStore _settingsStore;
    private readonly DailyLogWriter _dailyLogWriter;
    private readonly DailyLogWriter _dailyErrorLogWriter;
    private readonly TextBox _ateraBaseUrlTextBox = new();
    private readonly TextBox _ateraApiKeyTextBox = new();
    private readonly TextBox _snipeBaseUrlTextBox = new();
    private readonly TextBox _snipeApiTokenTextBox = new();
    private readonly TextBox _defaultCompanyTextBox = new();
    private readonly TextBox _companyAliasesTextBox = new();
    private readonly TextBox _defaultManufacturerTextBox = new();
    private readonly TextBox _manufacturerAliasesTextBox = new();
    private readonly TextBox _defaultModelTextBox = new();
    private readonly TextBox _defaultCategoryTextBox = new();
    private readonly TextBox _modelCategoriesToNormalizeTextBox = new();
    private readonly CheckedListBox _ignoredDeviceTypesCheckedListBox = new();
    private readonly NumericUpDown _defaultStatusIdInput = new();
    private readonly TextBox _macCustomFieldTextBox = new();
    private readonly TextBox _macFieldsetNameTextBox = new();
    private readonly TextBox _ignoredMacAddressesTextBox = new();
    private readonly NumericUpDown _nameMatchThresholdInput = new();
    private readonly CheckBox _createMissingCompaniesCheckBox = new();
    private readonly CheckBox _createMissingModelsCheckBox = new();
    private readonly Button _loadConfigButton = new();
    private readonly Button _saveConfigButton = new();
    private readonly Button _testAteraConnectionButton = new();
    private readonly Button _testSnipeConnectionButton = new();
    private readonly Button _normalizeModelCategoriesButton = new();
    private readonly Button _previewChangesButton = new();
    private readonly Button _syncNowButton = new();
    private readonly Button _cancelRunButton = new();
    private readonly Button _openPreflightFolderButton = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _progressSummaryLabel = new();
    private readonly Label _progressDetailLabel = new();
    private readonly TextBox _logTextBox = new();

    private CancellationTokenSource? _runCancellation;
    private string? _lastPreflightDirectory;
    private bool _dailyLogWriteFailureReported;
    private bool _dailyErrorLogWriteFailureReported;

    /// <summary>
    /// Creates the default manual sync window using the shared ProgramData local settings path.
    /// </summary>
    public ManualSyncForm()
        : this(new LocalAppSettingsStore(LocalAppSettingsStore.GetDefaultFilePath()))
    {
    }

    /// <summary>
    /// Creates a manual sync window with an injected settings store for loading and saving local Atera credentials.
    /// </summary>
    public ManualSyncForm(LocalAppSettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);

        _settingsStore = settingsStore;
        var logDirectory = LocalAppSettingsStore.GetDefaultLogDirectory();
        _dailyLogWriter = new DailyLogWriter(logDirectory);
        _dailyErrorLogWriter = new DailyLogWriter(logDirectory, "ManualSync_Error");
        InitializeComponent();
    }

    /// <summary>
    /// Loads locally saved manual sync configuration into the form without validating it against external APIs.
    /// </summary>
    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await LoadManualSyncConfigAsync(logMissingConfig: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Cancels any in-flight manual run before the form closes.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _runCancellation?.Cancel();
        base.OnFormClosing(e);
    }

    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        await _dailyLogWriter.DisposeAsync().ConfigureAwait(true);
        await _dailyErrorLogWriter.DisposeAsync().ConfigureAwait(true);
        base.OnFormClosed(e);
    }

    private void InitializeComponent()
    {
        Text = "Atera Snipe-IT Manual Sync";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 720);
        Size = new Size(860, 780);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            Text = "Manual Sync Test"
        };
        root.Controls.Add(titleLabel, 0, 0);

        var inputGrid = CreateInputGrid();
        root.Controls.Add(inputGrid, 0, 1);

        var actionPanel = CreateActionPanel();
        root.Controls.Add(actionPanel, 0, 2);

        var progressPanel = CreateProgressPanel();
        root.Controls.Add(progressPanel, 0, 3);

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.Font = new Font("Consolas", 9F);
        root.Controls.Add(_logTextBox, 0, 4);

        AppendLog("Ready. Use Preview Changes before Sync Now when validating a real environment.");
    }

    private TableLayoutPanel CreateInputGrid()
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = new Padding(0, 12, 0, 8)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddTextRow(grid, "Atera API Base URL", _ateraBaseUrlTextBox, DefaultAteraBaseUrl, password: false);
        AddTextRow(grid, "Atera API Key", _ateraApiKeyTextBox, string.Empty, password: true);
        AddTextRow(grid, "Snipe-IT API Base URL", _snipeBaseUrlTextBox, string.Empty, password: false);
        AddTextRow(grid, "Snipe-IT API Token", _snipeApiTokenTextBox, string.Empty, password: true);
        AddTextRow(grid, "Default Company", _defaultCompanyTextBox, "Unknown Company", password: false);
        AddMultilineTextRow(grid, "Company Aliases", _companyAliasesTextBox);
        AddTextRow(grid, "Default Manufacturer", _defaultManufacturerTextBox, "Unknown Manufacturer", password: false);
        AddMultilineTextRow(grid, "Manufacturer Aliases", _manufacturerAliasesTextBox);
        AddTextRow(grid, "Default Model", _defaultModelTextBox, "Unknown Model", password: false);
        AddTextRow(grid, "Default Category", _defaultCategoryTextBox, "Computer", password: false);
        AddTextRow(
            grid,
            "Normalize From Categories (;)",
            _modelCategoriesToNormalizeTextBox,
            "Server; Laptop; Desktop",
            password: false);
        AddCheckedListRow(grid, "Ignore Device Types", _ignoredDeviceTypesCheckedListBox, CommonDeviceTypes);
        AddNumericRow(grid, "Default Status ID", _defaultStatusIdInput, minimum: 1, maximum: 999999, value: 2);
        AddTextRow(grid, "MAC Custom Field DB Column", _macCustomFieldTextBox, string.Empty, password: false);
        AddTextRow(grid, "MAC Fieldset Name", _macFieldsetNameTextBox, string.Empty, password: false);
        AddTextRow(grid, "Ignore MAC Addresses (; separated)", _ignoredMacAddressesTextBox, string.Empty, password: false);
        AddDecimalRow(grid, "Name Match Threshold", _nameMatchThresholdInput, value: 0.92M);
        AddCheckRow(grid, "Create Missing Companies", _createMissingCompaniesCheckBox, isChecked: true);
        AddCheckRow(grid, "Create Missing Models", _createMissingModelsCheckBox, isChecked: false);

        return grid;
    }

    private FlowLayoutPanel CreateActionPanel()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 0, 0, 12),
            WrapContents = true
        };

        _loadConfigButton.Text = "Load Config";
        _loadConfigButton.Width = 120;
        _loadConfigButton.Click += async (_, _) => await LoadManualSyncConfigAsync().ConfigureAwait(true);
        panel.Controls.Add(_loadConfigButton);

        _saveConfigButton.Text = "Save Config";
        _saveConfigButton.Width = 120;
        _saveConfigButton.Click += async (_, _) => await SaveManualSyncConfigAsync().ConfigureAwait(true);
        panel.Controls.Add(_saveConfigButton);

        _testAteraConnectionButton.Text = "Test Atera";
        _testAteraConnectionButton.Width = 110;
        _testAteraConnectionButton.Click += async (_, _) => await TestAteraConnectionAsync().ConfigureAwait(true);
        panel.Controls.Add(_testAteraConnectionButton);

        _testSnipeConnectionButton.Text = "Test Snipe-IT";
        _testSnipeConnectionButton.Width = 120;
        _testSnipeConnectionButton.Click += async (_, _) => await TestSnipeConnectionAsync().ConfigureAwait(true);
        panel.Controls.Add(_testSnipeConnectionButton);

        _previewChangesButton.Text = "Preview Changes";
        _previewChangesButton.Width = 140;
        _previewChangesButton.Click += async (_, _) => await RunManualSyncAsync(previewOnly: true).ConfigureAwait(true);
        panel.Controls.Add(_previewChangesButton);

        _syncNowButton.Text = "Sync Now";
        _syncNowButton.Width = 110;
        _syncNowButton.Click += async (_, _) => await RunManualSyncAsync(previewOnly: false).ConfigureAwait(true);
        panel.Controls.Add(_syncNowButton);

        _cancelRunButton.Text = "Cancel";
        _cancelRunButton.Width = 90;
        _cancelRunButton.Enabled = false;
        _cancelRunButton.Click += (_, _) => _runCancellation?.Cancel();
        panel.Controls.Add(_cancelRunButton);

        _openPreflightFolderButton.Text = "Open CSV Folder";
        _openPreflightFolderButton.Width = 135;
        _openPreflightFolderButton.Enabled = false;
        _openPreflightFolderButton.Click += (_, _) => OpenPreflightFolder();
        panel.Controls.Add(_openPreflightFolderButton);

        return panel;
    }

    private TableLayoutPanel CreateProgressPanel()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            Padding = new Padding(0, 0, 0, 12)
        };

        _progressSummaryLabel.AutoSize = true;
        _progressSummaryLabel.Text = "Progress: idle";
        panel.Controls.Add(_progressSummaryLabel, 0, 0);

        _progressBar.Dock = DockStyle.Top;
        _progressBar.Height = 22;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Value = 0;
        _progressBar.Style = ProgressBarStyle.Continuous;
        panel.Controls.Add(_progressBar, 0, 1);

        _progressDetailLabel.AutoEllipsis = true;
        _progressDetailLabel.AutoSize = false;
        _progressDetailLabel.Dock = DockStyle.Top;
        _progressDetailLabel.Height = 24;
        _progressDetailLabel.Text = "No run in progress.";
        panel.Controls.Add(_progressDetailLabel, 0, 2);

        return panel;
    }

    private static void AddTextRow(
        TableLayoutPanel grid,
        string labelText,
        TextBox textBox,
        string defaultValue,
        bool password)
    {
        AddLabel(grid, labelText);
        textBox.Dock = DockStyle.Fill;
        textBox.Text = defaultValue;
        textBox.UseSystemPasswordChar = password;
        grid.Controls.Add(textBox, 1, grid.RowCount - 1);
    }

    private static void AddNumericRow(
        TableLayoutPanel grid,
        string labelText,
        NumericUpDown input,
        decimal minimum,
        decimal maximum,
        decimal value)
    {
        AddLabel(grid, labelText);
        input.Dock = DockStyle.Left;
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.Value = value;
        input.Width = 120;
        grid.Controls.Add(input, 1, grid.RowCount - 1);
    }

    private static void AddMultilineTextRow(
        TableLayoutPanel grid,
        string labelText,
        TextBox textBox)
    {
        AddLabel(grid, labelText);
        textBox.Dock = DockStyle.Fill;
        textBox.Multiline = true;
        textBox.ScrollBars = ScrollBars.Vertical;
        textBox.Height = 72;
        textBox.PlaceholderText = "Atera company=Snipe-IT company";
        grid.Controls.Add(textBox, 1, grid.RowCount - 1);
    }

    private static void AddDecimalRow(
        TableLayoutPanel grid,
        string labelText,
        NumericUpDown input,
        decimal value)
    {
        AddLabel(grid, labelText);
        input.DecimalPlaces = 2;
        input.Increment = 0.01M;
        input.Minimum = 0.01M;
        input.Maximum = 1.00M;
        input.Value = value;
        input.Width = 120;
        grid.Controls.Add(input, 1, grid.RowCount - 1);
    }

    private static void AddCheckRow(
        TableLayoutPanel grid,
        string labelText,
        CheckBox checkBox,
        bool isChecked)
    {
        AddLabel(grid, labelText);
        checkBox.Checked = isChecked;
        checkBox.AutoSize = true;
        grid.Controls.Add(checkBox, 1, grid.RowCount - 1);
    }

    private static void AddCheckedListRow(
        TableLayoutPanel grid,
        string labelText,
        CheckedListBox checkedListBox,
        IReadOnlyList<string> values)
    {
        AddLabel(grid, labelText);
        checkedListBox.CheckOnClick = true;
        checkedListBox.Height = 96;
        checkedListBox.IntegralHeight = false;
        checkedListBox.MultiColumn = true;
        checkedListBox.Dock = DockStyle.Fill;
        foreach (var value in values)
        {
            checkedListBox.Items.Add(value);
        }

        grid.Controls.Add(checkedListBox, 1, grid.RowCount - 1);
    }

    private static void AddLabel(TableLayoutPanel grid, string labelText)
    {
        var rowIndex = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 6),
            Text = labelText
        };
        grid.Controls.Add(label, 0, rowIndex);
    }

    /// <summary>
    /// Reloads reusable manual sync settings from local config into the UI without logging secrets.
    /// </summary>
    private async Task LoadManualSyncConfigAsync(bool logMissingConfig = true)
    {
        try
        {
            var settings = await _settingsStore.LoadManualSyncSettingsAsync(CancellationToken.None).ConfigureAwait(true);
            if (settings is null)
            {
                if (logMissingConfig)
                {
                    AppendLog("No saved manual sync config was found in local settings.");
                }

                return;
            }

            ApplyManualSyncSettings(settings);
            AppendLog("Loaded Manual Sync test configuration, including saved API credentials, from the local settings file.");
        }
        catch (Exception exception)
        {
            AppendLog($"Could not load local settings: {exception.Message}");
        }
    }

    private async Task SaveManualSyncConfigAsync()
    {
        try
        {
            var settings = BuildManualSyncSettings();
            await _settingsStore
                .SaveManualSyncSettingsAsync(settings, CancellationToken.None)
                .ConfigureAwait(true);
            AppendLog($"Saved Manual Sync test configuration, including API credentials, to {LocalAppSettingsStore.GetDefaultFilePath()}. Treat this plaintext file as sensitive.");
        }
        catch (Exception exception)
        {
            AppendLog($"Save failed: {exception.Message}");
        }
    }

    private void ApplyManualSyncSettings(ManualSyncSettings settings)
    {
        SetTextIfPresent(_ateraBaseUrlTextBox, settings.AteraBaseUrl);
        SetTextIfPresent(_ateraApiKeyTextBox, settings.AteraApiKey);
        SetTextIfPresent(_snipeBaseUrlTextBox, settings.SnipeItBaseUrl);
        SetTextIfPresent(_snipeApiTokenTextBox, settings.SnipeItApiToken);
        SetTextIfPresent(_defaultCompanyTextBox, settings.DefaultCompanyName);
        SetTextIfPresent(_defaultManufacturerTextBox, settings.DefaultManufacturerName);
        SetTextIfPresent(_defaultModelTextBox, settings.DefaultModelName);
        SetTextIfPresent(_defaultCategoryTextBox, settings.DefaultCategoryName);
        if (settings.ModelCategoriesToNormalize.Count > 0)
        {
            _modelCategoriesToNormalizeTextBox.Text = string.Join("; ", settings.ModelCategoriesToNormalize);
        }

        SetTextIfPresent(_macCustomFieldTextBox, settings.MacAddressCustomFieldDbColumnName);
        SetTextIfPresent(_macFieldsetNameTextBox, settings.MacAddressFieldsetName);
        _ignoredMacAddressesTextBox.Text = string.Join("; ", settings.IgnoredMacAddresses);
        if (settings.DefaultStatusId.HasValue)
        {
            SetNumericIfInRange(_defaultStatusIdInput, settings.DefaultStatusId.Value);
        }

        if (settings.NameMatchThreshold.HasValue)
        {
            SetNumericIfInRange(_nameMatchThresholdInput, Convert.ToDecimal(settings.NameMatchThreshold.Value));
        }

        if (settings.CreateMissingCompanies.HasValue)
        {
            _createMissingCompaniesCheckBox.Checked = settings.CreateMissingCompanies.Value;
        }

        if (settings.CreateMissingModels.HasValue)
        {
            _createMissingModelsCheckBox.Checked = settings.CreateMissingModels.Value;
        }

        ApplyIgnoredDeviceTypes(settings.IgnoredDeviceTypes);

        if (settings.CompanyAliases.Count > 0)
        {
            _companyAliasesTextBox.Text = string.Join(
                Environment.NewLine,
                settings.CompanyAliases.Select(alias => $"{alias.Key}={alias.Value}"));
        }

        if (settings.ManufacturerAliases.Count > 0)
        {
            _manufacturerAliasesTextBox.Text = string.Join(
                Environment.NewLine,
                settings.ManufacturerAliases.Select(alias => $"{alias.Key}={alias.Value}"));
        }
    }

    private void ApplyIgnoredDeviceTypes(IReadOnlyList<string> ignoredDeviceTypes)
    {
        var normalizedIgnoredDeviceTypes = new HashSet<string>(
            ignoredDeviceTypes
                .Select(deviceType => deviceType?.Trim())
                .Where(deviceType => !string.IsNullOrWhiteSpace(deviceType))
                .Select(deviceType => deviceType!),
            StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < _ignoredDeviceTypesCheckedListBox.Items.Count; index++)
        {
            var deviceType = _ignoredDeviceTypesCheckedListBox.Items[index].ToString();
            var isChecked = deviceType is not null && normalizedIgnoredDeviceTypes.Remove(deviceType);
            _ignoredDeviceTypesCheckedListBox.SetItemChecked(index, isChecked);
        }

        foreach (var extraDeviceType in normalizedIgnoredDeviceTypes)
        {
            _ignoredDeviceTypesCheckedListBox.Items.Add(extraDeviceType, isChecked: true);
        }
    }

    private static void SetTextIfPresent(TextBox textBox, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            textBox.Text = value;
        }
    }

    private static void SetNumericIfInRange(NumericUpDown input, decimal value)
    {
        if (value >= input.Minimum && value <= input.Maximum)
        {
            input.Value = value;
        }
    }

    /// <summary>
    /// Verifies the Atera credentials by running the existing inventory pull path without touching Snipe-IT.
    /// </summary>
    private async Task TestAteraConnectionAsync()
    {
        if (_runCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        ResetProgress("Testing Atera connection...");
        SetRunningState(isRunning: true);

        try
        {
            var ateraBaseUri = ApiEndpointValidator.ValidateAteraBaseUri(RequireText(_ateraBaseUrlTextBox, "Atera API Base URL"));
            var apiKey = RequireText(_ateraApiKeyTextBox, "Atera API Key");
            var progress = new Progress<SyncProgressUpdate>(HandleProgressUpdate);

            AppendLog("Testing Atera connection with the existing inventory pull path...");
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            var client = new AteraClient(
                httpClient,
                new AteraPullOptions
                {
                    BaseUri = ateraBaseUri,
                    MaxRetryAttempts = 0,
                    RetryDelay = TimeSpan.Zero,
                    ItemsPerPage = 1,
                    MaxPages = 1
                },
                new SystemAteraClock(),
                NullLogger<AteraClient>.Instance);

            var result = await client
                .PullInventoryAsync(new AteraPullRequest { ApiKey = apiKey }, cancellation.Token, progress)
                .ConfigureAwait(true);

            HandleProgressUpdate(new SyncProgressUpdate
            {
                Stage = "AteraPull",
                Message = "Atera connection test completed.",
                Percent = 100
            });
            AppendLog($"Atera connection OK. Pulled {result.Summary.AgentCount} agent(s). Warnings: {result.Warnings.Count}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            MarkProgressStopped("Atera connection test canceled.");
            AppendLog("Atera connection test canceled.");
        }
        catch (Exception exception)
        {
            MarkProgressStopped("Atera connection test failed.");
            AppendLog($"Atera connection failed: {exception.Message}");
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            SetRunningState(isRunning: false);
        }
    }

    /// <summary>
    /// Verifies Snipe-IT connectivity with a documented read-only hardware lookup and never mutates assets.
    /// </summary>
    private async Task TestSnipeConnectionAsync()
    {
        if (_runCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        ResetProgress("Testing Snipe-IT connection...");
        SetRunningState(isRunning: true);

        try
        {
            var baseUri = RequireSnipeBaseUri();
            var apiToken = RequireText(_snipeApiTokenTextBox, "Snipe-IT API Token");

            HandleProgressUpdate(new SyncProgressUpdate
            {
                Stage = "SnipeImport",
                Message = "Sending read-only Snipe-IT GET /hardware?limit=1.",
                Percent = 50
            });
            AppendLog("Testing Snipe-IT connection with GET /hardware?limit=1...");
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildSnipeUri(baseUri, "hardware?limit=1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
                .ConfigureAwait(true);
            var content = await response.Content.ReadAsStringAsync(cancellation.Token).ConfigureAwait(true);

            if (!response.IsSuccessStatusCode)
            {
                MarkProgressStopped("Snipe-IT connection test failed.");
                AppendLog($"Snipe-IT connection failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
                return;
            }

            var businessError = TryReadSnipeBusinessError(content);
            if (businessError is not null)
            {
                MarkProgressStopped("Snipe-IT connection test failed.");
                AppendLog($"Snipe-IT connection failed: {businessError}");
                return;
            }

            HandleProgressUpdate(new SyncProgressUpdate
            {
                Stage = "SnipeImport",
                Message = "Snipe-IT connection test completed.",
                Percent = 100
            });
            AppendLog("Snipe-IT connection OK. GET /hardware?limit=1 returned a successful response.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            MarkProgressStopped("Snipe-IT connection test canceled.");
            AppendLog("Snipe-IT connection test canceled.");
        }
        catch (Exception exception)
        {
            MarkProgressStopped("Snipe-IT connection test failed.");
            AppendLog($"Snipe-IT connection failed: {exception.Message}");
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            SetRunningState(isRunning: false);
        }
    }

    /// <summary>
    /// Scans all Snipe-IT models, confirms every non-target model, and updates them to the UI default category.
    /// </summary>
    private async Task NormalizeModelCategoriesAsync()
    {
        if (_runCancellation is not null)
        {
            return;
        }

        var logPath = ModelCategoryNormalizationLog.CreatePath(
            LocalAppSettingsStore.GetDefaultLogDirectory(),
            DateTimeOffset.Now);
        var operationLogAvailable = true;
        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        ResetProgress("Scanning model categories...");
        SetRunningState(isRunning: true);

        async Task WriteOperationLogAsync(string message, bool showInUi = true)
        {
            if (showInUi)
            {
                AppendLog(message);
            }

            if (!operationLogAvailable)
            {
                return;
            }

            try
            {
                await ModelCategoryNormalizationLog
                    .AppendAsync(logPath, message, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                operationLogAvailable = false;
                AppendLog($"Could not write category normalization log: {exception.Message}");
            }
        }

        try
        {
            var sourceCategoryNames = RequireSemicolonSeparatedValues(
                _modelCategoriesToNormalizeTextBox,
                "Normalize From Categories");

            var options = new SnipeModelCategoryNormalizationOptions
            {
                BaseUrl = RequireSnipeBaseUri().AbsoluteUri.TrimEnd('/'),
                ApiToken = RequireText(_snipeApiTokenTextBox, "Snipe-IT API Token"),
                TargetCategoryName = RequireText(_defaultCategoryTextBox, "Default Category"),
                SourceCategoryNames = sourceCategoryNames
            };
            await WriteOperationLogAsync(
                $"BEGIN model category normalization. Source categories: {string.Join(", ", sourceCategoryNames)}. Target category: {options.TargetCategoryName}.")
                .ConfigureAwait(true);

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            var normalizer = new SnipeModelCategoryNormalizer(
                httpClient,
                NullLogger<SnipeModelCategoryNormalizer>.Instance);
            var progress = new Progress<SyncProgressUpdate>(HandleProgressUpdate);
            var plan = await normalizer
                .PlanAsync(options, cancellation.Token, progress)
                .ConfigureAwait(true);

            await WriteOperationLogAsync(
                $"SCAN complete. Models scanned={plan.ScannedModelCount}; source categories={string.Join(", ", plan.SourceCategoryNames)}; candidate models={plan.Models.Count}; target category={plan.TargetCategoryName} (id {plan.TargetCategoryId}).")
                .ConfigureAwait(true);
            foreach (var candidate in plan.Models)
            {
                await WriteOperationLogAsync(
                    $"PLAN model id={candidate.ModelId}; name='{candidate.ModelName}'; category='{candidate.SourceCategoryName}' -> '{plan.TargetCategoryName}'.",
                    showInUi: false).ConfigureAwait(true);
            }

            if (!operationLogAvailable)
            {
                MarkProgressStopped("Category normalization stopped because its audit log is unavailable.");
                return;
            }

            if (plan.Models.Count == 0)
            {
                HandleProgressUpdate(new SyncProgressUpdate
                {
                    Stage = "CategoryNormalize",
                    Message = $"No models in {string.Join(", ", plan.SourceCategoryNames)} require a change to '{plan.TargetCategoryName}'.",
                    Percent = 100
                });
                await WriteOperationLogAsync("END success. No candidate models; no PUT requests were sent.")
                    .ConfigureAwait(true);
                return;
            }

            var confirmation = MessageBox.Show(
                this,
                $"The scan read {plan.ScannedModelCount} models.\n\n"
                + $"Source categories: {string.Join(", ", plan.SourceCategoryNames)}.\n"
                + $"{plan.Models.Count} matching models currently use another category.\n"
                + $"Their category will be changed to '{plan.TargetCategoryName}'.\n\n"
                + "Categories not listed above will not be changed. Category belongs to the Model, "
                + "so each update affects every asset using that Model. Continue?",
                "Confirm Model Category Normalization",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                MarkProgressStopped("Category normalization canceled before any updates.");
                await WriteOperationLogAsync("END canceled by operator before any PUT request.")
                    .ConfigureAwait(true);
                return;
            }

            await WriteOperationLogAsync("Operator confirmed model category updates.")
                .ConfigureAwait(true);
            if (!operationLogAvailable)
            {
                MarkProgressStopped("Category normalization stopped because its audit log is unavailable.");
                return;
            }

            var result = await normalizer
                .ExecuteAsync(plan, options, cancellation.Token, progress)
                .ConfigureAwait(true);
            foreach (var outcome in result.Outcomes)
            {
                var detail = outcome.Success
                    ? "SUCCESS"
                    : $"FAILED code={outcome.ErrorCode}; reason={outcome.ErrorMessage}";
                await WriteOperationLogAsync(
                    $"{detail}; model id={outcome.ModelId}; name='{outcome.ModelName}'; category='{outcome.SourceCategoryName}' -> '{outcome.TargetCategoryName}'.")
                    .ConfigureAwait(true);
            }

            var summary = $"Category normalization summary: updated models={result.UpdatedModelCount}; failed models={result.FailedModelCount}; canceled={result.Cancelled}.";
            await WriteOperationLogAsync(summary).ConfigureAwait(true);
            if (result.Success)
            {
                HandleProgressUpdate(new SyncProgressUpdate
                {
                    Stage = "CategoryNormalize",
                    Message = "Model category normalization completed successfully.",
                    Percent = 100
                });
                await WriteOperationLogAsync("END success.", showInUi: false).ConfigureAwait(true);
            }
            else
            {
                MarkProgressStopped(result.Cancelled
                    ? "Category normalization canceled with a partial result."
                    : "Category normalization finished with model update failures.");
                await WriteOperationLogAsync(
                    result.Cancelled ? "END partial/canceled." : "END with failures.",
                    showInUi: false).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            MarkProgressStopped("Category normalization canceled during the read-only scan.");
            await WriteOperationLogAsync("END canceled during the read-only scan; no pending PUT was interrupted.")
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MarkProgressStopped("Category normalization failed.");
            await WriteOperationLogAsync($"END failed: {exception.Message}").ConfigureAwait(true);
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            SetRunningState(isRunning: false);
            AppendLog(operationLogAvailable
                ? $"Category normalization log: {logPath}"
                : $"Category normalization log could not be completed. Intended path: {logPath}");
        }
    }

    /// <summary>
    /// Runs a preview or real manual sync from UI-provided settings, preserving secrets and reporting only safe summaries.
    /// </summary>
    private async Task RunManualSyncAsync(bool previewOnly)
    {
        if (_runCancellation is not null)
        {
            return;
        }

        if (!previewOnly && !ConfirmRealSync())
        {
            return;
        }

        _lastPreflightDirectory = previewOnly
            ? LocalAppSettingsStore.GetDefaultManualPreflightDirectory(CreateRunId())
            : null;

        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        ResetProgress(previewOnly ? "Preparing preview run..." : "Preparing real manual sync...");
        SetRunningState(isRunning: true);

        try
        {
            var baseRequest = BuildBaseRequest(out var ateraBaseUri);
            var request = previewOnly
                ? ManualSyncRequestFactory.CreatePreviewChangesRequest(baseRequest, _lastPreflightDirectory!)
                : ManualSyncRequestFactory.CreateSyncNowRequest(baseRequest);

            var uiStageTracker = new ManualSyncUiStageTracker();
            AppendFileLog(previewOnly
                ? $"Starting preview. CSV folder: {_lastPreflightDirectory}"
                : "Starting real manual sync.");
            AppendUiLog(uiStageTracker.Start());
            var progressCalculator = new ManualSyncProgressCalculator(previewOnly);

            var progress = new Progress<SyncProgressUpdate>(update =>
            {
                HandleProgressUpdate(update, progressCalculator);
                var uiMilestone = uiStageTracker.Observe(update);
                if (uiMilestone is not null)
                {
                    AppendUiLog(uiMilestone);
                }
            });
            var result = await RunPipelineAsync(request, ateraBaseUri, progress, cancellation.Token).ConfigureAwait(true);
            AppendDetailedResult(result, previewOnly ? _lastPreflightDirectory : null);
            foreach (var uiMilestone in uiStageTracker.Complete())
            {
                AppendUiLog(uiMilestone);
            }

            var savedReportPath = await SaveRunReportAsync(result, CancellationToken.None).ConfigureAwait(true);
            AppendErrorResult(result, previewOnly, savedReportPath);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            MarkProgressStopped("Run canceled.");
            AppendFileLog("Run canceled.");
            AppendUiLog("Sync canceled.");
        }
        catch (Exception exception)
        {
            MarkProgressStopped("Run failed before completion.");
            var failureMessage = $"Run failed before completion: {exception.Message}";
            AppendFileLog(failureMessage);
            AppendUiLog("Sync failed.");
            AppendStandaloneError(previewOnly ? "Preview" : "Sync", failureMessage);
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            SetRunningState(isRunning: false);
        }
    }

    private SyncRunRequest BuildBaseRequest(out Uri ateraBaseUri)
    {
        var settings = BuildManualSyncSettings();
        ateraBaseUri = new Uri(settings.AteraBaseUrl!, UriKind.Absolute);

        return new SyncRunRequest
        {
            Atera = new AteraPullRequest
            {
                ApiKey = settings.AteraApiKey!
            },
            Mapping = new MappingOptions
            {
                DefaultCompanyName = settings.DefaultCompanyName!,
                DefaultManufacturerName = settings.DefaultManufacturerName!,
                DefaultModelName = settings.DefaultModelName!,
                DefaultCategoryName = settings.DefaultCategoryName!,
                DefaultStatusId = settings.DefaultStatusId!.Value,
                CompanyAliases = settings.CompanyAliases,
                ManufacturerAliases = settings.ManufacturerAliases,
                IgnoredDeviceTypes = settings.IgnoredDeviceTypes,
                IgnoredMacAddresses = settings.IgnoredMacAddresses
            },
            SnipeIt = new SnipeImportOptions
            {
                BaseUrl = settings.SnipeItBaseUrl!,
                ApiToken = settings.SnipeItApiToken!,
                DryRun = true,
                CreateMissingCompanies = settings.CreateMissingCompanies ?? true,
                CreateMissingModels = settings.CreateMissingModels ?? false,
                MacAddressCustomFieldDbColumnName = settings.MacAddressCustomFieldDbColumnName,
                MacAddressFieldsetName = settings.MacAddressFieldsetName,
                ModelCategoryNormalizationTargetName = settings.DefaultCategoryName,
                ModelCategoriesToNormalize = settings.ModelCategoriesToNormalize,
                IgnoredMacAddresses = settings.IgnoredMacAddresses,
                NameMatchThreshold = settings.NameMatchThreshold!.Value,
                ManualPreflightCsvEnabled = false,
                ManualPreflightCsvDirectory = null
            },
            Sync = new SyncRunOptions
            {
                DryRun = true,
                TriggeredBy = "manual-preview"
            }
        };
    }

    private ManualSyncSettings BuildManualSyncSettings()
    {
        var macCustomFieldDbColumnName = ReadOptionalText(_macCustomFieldTextBox);
        var macAddressFieldsetName = ReadOptionalText(_macFieldsetNameTextBox);
        if (string.IsNullOrWhiteSpace(macCustomFieldDbColumnName)
            != string.IsNullOrWhiteSpace(macAddressFieldsetName))
        {
            throw new InvalidOperationException(
                "MAC Custom Field DB Column and MAC Fieldset Name must be configured together.");
        }

        return new ManualSyncSettings
        {
            AteraBaseUrl = ApiEndpointValidator.ValidateAteraBaseUri(RequireText(_ateraBaseUrlTextBox, "Atera API Base URL")).AbsoluteUri.TrimEnd('/'),
            AteraApiKey = RequireText(_ateraApiKeyTextBox, "Atera API Key"),
            SnipeItBaseUrl = RequireSnipeBaseUri().AbsoluteUri.TrimEnd('/'),
            SnipeItApiToken = RequireText(_snipeApiTokenTextBox, "Snipe-IT API Token"),
            DefaultCompanyName = RequireText(_defaultCompanyTextBox, "Default Company"),
            DefaultManufacturerName = RequireText(_defaultManufacturerTextBox, "Default Manufacturer"),
            DefaultModelName = RequireText(_defaultModelTextBox, "Default Model"),
            DefaultCategoryName = RequireText(_defaultCategoryTextBox, "Default Category"),
            ModelCategoriesToNormalize = RequireSemicolonSeparatedValues(
                _modelCategoriesToNormalizeTextBox,
                "Normalize From Categories"),
            DefaultStatusId = Convert.ToInt32(_defaultStatusIdInput.Value),
            CompanyAliases = ParseCompanyAliases(_companyAliasesTextBox.Text),
            ManufacturerAliases = ParseManufacturerAliases(_manufacturerAliasesTextBox.Text),
            IgnoredDeviceTypes = ReadCheckedDeviceTypes(_ignoredDeviceTypesCheckedListBox),
            MacAddressCustomFieldDbColumnName = macCustomFieldDbColumnName,
            MacAddressFieldsetName = macAddressFieldsetName,
            IgnoredMacAddresses = ParseSemicolonSeparatedValues(_ignoredMacAddressesTextBox.Text),
            NameMatchThreshold = Convert.ToDouble(_nameMatchThresholdInput.Value),
            CreateMissingCompanies = _createMissingCompaniesCheckBox.Checked,
            CreateMissingModels = _createMissingModelsCheckBox.Checked
        };
    }

    private async Task<SyncRunResult> RunPipelineAsync(
        SyncRunRequest request,
        Uri ateraBaseUri,
        IProgress<SyncProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        using var ateraHttpClient = new HttpClient();
        using var snipeHttpClient = new HttpClient();
        var ateraClient = new AteraClient(
            ateraHttpClient,
            new AteraPullOptions
            {
                BaseUri = ateraBaseUri,
                MaxRetryAttempts = 2,
                RetryDelay = TimeSpan.FromSeconds(2)
            },
            new SystemAteraClock(),
            NullLogger<AteraClient>.Instance);
        var orchestrator = new SyncOrchestrator(
            ateraClient,
            new InventoryMapper(),
            new SnipeImporter(snipeHttpClient, NullLogger<SnipeImporter>.Instance),
            NullLogger<SyncOrchestrator>.Instance);

        return await orchestrator.RunOnceAsync(request, cancellationToken, progress).ConfigureAwait(false);
    }

    private void AppendDetailedResult(SyncRunResult result, string? preflightDirectory)
    {
        var isPreview = preflightDirectory is not null;
        AppendFileLog(result.Success
            ? $"{(isPreview ? "Preview" : "Run")} finished successfully."
            : $"{(isPreview ? "Preview" : "Run")} finished with failures.");
        AppendFileLog($"Pulled agents: {result.PullResult?.Summary.AgentCount ?? 0}");
        AppendFileLog($"Mapped assets: {result.ImportBatch?.Summary.MappedAssetCount ?? 0}");

        if (result.ImportResult is not null)
        {
            if (isPreview)
            {
                AppendFileLog(
                    $"Preview summary: planned creates={result.ImportResult.CreatedAssets}, planned updates={result.ImportResult.UpdatedAssets}, blocked assets={result.ImportResult.FailedAssets}.");
                AppendFileLog(
                    $"Planned reference changes: companies={result.ImportResult.CreatedCompanies}, categories={result.ImportResult.CreatedCategories}, models added={result.ImportResult.CreatedModels}, models updated={result.ImportResult.UpdatedModels}.");
            }
            else
            {
                AppendFileLog(
                    $"Import summary: created assets={result.ImportResult.CreatedAssets}, updated assets={result.ImportResult.UpdatedAssets}, failed assets={result.ImportResult.FailedAssets}.");
                AppendFileLog(
                    $"Reference changes: companies created={result.ImportResult.CreatedCompanies}, categories created={result.ImportResult.CreatedCategories}, models created={result.ImportResult.CreatedModels}, models updated={result.ImportResult.UpdatedModels}.");
            }
        }

        AppendFileLog($"Warnings: {result.Warnings.Count}; failures: {result.Failures.Count}.");

        foreach (var warning in result.Warnings)
        {
            AppendFileLog($"Warning {warning.Code}: {warning.Message}");
        }

        var failureReasonLines = BuildFailureReasonLines(result);
        foreach (var line in failureReasonLines)
        {
            AppendFileLog(line);
        }

        if (preflightDirectory is not null)
        {
            AppendFileLog($"Preflight CSV folder: {preflightDirectory}");
        }
    }

    /// <summary>
    /// Builds distinct safe failure lines, using pre-orchestration import messages so shared reference failures group across different asset targets.
    /// </summary>
    private static IReadOnlyList<string> BuildFailureReasonLines(SyncRunResult result)
    {
        if (result.ImportResult?.Failures is { Count: > 0 } importFailures)
        {
            return importFailures
                .GroupBy(failure => new { failure.Code, failure.Message })
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.Code, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Message, StringComparer.Ordinal)
                .Select(group =>
                {
                    var targetExamples = string.Join(
                        ", ",
                        group
                            .Select(failure => $"{failure.TargetType} '{failure.TargetName}'")
                            .Distinct(StringComparer.Ordinal)
                            .Take(3));
                    var remainingTargets = Math.Max(0, group.Count() - 3);
                    var remainingSuffix = remainingTargets == 0
                        ? string.Empty
                        : $", +{remainingTargets} more";
                    return $"Failure x{group.Count()} SnipeImport/{group.Key.Code}: {group.Key.Message} Affected: {targetExamples}{remainingSuffix}.";
                })
                .ToList();
        }

        return result.Failures
            .GroupBy(failure => new { failure.Stage, failure.Code, failure.Message })
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Stage, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Code, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Message, StringComparer.Ordinal)
            .Select(group =>
                $"Failure x{group.Count()} {group.Key.Stage}/{group.Key.Code}: {group.Key.Message}")
            .ToList();
    }

    /// <summary>
    /// Queues one complete, sanitized failure block for a completed failed Preview or Sync run.
    /// </summary>
    private void AppendErrorResult(SyncRunResult result, bool previewOnly, string? savedReportPath)
    {
        if (result.Success)
        {
            return;
        }

        var timestamp = DateTimeOffset.Now;
        var mode = previewOnly ? "Preview" : "Sync";
        var builder = new StringBuilder();
        builder.AppendLine($"{timestamp:yyyy-MM-dd HH:mm:ss zzz} BEGIN FAILED {mode}");
        builder.AppendLine($"Run UTC: {result.StartedAt.ToUniversalTime():O} to {result.FinishedAt.ToUniversalTime():O}");
        builder.AppendLine(
            $"Summary: pulled={result.PullResult?.Summary.AgentCount ?? 0}; mapped={result.ImportBatch?.Summary.MappedAssetCount ?? 0}; warnings={result.Warnings.Count}; failures={result.Failures.Count}.");

        var failureLines = BuildFailureReasonLines(result);
        if (failureLines.Count == 0)
        {
            builder.AppendLine("Failure: The run failed without a structured failure reason.");
        }
        else
        {
            foreach (var failureLine in failureLines)
            {
                builder.AppendLine(failureLine);
            }
        }

        builder.AppendLine(savedReportPath is null
            ? "History report: unavailable."
            : $"History report: {savedReportPath}");
        builder.AppendLine($"{timestamp:yyyy-MM-dd HH:mm:ss zzz} END FAILED {mode}");
        builder.AppendLine();
        QueueErrorLog(timestamp, builder.ToString());
    }

    /// <summary>
    /// Records a sanitized failure that occurs before the pipeline can return a structured run result.
    /// </summary>
    private void AppendStandaloneError(string operation, string message)
    {
        var timestamp = DateTimeOffset.Now;
        var block = $"{timestamp:yyyy-MM-dd HH:mm:ss zzz} {operation} failure before structured result: {message}{Environment.NewLine}{Environment.NewLine}";
        QueueErrorLog(timestamp, block);
    }

    private void QueueErrorLog(DateTimeOffset timestamp, string block)
    {
        if (_dailyErrorLogWriter.TryWrite(timestamp, block))
        {
            AppendFileLog($"Dedicated error log: {_dailyErrorLogWriter.GetLogPath(timestamp)}");
            return;
        }

        if (_dailyErrorLogWriteFailureReported)
        {
            return;
        }

        _dailyErrorLogWriteFailureReported = true;
        var detail = _dailyErrorLogWriter.LastError?.Message ?? "the daily error-log writer is unavailable";
        AppendFileLog($"Could not queue dedicated error log: {detail}");
    }

    /// <summary>
    /// Persists the completed manual run result and returns the saved history report path when discoverable.
    /// </summary>
    private async Task<string?> SaveRunReportAsync(
        SyncRunResult result,
        CancellationToken cancellationToken)
    {
        var options = new SyncStatusStoreOptions();
        try
        {
            var statusStore = new JsonFileSyncStatusStore(options, NullLogger<JsonFileSyncStatusStore>.Instance);
            await statusStore.SaveAsync(result, cancellationToken).ConfigureAwait(true);
            var savedReportPath = FindLatestRunReportPath(options.HistoryDirectoryPath);
            AppendFileLog(savedReportPath is null
                ? $"Saved run report/status history under: {Path.GetFullPath(options.HistoryDirectoryPath)}"
                : $"Saved run report/status history: {savedReportPath}");
            return savedReportPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppendFileLog($"Could not save run report/status history: {exception.Message}");
            return null;
        }
    }

    private static string? FindLatestRunReportPath(string historyDirectoryPath)
    {
        try
        {
            return Directory.Exists(historyDirectoryPath)
                ? Directory
                    .EnumerateFiles(historyDirectoryPath, "SyncResult_*.json", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string RequireText(TextBox textBox, string fieldName)
    {
        var value = textBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{fieldName} is required.")
            : value;
    }

    private static string? ReadOptionalText(TextBox textBox)
    {
        var value = textBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Splits an operator-entered semicolon list while preserving values for Core validation and normalization.
    /// </summary>
    private static IReadOnlyList<string> ParseSemicolonSeparatedValues(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>
    /// Parses one required semicolon-delimited UI field and fails before any API or settings write when it is empty.
    /// </summary>
    private static IReadOnlyList<string> RequireSemicolonSeparatedValues(TextBox textBox, string fieldName)
    {
        var values = ParseSemicolonSeparatedValues(textBox.Text);
        return values.Count == 0
            ? throw new InvalidOperationException($"{fieldName} requires at least one category.")
            : values;
    }

    private static IReadOnlyList<string> ReadCheckedDeviceTypes(CheckedListBox checkedListBox)
    {
        var selectedDeviceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in checkedListBox.CheckedItems)
        {
            var deviceType = item?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(deviceType))
            {
                selectedDeviceTypes.Add(deviceType);
            }
        }

        return selectedDeviceTypes.ToList();
    }

    private static Uri RequireAbsoluteUri(TextBox textBox, string fieldName)
    {
        var value = RequireText(textBox, fieldName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException($"{fieldName} must be an absolute URL.");
    }

    /// <summary>
    /// Parses operator-entered company aliases without persisting them or calling external systems.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseCompanyAliases(string? aliasesText)
    {
        return ParseAliases(
            aliasesText,
            "Company",
            "Atera company=Snipe-IT company");
    }

    /// <summary>
    /// Parses operator-entered manufacturer aliases without fuzzy matching or external lookups.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseManufacturerAliases(string? aliasesText)
    {
        return ParseAliases(
            aliasesText,
            "Manufacturer",
            "Atera manufacturer=Snipe-IT manufacturer");
    }

    /// <summary>
    /// Parses deterministic one-way aliases shared by company and manufacturer UI fields.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseAliases(
        string? aliasesText,
        string aliasType,
        string expectedSyntax)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(aliasesText))
        {
            return aliases;
        }

        foreach (var rawLine in aliasesText.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Contains("=>", StringComparison.Ordinal)
                || separatorIndex <= 0
                || separatorIndex != line.LastIndexOf("=", StringComparison.Ordinal)
                || separatorIndex + 1 >= line.Length)
            {
                throw new InvalidOperationException(
                    $"{aliasType} alias line '{line}' must use '{expectedSyntax}'.");
            }

            var source = line[..separatorIndex].Trim();
            var target = line[(separatorIndex + 1)..].Trim();
            if (source.Length == 0 || target.Length == 0)
            {
                throw new InvalidOperationException(
                    $"{aliasType} alias line '{line}' must include both source and target names.");
            }

            aliases[source] = target;
        }

        return aliases;
    }

    private Uri RequireSnipeBaseUri()
    {
        var uri = RequireAbsoluteUri(_snipeBaseUrlTextBox, "Snipe-IT API Base URL");
        if (string.Equals(uri.Host, ExampleSnipeHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Snipe-IT API Base URL is still using the example placeholder. Enter your real Snipe-IT URL, including /api/v1.");
        }

        return ApiEndpointValidator.ValidateSnipeBaseUri(uri.AbsoluteUri);
    }

    private static Uri BuildSnipeUri(Uri baseUri, string relativePath)
    {
        var normalizedBase = baseUri.AbsoluteUri.TrimEnd('/') + "/";
        return new Uri(new Uri(normalizedBase), relativePath.TrimStart('/'));
    }

    private static string? TryReadSnipeBusinessError(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                return root.TryGetProperty("messages", out var messages)
                    ? $"Snipe-IT returned error status: {messages}"
                    : "Snipe-IT returned error status.";
            }
        }
        catch (JsonException)
        {
            return "Snipe-IT returned a successful HTTP status but the response was not valid JSON.";
        }

        return null;
    }

    private bool ConfirmRealSync()
    {
        return MessageBox.Show(
            this,
            "Sync Now can create or update Snipe-IT assets. Continue?",
            "Confirm Real Sync",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void ResetProgress(string message)
    {
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = 0;
        _progressSummaryLabel.Text = "Progress: 0%";
        _progressDetailLabel.Text = message;
    }

    private void MarkProgressStopped(string message)
    {
        _progressSummaryLabel.Text = $"Progress: stopped at {_progressBar.Value}%";
        _progressDetailLabel.Text = message;
    }

    private void HandleProgressUpdate(SyncProgressUpdate update)
    {
        HandleProgressUpdate(update, manualSyncCalculator: null);
    }

    private void HandleProgressUpdate(
        SyncProgressUpdate update,
        ManualSyncProgressCalculator? manualSyncCalculator)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => HandleProgressUpdate(update, manualSyncCalculator));
            return;
        }

        var value = manualSyncCalculator?.Calculate(update) ?? CalculateProgressValue(update);
        _progressBar.Value = value;

        var detail = BuildProgressDetail(update);
        _progressSummaryLabel.Text = $"Progress: {value}% - {update.Stage}";
        _progressDetailLabel.Text = detail;
        AppendFileLog($"Progress {update.Stage}: {detail}");
    }

    private int CalculateProgressValue(SyncProgressUpdate update)
    {
        if (update.Percent is { } percent)
        {
            return Math.Clamp(percent, _progressBar.Minimum, _progressBar.Maximum);
        }

        if (update.Current is not { } current || update.Total is not { } total || total <= 0)
        {
            return update.Stage switch
            {
                "AteraPull" => Math.Max(_progressBar.Value, 5),
                "SnipeImport" => Math.Max(_progressBar.Value, 50),
                _ => _progressBar.Value
            };
        }

        var ratio = Math.Clamp(current / (double)total, 0D, 1D);
        var calculated = update.Stage switch
        {
            "AteraPull" => 5 + (int)Math.Round(ratio * 30D),
            "SnipeImport" => 50 + (int)Math.Round(ratio * 45D),
            _ => _progressBar.Value
        };

        return Math.Clamp(Math.Max(_progressBar.Value, calculated), _progressBar.Minimum, _progressBar.Maximum);
    }

    private static string BuildProgressDetail(SyncProgressUpdate update)
    {
        return update.Current is { } current && update.Total is { } total && total > 0
            ? $"{update.Message} ({current}/{total})"
            : update.Message;
    }

    private void SetRunningState(bool isRunning)
    {
        _loadConfigButton.Enabled = !isRunning;
        _saveConfigButton.Enabled = !isRunning;
        _testAteraConnectionButton.Enabled = !isRunning;
        _testSnipeConnectionButton.Enabled = !isRunning;
        _normalizeModelCategoriesButton.Enabled = !isRunning;
        _previewChangesButton.Enabled = !isRunning;
        _syncNowButton.Enabled = !isRunning;
        _cancelRunButton.Enabled = isRunning;
        _openPreflightFolderButton.Enabled = !isRunning
            && _lastPreflightDirectory is not null
            && Directory.Exists(_lastPreflightDirectory);
        _progressBar.Style = ProgressBarStyle.Continuous;
    }

    private void OpenPreflightFolder()
    {
        if (_lastPreflightDirectory is null || !Directory.Exists(_lastPreflightDirectory))
        {
            AppendLog("No preflight CSV folder is available yet.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _lastPreflightDirectory,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Writes one general-purpose log line to both the UI and the ProgramData daily manual sync log file.
    /// </summary>
    private void AppendLog(string message)
    {
        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        _logTextBox.AppendText(line);
        QueueDailyLog(now, line);
    }

    /// <summary>
    /// Writes a concise milestone to the on-screen log without duplicating it into the detailed file log.
    /// </summary>
    private void AppendUiLog(string message)
    {
        _logTextBox.AppendText($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
    }

    /// <summary>
    /// Queues one detailed line for the daily file without exposing it in the on-screen log.
    /// </summary>
    private void AppendFileLog(string message)
    {
        var now = DateTimeOffset.Now;
        QueueDailyLog(now, $"{now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
    }

    private void QueueDailyLog(DateTimeOffset timestamp, string line)
    {
        if (!_dailyLogWriter.TryWrite(timestamp, line) && !_dailyLogWriteFailureReported)
        {
            _dailyLogWriteFailureReported = true;
            var detail = _dailyLogWriter.LastError?.Message ?? "the daily log writer is unavailable";
            _logTextBox.AppendText($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Could not queue ProgramData daily log: {detail}{Environment.NewLine}");
        }
    }

    private static string CreateRunId()
    {
        return DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    }
}
