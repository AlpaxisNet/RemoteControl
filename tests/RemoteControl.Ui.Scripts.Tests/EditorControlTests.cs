using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using RemoteControl.Core;
using RemoteControl.Scripting;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Scripts.Tests;

public sealed class EditorControlTests
{
    private static (TestEnvironment Env, Window Host, ScriptEditorControl Editor) Open(string? theme = null, Func<RcOptions, RcOptions>? options = null, ScriptEditorMode mode = ScriptEditorMode.Script)
    {
        var env = new TestEnvironment(theme, options);
        var editor = new ScriptEditorControl(env.Services, mode);
        var host = env.Show(new Window { Content = editor }, 700, 480);
        return (env, host, editor);
    }

    private static Color? ColorAt(ScriptEditorControl editor, int line, int offset)
    {
        IHighlightingDefinition definition = editor.HighlightingDefinition!;
        using var highlighter = new DocumentHighlighter(editor.Editor.Document, definition);
        HighlightedLine highlighted = highlighter.HighlightLine(line);
        HighlightedSection? section = highlighted.Sections.LastOrDefault(item => item.Offset <= offset && offset < item.Offset + item.Length && item.Color.Foreground is not null);
        return section?.Color.Foreground?.GetColor(null);
    }

    [AvaloniaFact]
    public void A_keyword_gets_the_colour_of_the_syntax_theme_and_follows_a_theme_change()
    {
        var (env, _, editor) = Open("monokai");
        using (env)
        {
            editor.SetContent("if (a) { echo(\"x\"); }");
            SyntaxTheme theme = editor.SyntaxThemeInUse!;
            Assert.Equal("monokai", theme.Id);
            Assert.Equal(EditorTheme.ToColor(theme.Colors["Keyword"].Foreground), ColorAt(editor, 1, 0));
            Assert.Equal(EditorTheme.ToColor(theme.Colors["String"].Foreground), ColorAt(editor, 1, 15));

            env.SetOptions(options => options with { Theme = "nord" });
            Assert.Equal("nord", editor.SyntaxThemeInUse!.Id);
            Assert.Equal(EditorTheme.ToColor(editor.SyntaxThemeInUse.Colors["Keyword"].Foreground), ColorAt(editor, 1, 0));

            env.SetOptions(options => options with { SyncSyntaxTheme = false, SyntaxTheme = "dracula" });
            Assert.Equal("dracula", editor.SyntaxThemeInUse!.Id);
        }
    }

    [AvaloniaFact]
    public void Configuration_text_uses_the_ini_highlighting()
    {
        var (env, _, editor) = Open("monokai", mode: ScriptEditorMode.Configuration);
        using (env)
        {
            editor.SetContent("; comment\nkey=value");
            Assert.Equal("Ini", editor.HighlightingDefinition!.Name);
            Assert.NotNull(ColorAt(editor, 1, 0));
        }
    }

    [AvaloniaFact]
    public async Task A_bad_script_is_underlined_and_the_mark_goes_when_it_is_fixed()
    {
        var (env, _, editor) = Open();
        using (env)
        {
            await ApiDefinitionCatalog.WhenLoadedAsync();
            editor.SetContent("function onCreated() {\n  if (a {\n  }\n}");
            await TestUi.WaitUntilAsync(() => editor.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error), what: "an error");
            editor.ReplaceText("function onCreated() {\n  if (a) {\n  }\n}");
            await TestUi.WaitUntilAsync(() => editor.Diagnostics.All(item => item.Severity != DiagnosticSeverity.Error), what: "no error");
        }
    }

    [AvaloniaFact]
    public async Task Analysis_results_for_an_older_text_are_dropped()
    {
        var (env, _, editor) = Open();
        using (env)
        {
            editor.SetContent("function onCreated() {\n  if (a {\n  }\n}");
            Task running = editor.AnalyzeNowAsync();
            editor.ReplaceText("function onCreated() {\n}");
            await running;
            Assert.DoesNotContain(editor.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        }
    }

    [AvaloniaFact]
    public async Task Ctrl_space_proposes_api_items_with_scope_and_preview_and_inserts_the_choice()
    {
        var (env, _, editor) = Open();
        using (env)
        {
            await ApiDefinitionCatalog.WhenLoadedAsync();
            editor.SetContent("function onCreated() {\n  ec\n}");
            editor.SetCaret(editor.Text.IndexOf("ec", StringComparison.Ordinal) + 2);
            editor.RequestCompletion();
            Assert.True(editor.IsCompletionOpen);
            CompletionRow echo = Assert.Single(editor.CompletionRows, row => row.Item.InsertText == "echo");
            Assert.Equal("GLOBAL", echo.Scope);
            Assert.True(editor.CompletionRows.Count <= 16);
            editor.CompletionList.SelectedItem = echo;
            Assert.Contains("Writes a line", editor.CompletionPreviewText);
            Assert.True(editor.AcceptCompletion());
            Assert.Contains("  echo\n", editor.Text);
            Assert.False(editor.IsCompletionOpen);
        }
    }

    [AvaloniaFact]
    public async Task Local_functions_are_proposed()
    {
        var (env, _, editor) = Open();
        using (env)
        {
            editor.SetContent("function myHelper(a, b) {\n}\nfunction onCreated() {\n  myH\n}");
            await editor.UpdateSymbolsNowAsync();
            editor.SetCaret(editor.Text.IndexOf("myH\n", StringComparison.Ordinal) + 3);
            editor.RequestCompletion();
            CompletionRow helper = Assert.Single(editor.CompletionRows, row => row.Item.InsertText == "myHelper");
            editor.CompletionList.SelectedItem = helper;
            Assert.True(editor.AcceptCompletion());
            Assert.Contains("  myHelper\n", editor.Text);
        }
    }

    [AvaloniaFact]
    public void Online_players_are_proposed_inside_a_string()
    {
        var (env, _, editor) = Open();
        using (env)
        {
            editor.PlayerSource = () => [new OnlinePlayer("GraalPlayer1", "Stefan", "Stef")];
            editor.SetContent("findplayer(\"Gra\");");
            editor.SetCaret(editor.Text.IndexOf("Gra", StringComparison.Ordinal) + 3);
            editor.RequestCompletion();
            CompletionRow player = Assert.Single(editor.CompletionRows, row => row.Item.Kind == CompletionItemKind.Player);
            editor.CompletionList.SelectedItem = player;
            Assert.True(editor.AcceptCompletion());
            Assert.Equal("findplayer(\"GraalPlayer1\");", editor.Text);
        }
    }

    [AvaloniaFact]
    public async Task Typing_opens_the_list_after_two_letters_and_the_signature_hint_in_a_call()
    {
        var (env, host, editor) = Open();
        using (env)
        {
            await ApiDefinitionCatalog.WhenLoadedAsync();
            editor.SetContent(string.Empty);
            editor.FocusText();
            TestUi.TypeText(host, "e");
            await Task.Delay(300);
            TestUi.Settle();
            Assert.False(editor.IsCompletionOpen);
            TestUi.TypeText(host, "c");
            await TestUi.WaitUntilAsync(() => editor.IsCompletionOpen, what: "the completion list");
            TestUi.PressKey(host, Key.Escape);
            Assert.False(editor.IsCompletionOpen);
            TestUi.TypeText(host, "ho(");
            await TestUi.WaitUntilAsync(() => editor.IsSignatureOpen, what: "the signature hint");
            Assert.Contains("echo(text)", editor.SignatureText);
            Assert.Contains("Writes a line", editor.SignatureText);
        }
    }

    [AvaloniaFact]
    public async Task Hovering_a_function_shows_its_signature_and_a_diagnostic()
    {
        var (env, _, editor) = Open();
        using (env)
        {
            await ApiDefinitionCatalog.WhenLoadedAsync();
            editor.SetContent("function onCreated() {\n  echo(\"a\");\n}");
            Assert.True(editor.ShowHover(editor.Text.IndexOf("echo", StringComparison.Ordinal) + 1));
            Assert.True(editor.IsHoverOpen);
            Assert.Contains("echo(text)", editor.HoverText);
            Assert.Contains("Writes a line", editor.HoverText);
        }
    }

    [AvaloniaFact]
    public void Format_reindents_as_one_undo_step()
    {
        var (env, _, editor) = Open();
        using (env)
        {
            const string original = "function a() {\nif (b) {\nc = 1;\n}\n}";
            editor.SetContent(original);
            editor.Format();
            Assert.Equal("function a() {\n  if (b) {\n    c = 1;\n  }\n}", editor.Text);
            editor.Editor.Undo();
            Assert.Equal(original, editor.Text);
        }
    }

    [AvaloniaFact]
    public void Find_replace_and_replace_all()
    {
        var (env, host, editor) = Open();
        using (env)
        {
            editor.SetContent("foo bar Foo foo");
            editor.FocusText();
            TestUi.PressKey(host, Key.F, RawInputModifiers.Control);
            Assert.True(editor.IsFindOpen);
            Assert.False(editor.IsReplaceOpen);
            editor.FindBox.Text = "foo";
            Assert.True(editor.FindNext());
            Assert.Equal(0, editor.Editor.SelectionStart);
            Assert.True(editor.FindNext());
            Assert.Equal(8, editor.Editor.SelectionStart);
            editor.ShowFind(true);
            Assert.True(editor.IsReplaceOpen);
            editor.ReplaceBox.Text = "baz";
            Assert.Equal(3, editor.ReplaceAll());
            Assert.Equal("baz bar baz baz", editor.Text);
            editor.Editor.Undo();
            Assert.Equal("foo bar Foo foo", editor.Text);
            editor.SetCaret(0);
            Assert.True(editor.ReplaceNext());
            Assert.Equal("baz bar Foo foo", editor.Text);
            editor.FindBox.Text = "nothing";
            Assert.False(editor.FindNext());
            Assert.Equal("Not found", editor.FindStatus);
        }
    }

    [AvaloniaFact]
    public void Go_to_line_moves_the_caret_and_clamps()
    {
        var (env, host, editor) = Open();
        using (env)
        {
            editor.SetContent("1\n2\n3\n4\n5");
            editor.FocusText();
            TestUi.PressKey(host, Key.G, RawInputModifiers.Control);
            Assert.True(editor.IsGoToLineOpen);
            editor.LineBox.Text = "3";
            editor.LineBox.Focus();
            TestUi.PressKey(host, Key.Enter);
            Assert.Equal(3, editor.CaretLine);
            Assert.False(editor.IsGoToLineOpen);
            editor.GoToLine(99);
            Assert.Equal(5, editor.CaretLine);
        }
    }

    [AvaloniaFact]
    public void A_two_megabyte_text_switches_to_the_large_text_mode_without_hanging()
    {
        var (env, _, editor) = Open();
        using (env)
        {
            string line = "  echo(\"" + new string('x', 90) + "\");\n";
            string text = string.Concat(Enumerable.Repeat(line, 2 * 1024 * 1024 / line.Length + 1));
            var watch = Stopwatch.StartNew();
            editor.SetContent(text);
            TestUi.Settle();
            editor.GoToLine(15000);
            TestUi.Settle();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "took " + watch.Elapsed);
            Assert.True(editor.IsLargeContent);
            Assert.Null(editor.HighlightingDefinition);
            editor.RequestCompletion();
            Assert.False(editor.IsCompletionOpen);
            Assert.Empty(editor.Diagnostics);
            Assert.False(editor.BracketsEnabled);
        }
    }

    [AvaloniaFact]
    public void Extra_carets_type_into_every_line_as_one_undo_step()
    {
        var (env, host, editor) = Open();
        using (env)
        {
            editor.SetContent("a\nb\nc");
            editor.FocusText();
            editor.SetCaret(0);
            TestUi.PressKey(host, Key.Down, RawInputModifiers.Control | RawInputModifiers.Alt);
            TestUi.PressKey(host, Key.Down, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal(2, editor.Carets.Count);
            TestUi.TypeText(host, "x");
            Assert.Equal("xa\nxb\nxc", editor.Text);
            TestUi.PressKey(host, Key.Back);
            Assert.Equal("a\nb\nc", editor.Text);
            TestUi.PressKey(host, Key.Escape);
            Assert.Equal(0, editor.Carets.Count);
            editor.Editor.Undo();
            Assert.Equal("xa\nxb\nxc", editor.Text);
        }
    }

    [AvaloniaFact]
    public void Ctrl_d_selects_the_word_then_the_next_occurrence()
    {
        var (env, host, editor) = Open();
        using (env)
        {
            editor.SetContent("foo x foo");
            editor.FocusText();
            editor.SetCaret(1);
            TestUi.PressKey(host, Key.D, RawInputModifiers.Control);
            Assert.Equal("foo", editor.SelectedText);
            TestUi.PressKey(host, Key.D, RawInputModifiers.Control);
            Assert.Equal(1, editor.Carets.Count);
            TestUi.TypeText(host, "z");
            Assert.Equal("z x z", editor.Text);
        }
    }

    [AvaloniaFact]
    public void Ctrl_l_selects_the_line_and_ctrl_s_saves_once_per_press()
    {
        var (env, host, editor) = Open();
        using (env)
        {
            editor.SetContent("first\nsecond line\nthird");
            editor.FocusText();
            editor.SetCaret(8);
            TestUi.PressKey(host, Key.L, RawInputModifiers.Control);
            Assert.Equal("second line", editor.SelectedText);
            int saves = 0;
            editor.SaveRequested += (_, _) => saves++;
            host.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            host.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            host.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            TestUi.PressKey(host, Key.S, RawInputModifiers.Control);
            Assert.Equal(2, saves);
        }
    }

    [AvaloniaFact]
    public void Options_apply_to_an_open_editor()
    {
        var (env, _, editor) = Open(options: options => options with { ScriptTabWidth = 4, ShowLineNumbers = false, Minimap = false });
        using (env)
        {
            Assert.Equal(4, editor.Editor.Options.IndentationSize);
            Assert.False(editor.Editor.ShowLineNumbers);
            Assert.False(editor.Minimap.IsVisible);
            env.SetOptions(options => options with { ScriptTabWidth = 3, ShowLineNumbers = true, Minimap = true, ScriptUseTabs = true, ScriptFontSize = 14 });
            Assert.Equal(3, editor.Editor.Options.IndentationSize);
            Assert.False(editor.Editor.Options.ConvertTabsToSpaces);
            Assert.True(editor.Editor.ShowLineNumbers);
            Assert.True(editor.Minimap.IsVisible);
            Assert.Equal(14, editor.FontPoints);
            editor.Zoom(100);
            Assert.Equal(48, editor.FontPoints);
            editor.Zoom(-100);
            Assert.Equal(6, editor.FontPoints);
        }
    }

    [AvaloniaFact]
    public void Brackets_are_matched_and_the_minimap_marks_the_view()
    {
        var (env, _, editor) = Open(options: options => options with { Minimap = true });
        using (env)
        {
            editor.SetContent("if (a) { b(\")\"); }" + string.Concat(Enumerable.Repeat("\nline", 300)));
            editor.SetCaret(7);
            Assert.Equal((7, 17), editor.BracketPair);
            TestUi.Settle();
            Assert.True(editor.Minimap.MarkerVisible);
            Assert.InRange(editor.Minimap.MarkerHeight, 12, 48);
            Assert.Equal(12, MinimapView.MarkerHeightFor(100, 1, 1000));
            Assert.Equal(48, MinimapView.MarkerHeightFor(400, 900, 1000));
        }
    }

    [AvaloniaFact]
    public void Pictures_of_the_editor()
    {
        foreach (string theme in new[] { "catppuccin", "light" })
        {
            var (env, host, editor) = Open(theme, options => options with { Minimap = true });
            using (env)
            {
                editor.SetContent("//#CLIENTSIDE\nfunction onCreated() {\n  // comment\n  this.value = 3.5;\n  if (this.value > 2) {\n    echo(\"hello \" @ player.account);\n  }\n}\n");
                editor.ChangedLines = new ChangedLines(3, 4);
                editor.ShowFind(true);
                editor.FindBox.Text = "value";
                editor.FindNext();
                TestUi.Settle();
                Shots.Save(host, "script-editor-control-" + theme);
            }
        }
    }
}
