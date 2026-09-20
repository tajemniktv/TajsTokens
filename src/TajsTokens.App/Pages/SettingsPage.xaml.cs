// Taj's Tokens | SettingsPage.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.App.Pages;

public sealed partial class SettingsPage : Page
{
    private RuntimeSettings? _renderedSettings;

    public SettingsPage()
    {
        InitializeComponent();
        BuildVersionText.Text = $"Build {Assembly.GetExecutingAssembly().GetCustomAttributes(false)
            .OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown"}";
        Loaded += OnLoaded;
    }

    private App App => (App)Application.Current;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderSettings();
    }

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

        if (!TryParseThresholds(ThresholdsTextBox.Text, out int[] thresholds, out string? thresholdError))
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid notification thresholds", thresholdError!);
            return;
        }

        RuntimeSettings baseline = _renderedSettings ?? App.Services.Settings;
        RuntimeSettings candidate = baseline with
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
            CodexCliTimeoutSeconds = (int)Math.Round(CodexCliTimeoutBox.Value, MidpointRounding.AwayFromZero),
        };

        SaveButton.IsEnabled = false;
        try
        {
            (bool Success, string? Error) result = await App.TryApplySettingsAsync(baseline, candidate);
            if (!result.Success)
            {
                RenderSettings();
                ShowStatus(InfoBarSeverity.Error, "Settings were not applied", result.Error ?? "Unknown settings error.");
                return;
            }

            RenderSettings();
            ShowStatus(
                InfoBarSeverity.Success,
                "Settings saved",
                "Runtime settings were persisted and live services were updated where applicable.");
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void RenderSettings()
    {
        RuntimeSettings settings = App.Services.Settings;
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
            _ => 2,
        };
        CodexCliTimeoutBox.Value = settings.CodexCliTimeoutSeconds;
        DataFolderText.Text = App.Services.DataFolder;
        DatabasePathText.Text = App.Services.DatabasePath;
        SettingsSchemaText.Text = $"Settings schema v{settings.SchemaVersion} · poll range 15–3600 s";
        RolloutOwnershipText.Text =
            $"{settings.RolloutAccountAssociations.Length} bounded source/session associations. Attribution remains user-asserted, not provider-verified.";
        RevokeRolloutsButton.IsEnabled = settings.RolloutAccountAssociations.Length > 0;
    }

    private async void OnAssociateRolloutsClicked(object sender, RoutedEventArgs e)
    {
        string[] accounts = App.Services.Telemetry.Latest.QuotaLanes.Where(x => x.IsFresh && x.Snapshot?.AccountKey is not null)
            .Select(x => x.Snapshot!.AccountKey!).Distinct().ToArray();
        if (accounts.Length == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "No recorded account", "Refresh account quota first; no current login will be guessed.");
            return;
        }
        RuntimeSettings baseline = App.Services.Settings;
        var choice = new ComboBox
        {
            ItemsSource = accounts.Select(QuotaAccountScope.Describe).ToArray(), SelectedIndex = accounts.Length == 1 ? 0 : -1,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(
            new TextBlock
            {
                Text =
                    "I confirm all retained rollout histories belong to the account selected below. This adds revocable, bounded user assertions, not native account facts. Existing associations are replaced.",
                TextWrapping = TextWrapping.Wrap,
            });
        content.Children.Add(choice);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Associate rollout history",
            Content = content,
            PrimaryButtonText = "Confirm association",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || choice.SelectedIndex < 0) return;
        string account = accounts[choice.SelectedIndex];
        AssociateRolloutsButton.IsEnabled = false;
        try
        {
            if (!App.Services.Telemetry.Latest.QuotaLanes.Any(x => x.IsFresh && x.Snapshot?.AccountKey == account))
                throw new InvalidOperationException("The selected account is no longer current. Refresh and confirm again.");
            RolloutAccountAssociation[] assertions =
                await App.Services.PrepareRolloutAccountAssociationsAsync(account, CancellationToken.None);
            (bool Success, string? Error) result = await App.TryApplySettingsAsync(
                baseline,
                baseline with { RolloutAccountAssociations = assertions });
            RenderSettings();
            ShowStatus(
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                "Historical ownership",
                result.Success
                    ? $"Saved {assertions.Length} user-asserted associations. Re-run model evaluation; native data is unchanged."
                    : result.Error!);
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Association not saved", exception.Message);
        }
        finally
        {
            AssociateRolloutsButton.IsEnabled = true;
        }
    }

    private async void OnRevokeRolloutsClicked(object sender, RoutedEventArgs e)
    {
        RuntimeSettings baseline = App.Services.Settings;
        (bool Success, string? Error) result = await App.TryApplySettingsAsync(baseline, baseline with { RolloutAccountAssociations = [] });
        RenderSettings();
        ShowStatus(
            result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            "Historical ownership",
            result.Success
                ? "Associations revoked. Future modelling will no longer use them; native observations are unchanged."
                : result.Error!);
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
        foreach (string part in text.Split(
                     [',', ';', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value is <= 0 or >= 100)
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

    private CodexCliPromptMode ParsePromptMode()
    {
        return (CodexCliPromptModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "LastArgument" => CodexCliPromptMode.LastArgument,
            "None" => CodexCliPromptMode.None,
            _ => CodexCliPromptMode.StandardInput,
        };
    }
}