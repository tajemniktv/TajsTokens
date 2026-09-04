using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class CodexCliHarnessPage : Page
{
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private CancellationTokenSource? _runCancellation;
    private bool _loaded;
    private bool _running;

    public CodexCliHarnessPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private App App => (App)Application.Current;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        RenderSettings();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _runCancellation?.Cancel();
    }

    private async void OnRunClicked(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        if (!TryBuildRequest(out var request, out var error))
        {
            StatusText.Text = error;
            return;
        }

        _stdout.Clear();
        _stderr.Clear();
        RenderOutput();
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        _running = true;
        UpdateRunButtons();
        StatusText.Text = "Starting the configured Codex CLI…";

        var progress = new Progress<CodexCliOutputLine>(AppendOutput);
        try
        {
            var result = await Task.Run(
                () => App.Services.CodexCliHarness.RunAsync(request, progress, _runCancellation.Token),
                _runCancellation.Token);
            StatusText.Text = result.Succeeded
                ? $"Completed successfully · exit code {result.ExitCode} · {result.Duration.TotalSeconds:0.0}s"
                : $"Completed with exit code {result.ExitCode} · {result.Duration.TotalSeconds:0.0}s";
        }
        catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
        {
            if (_loaded)
            {
                StatusText.Text = "Run cancelled. The child process was asked to exit.";
            }
        }
        catch (TimeoutException exception)
        {
            StatusText.Text = $"Run timed out: {Summarize(exception.Message)}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Run failed: {Summarize(exception.Message)}";
        }
        finally
        {
            _running = false;
            UpdateRunButtons();
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => _runCancellation?.Cancel();

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSettings(out var candidate, out var error))
        {
            StatusText.Text = error;
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var result = await App.TryApplySettingsAsync(App.Services.Settings, candidate);
            StatusText.Text = result.Success
                ? "Harness configuration saved."
                : $"Configuration was not saved: {result.Error ?? "Unknown settings error."}";
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private bool TryBuildRequest(out CodexCliHarnessRequest request, out string error)
    {
        if (!TryBuildSettings(out var settings, out error))
        {
            request = null!;
            return false;
        }

        request = CodexCliHarnessRequest.FromSettings(settings, PromptBox.Text ?? string.Empty);
        return true;
    }

    private bool TryBuildSettings(out RuntimeSettings settings, out string error)
    {
        if (string.IsNullOrWhiteSpace(CommandBox.Text))
        {
            settings = null!;
            error = "Enter an executable path or a command available on PATH.";
            return false;
        }

        if (double.IsNaN(TimeoutBox.Value) || TimeoutBox.Value is < 5 or > 3600)
        {
            settings = null!;
            error = "Choose a timeout between 5 and 3600 seconds.";
            return false;
        }

        settings = App.Services.Settings with
        {
            CodexCliCommand = CommandBox.Text,
            CodexCliArguments = ArgumentsBox.Text ?? string.Empty,
            CodexCliWorkingDirectory = WorkingDirectoryBox.Text ?? string.Empty,
            CodexCliPromptMode = ParsePromptMode(),
            CodexCliTimeoutSeconds = (int)Math.Round(TimeoutBox.Value, MidpointRounding.AwayFromZero)
        };
        error = string.Empty;
        return true;
    }

    private void RenderSettings()
    {
        var settings = App.Services.Settings;
        CommandBox.Text = settings.CodexCliCommand;
        ArgumentsBox.Text = settings.CodexCliArguments;
        WorkingDirectoryBox.Text = settings.CodexCliWorkingDirectory;
        PromptModeCombo.SelectedIndex = settings.CodexCliPromptMode switch
        {
            CodexCliPromptMode.StandardInput => 0,
            CodexCliPromptMode.LastArgument => 1,
            _ => 2
        };
        TimeoutBox.Value = settings.CodexCliTimeoutSeconds;
    }

    private void AppendOutput(CodexCliOutputLine line)
    {
        var buffer = line.Stream == CodexCliOutputStream.StandardOutput ? _stdout : _stderr;
        buffer.AppendLine(line.Text);
        RenderOutput();
    }

    private void RenderOutput()
    {
        StdoutBox.Text = _stdout.ToString();
        StderrBox.Text = _stderr.ToString();
    }

    private void UpdateRunButtons()
    {
        RunButton.IsEnabled = !_running;
        CancelButton.IsEnabled = _running;
        SaveButton.IsEnabled = !_running;
    }

    private CodexCliPromptMode ParsePromptMode() =>
        (PromptModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "LastArgument" => CodexCliPromptMode.LastArgument,
            "None" => CodexCliPromptMode.None,
            _ => CodexCliPromptMode.StandardInput
        };

    private static string Summarize(string message) => message.ReplaceLineEndings(" ").Trim();
}
