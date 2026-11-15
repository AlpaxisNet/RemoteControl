using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>
/// The fonts the program ships with: <c>fonts/tempus-sans-itc.ttf</c> ("Tempus Sans ITC"), which the C++ registered privately at
/// start-up (<c>registerBundledFonts</c>, <c>TMain.cpp:51-82</c>). The file is embedded in this library and registered as a font
/// collection, so nothing is installed on the system.
/// </summary>
/// <example>
/// In <c>Program.BuildAvaloniaApp()</c>: <c>AppBuilder.Configure&lt;App&gt;().UsePlatformDetect().WithInterFont().WithBundledFonts();</c>
/// then <c>new TextBlock { FontFamily = BundledFonts.TempusSans }</c>. In XAML the family is
/// <c>avares://RemoteControl.Ui.Common/Assets/Fonts#Tempus Sans ITC</c>.
/// </example>
public static class BundledFonts
{
    /// <summary>The key of the bundled font collection.</summary>
    public static readonly Uri CollectionKey = new("fonts:RemoteControl", UriKind.Absolute);

    /// <summary>Where the embedded font files are.</summary>
    public static readonly Uri Source = new("avares://RemoteControl.Ui.Common/Assets/Fonts", UriKind.Absolute);

    private static int s_registered;

    /// <summary>The family name of the bundled font.</summary>
    public const string TempusSansName = "Tempus Sans ITC";

    /// <summary>The bundled "Tempus Sans ITC", read from the embedded file (usable with or without <see cref="Register"/>).</summary>
    public static FontFamily TempusSans { get; } = new(Source + "#" + TempusSansName);

    /// <summary>Whether the fonts were registered.</summary>
    public static bool IsRegistered => Volatile.Read(ref s_registered) != 0;

    /// <summary>Registers the bundled fonts while the application is built.</summary>
    /// <param name="builder">The application builder.</param>
    /// <returns>The same builder.</returns>
    public static AppBuilder WithBundledFonts(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ConfigureFonts(manager => Add(manager, AppLog.Default));
    }

    /// <summary>Registers the bundled fonts in a running application (when the builder was not available). Never throws.</summary>
    /// <param name="log">Where a failure is logged; <see cref="AppLog.Default"/> when <c>null</c>.</param>
    /// <returns>Whether the fonts are registered.</returns>
    public static bool Register(AppLog? log = null)
    {
        try
        {
            return Add(FontManager.Current, log ?? AppLog.Default);
        }
        catch (Exception exception)
        {
            (log ?? AppLog.Default).Warn("The bundled fonts could not be registered.", exception);
            return false;
        }
    }

    private static bool Add(FontManager manager, AppLog log)
    {
        if (Interlocked.Exchange(ref s_registered, 1) != 0) return true;
        try
        {
            manager.AddFontCollection(new EmbeddedFontCollection(CollectionKey, Source));
            return true;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref s_registered, 0);
            log.Warn("The bundled fonts could not be registered.", exception);
            return false;
        }
    }
}
