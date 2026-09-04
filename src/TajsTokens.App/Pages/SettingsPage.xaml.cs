using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class SettingsPage : Page
{
    private RuntimeSettings? _renderedSettings;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private App App => (App)Application.Current;

    private void OnLoaded(object sender, RoutedEventArgs e) => RenderSettings();

    private void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        RenderSettings();
        ShowStatus(InfoBarSeverity.Informational, "Reloaded", "Restored the currently persisted runtime settings.");
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(PollIntervalBox.Value))
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid refresh interval", "Choose a refresh interval between 15 and 3600 seconds.");
            return;
        }

        if (string.IsNullOrWhiteSpace(CodexCliCommandBox.Text))
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid Codex CLI", "Enter an executable path or a command available on PATH.");
            return;
        }

        if (double.IsNaN(CodexCliTimeoutBox.Value) || CodexCliTimeoutBox.Value is < 5 or > 3600)
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid harness timeout", "Choose a timeout between 5 and 3600 seconds.");
            return;
        }

        if (!TryParseThresholds(ThresholdsTextBox.Text, out var thresholds, out var thresholdError))
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid notification thresholds", thresholdError!);
            return;
        }

        var baseline = _renderedSettings ?? App.Services.Settings;
        var candidate = baseline with
        {
            RunInBackground = RunInBackgroundToggle.IsOn,
            LaunchAtLogin = LaunchAtLoginToggle.IsOn,
            PollIntervalSeconds = (int)Math.Round(PollIntervalBox.Value, MidpointRounding.AwayFromZero),
            NotificationsEnabled = NotificationsToggle.IsOn,
            LowQuotaThresholds = thresholds,
            TokscaleReconciliationEnabled = TokscaleReconciliationToggle.IsOn,
            TokscaleFallbackEnabled = TokscaleFallbackToggle.IsOn,
            CodexCliCommand = CodexCliCommandBox.Text,
            CodexCliArguments = CodexCliArgumentsBox.Text,
            CodexCliWorkingDirectory = CodexCliWorkingDirectoryBox.Text,
            CodexCliPromptMode = ParsePromptMode(),
            CodexCliTimeoutSeconds = (int)Math.Round(CodexCliTimeoutBox.Value, MidpointRounding.AwayFromZero)
        };

        SaveButton.IsEnabled = false;
        try
        {
            var result = await App.TryApplySettingsAsync(baseline, candidate);
            if (!result.Success)
            {
                RenderSettings();
                ShowStatus(InfoBarSeverity.Error, "Settings were not applied", result.Error ?? "Unknown settings error.");
                return;
            }

            RenderSettings();
            ShowStatus(InfoBarSeverity.Success, "Settings saved", "Runtime settings were persisted and live services were updated where applicable.");
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void RenderSettings()
    {
        var settings = App.Services.Settings;
        _renderedSettings = settings;
        RunInBackgroundToggle.IsOn = settings.RunInBackground;
        LaunchAtLoginToggle.IsOn = settings.LaunchAtLogin;
        PollIntervalBox.Value = settings.PollIntervalSeconds;
        NotificationsToggle.IsOn = settings.NotificationsEnabled;
        ThresholdsTextBox.Text = string.Join(", ", settings.LowQuotaThresholds);
        TokscaleReconciliationToggle.IsOn = settings.TokscaleReconciliationEnabled;
        TokscaleFallbackToggle.IsOn = settings.TokscaleFallbackEnabled;
        CodexCliCommandBox.Text = settings.CodexCliCommand;
        CodexCliArgumentsBox.Text = settings.CodexCliArguments;
        CodexCliWorkingDirectoryBox.Text = settings.CodexCliWorkingDirectory;
        CodexCliPromptModeCombo.SelectedIndex = settings.CodexCliPromptMode switch
        {
            CodexCliPromptMode.StandardInput => 0,
            CodexCliPromptMode.LastArgument => 1,
            _ => 2
        };
        CodexCliTimeoutBox.Value = settings.CodexCliTimeoutSeconds;
        DataFolderText.Text = App.Services.DataFolder;
        DatabasePathText.Text = App.Services.DatabasePath;
        SettingsSchemaText.Text = $"Settings schema v{settings.SchemaVersion} · poll range 15–3600 s";
    }

    private static bool TryParseThresholds(string? text, out int[] thresholds, out string? error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            thresholds = RuntimeSettings.DefaultLowQuotaThresholds.ToArray();
            error = null;
            return true;
        }

        var values = new List<int>();
        foreach (var part in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value is <= 0 or >= 100)
            {
                thresholds = [];
                error = $"'{part}' is not a percentage between 1 and 99.";
                return false;
            }

            values.Add(value);
        }

        thresholds = RuntimeSettings.NormalizeLowQuotaThresholds(values);
        error = null;
        return true;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private CodexCliPromptMode ParsePromptMode() =>
        (CodexCliPromptModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "LastArgument" => CodexCliPromptMode.LastArgument,
            "None" => CodexCliPromptMode.None,
            _ => CodexCliPromptMode.StandardInput
        };
}
