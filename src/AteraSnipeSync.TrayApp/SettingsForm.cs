using AteraSnipeSync.Core.Configuration;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Provides the first TrayApp settings surface for saving local API credentials.
/// </summary>
public partial class SettingsForm : Form
{
    private readonly LocalAppSettingsStore _settingsStore;

    public SettingsForm()
        : this(new LocalAppSettingsStore(LocalAppSettingsStore.GetDefaultFilePath()))
    {
    }

    public SettingsForm(LocalAppSettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);

        _settingsStore = settingsStore;
        InitializeComponent();
    }

    /// <summary>
    /// Loads any saved Atera API key into the password field without calling external APIs.
    /// </summary>
    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await LoadSavedAteraApiKeyAsync().ConfigureAwait(true);
    }

    private async Task LoadSavedAteraApiKeyAsync()
    {
        try
        {
            var apiKey = await _settingsStore.LoadAteraApiKeyAsync(CancellationToken.None).ConfigureAwait(true);
            if (apiKey is not null)
            {
                ateraApiKeyTextBox.Text = apiKey;
                statusLabel.Text = "Atera API key loaded.";
            }
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Could not load local settings: {exception.Message}";
        }
    }

    private async void SaveButton_Click(object? sender, EventArgs e)
    {
        await SaveAteraApiKeyAsync().ConfigureAwait(true);
    }

    private async Task SaveAteraApiKeyAsync()
    {
        saveButton.Enabled = false;
        statusLabel.Text = "Saving...";

        try
        {
            await _settingsStore.SaveAteraApiKeyAsync(
                ateraApiKeyTextBox.Text,
                CancellationToken.None).ConfigureAwait(true);
            statusLabel.Text = "Atera API key saved locally.";
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Save failed: {exception.Message}";
        }
        finally
        {
            saveButton.Enabled = true;
        }
    }
}
