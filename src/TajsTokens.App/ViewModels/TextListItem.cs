namespace TajsTokens.App.ViewModels;

// WinUI automation peers require distinct item identities even when labels match.
// Keep reference equality: making this a record reintroduces the duplicate-item hang.
internal sealed class TextListItem(string text)
{
    public string Text { get; } = text;
    public override string ToString() => Text;
}
