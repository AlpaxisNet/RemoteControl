using Avalonia.Media;

namespace RemoteControl.Ui.Start;

/// <summary>
/// The small symbolic icons of the start windows (the GTK ones were <c>document-edit-symbolic</c>, <c>preferences-system-symbolic</c>,
/// <c>list-add-symbolic</c>, <c>edit-delete-symbolic</c> and the stock OK, Close, Refresh and Connect images), drawn as geometry so that
/// they take the theme's text colour.
/// </summary>
internal static class Icons
{
    /// <summary>A pencil: "Manage accounts", "Manage saved list servers".</summary>
    public static Geometry Pencil { get; } = Parse("M3,17.25V21h3.75L17.81,9.94l-3.75-3.75L3,17.25z M20.71,7.04c0.39-0.39,0.39-1.02,0-1.41l-2.34-2.34c-0.39-0.39-1.02-0.39-1.41,0l-1.83,1.83l3.75,3.75L20.71,7.04z");

    /// <summary>A gear: "List server settings".</summary>
    public static Geometry Gear { get; } = Parse(
        "M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.07-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61l-1.92-3.32" +
        "c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41h-3.84" +
        "c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.74,8.87" +
        "C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.07,0.94l-2.03,1.58c-0.18,0.14-0.23,0.41-0.12,0.61" +
        "l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54c0.05,0.24,0.24,0.41,0.48,0.41h3.84" +
        "c0.24,0,0.44-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32" +
        "c0.12-0.22,0.07-0.47-0.12-0.61L19.14,12.94z M12,15.6c-1.98,0-3.6-1.62-3.6-3.6s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z");

    /// <summary>A plus: "New list server profile".</summary>
    public static Geometry Add { get; } = Parse("M19,13h-6v6h-2v-6H5v-2h6V5h2v6h6V13z");

    /// <summary>A bin: "Delete selected list server profile".</summary>
    public static Geometry Delete { get; } = Parse("M6,19c0,1.1,0.9,2,2,2h8c1.1,0,2-0.9,2-2V7H6V19z M19,4h-3.5l-1-1h-5l-1,1H5v2h14V4z");

    /// <summary>An arrow up: move a profile up.</summary>
    public static Geometry Up { get; } = Parse("M7,15l5-5l5,5H7z");

    /// <summary>An arrow down: move a profile down, open the account list.</summary>
    public static Geometry Down { get; } = Parse("M7,10l5,5l5-5H7z");

    /// <summary>A tick: OK, Select.</summary>
    public static Geometry Ok { get; } = Parse("M9,16.17L4.83,12l-1.42,1.41L9,19L21,7l-1.41-1.41L9,16.17z");

    /// <summary>A cross: Close.</summary>
    public static Geometry Close { get; } = Parse("M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41z");

    /// <summary>A circular arrow: Refresh.</summary>
    public static Geometry Refresh { get; } = Parse(
        "M17.65,6.35C16.2,4.9,14.21,4,12,4c-4.42,0-7.99,3.58-7.99,8s3.57,8,7.99,8c3.73,0,6.84-2.55,7.73-6h-2.08c-0.82,2.33-3.04,4-5.65,4" +
        "c-3.31,0-6-2.69-6-6s2.69-6,6-6c1.66,0,3.14,0.69,4.22,1.78L13,11h7V4L17.65,6.35z");

    /// <summary>A plug: Connect.</summary>
    public static Geometry Connect { get; } = Parse("M16,7V3h-2v4h-4V3H8v4C6.9,7,6,7.9,6,9v5.5L9.5,18v3h5v-3l3.5-3.5V9C18,7.9,17.1,7,16,7z");

    /// <summary>A floppy disk: Save.</summary>
    public static Geometry Save { get; } = Parse("M17,3H5C3.89,3,3,3.9,3,5v14c0,1.1,0.89,2,2,2h14c1.1,0,2-0.9,2-2V7L17,3z M12,19c-1.66,0-3-1.34-3-3s1.34-3,3-3s3,1.34,3,3S13.66,19,12,19z M15,9H5V5h10V9z");

    private static Geometry Parse(string data) => StreamGeometry.Parse(data);
}
