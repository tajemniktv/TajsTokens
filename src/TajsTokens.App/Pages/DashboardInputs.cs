using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TajsTokens.App.Pages;

// In-memory only. Implementations retain inputs/identities, never a Page, control,
// delegate or history read model. Dynamic selections belong to each page owner.
internal interface IDashboardPresentation
{
    object CaptureInputs();
    void RestoreInputs(object state);
}

internal sealed record DashboardInput(string Name, string? Text = null, double Number = double.NaN,
    bool? Flag = null, int Index = -1, DateTimeOffset? Date = null);

internal static class DashboardInputs
{
    public static DashboardInput[] Capture(params FrameworkElement[] controls) => controls.Select(control => control switch
    {
        TextBox box => new DashboardInput(box.Name, Text: box.Text),
        NumberBox box => new DashboardInput(box.Name, Text: box.Text, Number: box.Value),
        ComboBox box => new DashboardInput(box.Name, Index: box.SelectedIndex),
        Pivot pivot => new DashboardInput(pivot.Name, Index: pivot.SelectedIndex),
        TabView tabs => new DashboardInput(tabs.Name, Index: tabs.SelectedIndex),
        ToggleSwitch toggle => new DashboardInput(toggle.Name, Flag: toggle.IsOn),
        CheckBox check => new DashboardInput(check.Name, Flag: check.IsChecked),
        Expander expander => new DashboardInput(expander.Name, Flag: expander.IsExpanded),
        CalendarDatePicker date => new DashboardInput(date.Name, Date: date.Date),
        _ => throw new ArgumentException($"Unsupported dashboard input {control.Name}."),
    }).ToArray();

    // Apply while detached, before Loaded starts any page query/subscription.
    public static void Restore(DashboardInput[] state, params FrameworkElement[] controls)
    {
        foreach (FrameworkElement control in controls)
        {
            DashboardInput? value = state.FirstOrDefault(input => input.Name == control.Name);
            if (value is null) continue;
            switch (control)
            {
                case TextBox box: box.Text = value.Text ?? ""; break;
                case NumberBox box: box.Value = value.Number; box.Text = value.Text ?? ""; break;
                case ComboBox box: box.SelectedIndex = value.Index; break;
                case Pivot pivot: pivot.SelectedIndex = value.Index; break;
                case TabView tabs: tabs.SelectedIndex = value.Index; break;
                case ToggleSwitch toggle: toggle.IsOn = value.Flag == true; break;
                case CheckBox check: check.IsChecked = value.Flag; break;
                case Expander expander: expander.IsExpanded = value.Flag == true; break;
                case CalendarDatePicker date: date.Date = value.Date; break;
            }
        }
    }
}
