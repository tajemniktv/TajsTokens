using Microsoft.Win32;

namespace TajsTokens.App.Services;

public sealed class WindowsStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TajsTokens";

    public bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                throw new InvalidOperationException("The current TajsTokens executable path could not be resolved.");
            }

            if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Start-with-Windows is unavailable while TajsTokens is launched through dotnet.exe. Use a published/apphost build first.");
            }

            key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message.ReplaceLineEndings(" ").Trim();
            return false;
        }
    }
}
