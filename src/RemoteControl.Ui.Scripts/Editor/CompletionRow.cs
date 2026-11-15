using RemoteControl.Scripting;

namespace RemoteControl.Ui.Scripts;

/// <summary>One line of the autocomplete list: a kind badge, the label and the scope right-aligned.</summary>
internal sealed class CompletionRow
{
    public CompletionRow(CompletionItem item)
    {
        Item = item;
        Badge = item.Kind switch
        {
            CompletionItemKind.Class => "C",
            CompletionItemKind.Property => "P",
            CompletionItemKind.Player => "@",
            CompletionItemKind.Identifier => "·",
            _ => "ƒ",
        };
    }

    /// <summary>The proposal.</summary>
    public CompletionItem Item { get; }

    /// <summary>The kind as a one-character badge (the C++ showed theme icons).</summary>
    public string Badge { get; }

    /// <summary>The list text.</summary>
    public string Label => Item.Label;

    /// <summary>The scope column (GLOBAL, CLIENTSIDE, SERVERSIDE...).</summary>
    public string Scope => Item.Scope;

    /// <summary>The preview strip under the list: <c>Kind | summary</c>.</summary>
    public string Preview => Item.KindLabel + "  |  " + Item.Summary;

    /// <inheritdoc />
    public override string ToString() => Label;
}
