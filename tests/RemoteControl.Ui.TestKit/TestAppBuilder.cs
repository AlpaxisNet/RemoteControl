using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.TestKit;

/// <summary>
/// The screenless application every window test project runs in: Avalonia.Headless with Skia drawing (so that pictures of windows
/// can be captured), the Fluent theme, the Inter font and the program's bundled font. The program's theme is applied by
/// <see cref="TestEnvironment"/> (through <see cref="AppServices.Create"/>).
/// </summary>
/// <remarks>
/// <para><b>How a window test project sets itself up</b></para>
/// <para>1. The test project's <c>.csproj</c> references xunit, <c>Avalonia.Headless.XUnit</c> and this TestKit:</para>
/// <code>
/// &lt;ItemGroup&gt;
///   &lt;PackageReference Include="Microsoft.NET.Test.Sdk" /&gt;
///   &lt;PackageReference Include="xunit" /&gt;
///   &lt;PackageReference Include="xunit.runner.visualstudio" /&gt;
///   &lt;PackageReference Include="Avalonia.Headless.XUnit" /&gt;
/// &lt;/ItemGroup&gt;
/// &lt;ItemGroup&gt;
///   &lt;ProjectReference Include="..\RemoteControl.Ui.TestKit\RemoteControl.Ui.TestKit.csproj" /&gt;
///   &lt;ProjectReference Include="..\..\src\RemoteControl.Ui.Session\RemoteControl.Ui.Session.csproj" /&gt;
/// &lt;/ItemGroup&gt;
/// </code>
/// <para>2. One file of the test project (for example <c>AssemblySetup.cs</c>) holds the two assembly attributes: the application
/// the headless runner starts, and no parallel test collections (the tests share one UI thread and one application theme):</para>
/// <code>
/// [assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(RemoteControl.Ui.TestKit.TestAppBuilder))]
/// [assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
/// </code>
/// <para>3. Every test that touches a control is an <c>[AvaloniaFact]</c> (or <c>[AvaloniaTheory]</c>), so it runs on the headless UI
/// thread, and creates a <see cref="TestEnvironment"/> (temporary user folder, the repository's bundled assets, an
/// <see cref="AppServices"/>, sounds off). It never opens a real window, never touches the user's files and needs no network:</para>
/// <code>
/// public sealed class PlayerListTests
/// {
///     [AvaloniaFact]
///     public void Typing_a_name_selects_the_player()
///     {
///         using var env = new TestEnvironment(theme: "nord");
///         var window = env.Show(new PlayerListWindow(env.Services, new FakeSession()), 400, 500);
///         var list = TestUi.Find&lt;ListBox&gt;(window, "Players");
///         list.Focus();
///         TestUi.TypeText(window, "gra");
///         Assert.Equal(2, list.SelectedIndex);
///         TestUi.Click(TestUi.Find&lt;Button&gt;(window, "Refresh"));
///         TestUi.PressKey(window, Key.Escape);
///         Shots.Save(window, "player-list");     // writes a PNG only when RC_TEST_SHOTS names a folder
///     }
/// }
/// </code>
/// <para>4. Dialogs opened by a window are found with <c>TestUi.FindDialog()</c> (an <see cref="RcDialog"/>) and answered with
/// <see cref="RcDialog.Accept"/> or by clicking <see cref="Dialogs.OkButtonName"/>. Asynchronous work is awaited with
/// <see cref="TestUi.WaitUntil"/> (a timeout fails the test instead of hanging it).</para>
/// <para>The input helpers are in <see cref="TestUi"/> (not "Ui": inside a <c>RemoteControl.Ui.*</c> namespace that name would mean the
/// namespace). Dispatcher timers (animations, debouncers) only fire while a test awaits, so wait for them with
/// <see cref="TestUi.WaitUntilAsync"/> in an <c>async Task</c> test.</para>
/// <para>5. Run the tests with <c>dotnet test</c>; set <c>RC_TEST_SHOTS=/some/folder</c> to get the pictures and look at them.</para>
/// </remarks>
public static class TestAppBuilder
{
    /// <summary>Builds the headless application (called by Avalonia.Headless.XUnit through the assembly attribute).</summary>
    /// <returns>The application builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        // No test may start a browser, a sound player or any other program on the user's desktop: links and sounds are only
        // recorded (ExternalPrograms.Suppressed). The switch is also set by itself in a test run; this makes it certain.
        ExternalPrograms.Disabled = true;
        return AppBuilder.Configure<TestApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .WithBundledFonts();
    }
}

/// <summary>The application class of the tests: the Fluent theme, dark by default (the program's theme is applied on top of it).</summary>
public sealed class TestApplication : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }
}
