using TajsTokens.App.ViewModels;

namespace TajsTokens.Core.Tests;

public sealed class TextListItemTests
{
    [Fact]
    public void RepeatedLabelsKeepDistinctAutomationIdentitiesWithoutDroppingRows()
    {
        string[] labels = ["Repeated observation", "Other observation", "Repeated observation", ""];
        TextListItem[] rows = labels.Select(text => new TextListItem(text)).ToArray();

        Assert.Equal(labels, rows.Select(row => row.Text));
        Assert.Equal(labels, rows.Select(row => row.ToString()));
        Assert.NotEqual(rows[0], rows[2]);
        Assert.Equal(labels.Length, rows.Distinct().Count());
    }
}
