using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using RemoteControl.Scripting;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Session;

/// <summary>A read-only text view with INI highlighting, for the debug information and the level-NPC dump (the C++ source views).</summary>
internal static class IniView
{
    private static readonly Lazy<IHighlightingDefinition?> s_definition = new(Load);

    /// <summary>Makes the view.</summary>
    /// <param name="name">The control's name.</param>
    /// <param name="text">The text.</param>
    /// <returns>The editor.</returns>
    public static TextEditor Create(string name, string text)
    {
        var editor = new TextEditor
        {
            Name = name,
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = false,
            Padding = new Thickness(12, 10),
            // The INI colours are those of the dark graalcolors scheme; a light theme shows the text plain.
            SyntaxHighlighting = ThemeManager.Current?.IsDark == false ? null : s_definition.Value,
            Text = text,
        };
        editor.Options.HighlightCurrentLine = false;
        editor.Options.EnableHyperlinks = false;
        editor.Bind(TemplatedControl.BackgroundProperty, editor.GetResourceObservable(ThemeKeys.EditorBrush));
        editor.Bind(TemplatedControl.ForegroundProperty, editor.GetResourceObservable(ThemeKeys.TextBrush));
        editor.Bind(TemplatedControl.FontFamilyProperty, editor.GetResourceObservable(ThemeKeys.ScriptFontFamily));
        editor.Bind(TemplatedControl.FontSizeProperty, editor.GetResourceObservable(ThemeKeys.ScriptFontSize));
        return editor;
    }

    private static IHighlightingDefinition? Load()
    {
        try
        {
            using Stream stream = HighlightingResources.OpenIni();
            using var reader = XmlReader.Create(stream);
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception exception) when (exception is IOException or XmlException or InvalidOperationException or HighlightingDefinitionInvalidException)
        {
            UiSafe.Log.Warn("The INI highlighting could not be loaded.", exception);
            return null;
        }
    }
}
