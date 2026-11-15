using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace RemoteControl.Ui.TestKit;

/// <summary>
/// Pictures of windows for people to look at: <see cref="Save"/> writes a PNG into the folder named by the environment variable
/// <c>RC_TEST_SHOTS</c>, and does nothing when it is not set. The headless application draws with Skia, so the picture is what the
/// window would show.
/// </summary>
/// <example>
/// <code>
/// // RC_TEST_SHOTS=/tmp/shots dotnet test ...
/// Shots.Save(window, "options-general-nord");   // /tmp/shots/options-general-nord.png
/// </code>
/// </example>
public static class Shots
{
    /// <summary>The environment variable naming the folder.</summary>
    public const string Variable = "RC_TEST_SHOTS";

    /// <summary>The folder pictures are written to, or <c>null</c> when <c>RC_TEST_SHOTS</c> is not set.</summary>
    public static string? Folder
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(Variable);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    /// <summary>Whether pictures are written.</summary>
    public static bool Enabled => Folder is not null;

    /// <summary>Writes a picture of a window when <c>RC_TEST_SHOTS</c> is set.</summary>
    /// <param name="window">The shown window.</param>
    /// <param name="name">The file name without extension (characters that cannot be in a file name become <c>_</c>).</param>
    /// <returns>The file written, or <c>null</c>.</returns>
    public static string? Save(TopLevel window, string name)
    {
        ArgumentNullException.ThrowIfNull(window);
        string? folder = Folder;
        if (folder is null) return null;
        TestUi.Settle();
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        if (frame is null) return null;
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Clean(name) + ".png");
        frame.Save(file);
        return file;
    }

    /// <summary>Captures what the window shows now (whether or not <c>RC_TEST_SHOTS</c> is set), for tests that check pixels.</summary>
    /// <param name="window">The shown window.</param>
    /// <returns>The picture; dispose it.</returns>
    /// <exception cref="InvalidOperationException">The window could not be captured.</exception>
    public static WriteableBitmap Capture(TopLevel window)
    {
        ArgumentNullException.ThrowIfNull(window);
        TestUi.Settle();
        return window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window was not rendered.");
    }

    private static string Clean(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new((name ?? "shot").Select(character => invalid.Contains(character) || character is ' ' ? '_' : character).ToArray());
        return cleaned.Length == 0 ? "shot" : cleaned;
    }
}
