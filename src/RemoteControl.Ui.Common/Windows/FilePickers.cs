using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace RemoteControl.Ui.Common;

/// <summary>
/// The file and folder pickers of the platform (Avalonia <see cref="IStorageProvider"/>), returning plain local paths. A picker that
/// is cancelled, unavailable or fails gives <c>null</c> (or an empty list); nothing here throws.
/// </summary>
/// <example>
/// <code>
/// string? folder = await FilePickers.PickFolderAsync(this, "Download folder", startFolder: current);
/// string? file = await FilePickers.OpenFileAsync(this, "Background image", FilePickers.Images);
/// string? target = await FilePickers.SaveFileAsync(this, "Save script", "weapon.gs2", ".gs2");
/// </code>
/// </example>
public static class FilePickers
{
    /// <summary>A filter for the pictures the background accepts (PNG, JPEG, GIF, WebP, MNG, BMP).</summary>
    public static IReadOnlyList<FilePickerFileType> Images { get; } =
    [
        new("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.mng", "*.bmp"], MimeTypes = ["image/*"] },
        FilePickerFileTypes.All,
    ];

    /// <summary>A filter for text and script files.</summary>
    public static IReadOnlyList<FilePickerFileType> Text { get; } =
    [
        new("Text and scripts") { Patterns = ["*.txt", "*.gs2", "*.gs", "*.nw", "*.json", "*.xml", "*.ini", "*.cfg", "*.conf"], MimeTypes = ["text/*"] },
        FilePickerFileTypes.All,
    ];

    /// <summary>Asks for one existing file.</summary>
    /// <param name="owner">A control of the window the picker belongs to.</param>
    /// <param name="title">The picker title.</param>
    /// <param name="filters">The file types, or <c>null</c> for all files.</param>
    /// <param name="startFolder">The folder the picker starts in, or <c>null</c>.</param>
    /// <returns>The full path, or <c>null</c>.</returns>
    public static async Task<string?> OpenFileAsync(Visual owner, string title, IReadOnlyList<FilePickerFileType>? filters = null, string? startFolder = null)
    {
        IReadOnlyList<string> files = await OpenFilesCoreAsync(owner, title, filters, startFolder, false).ConfigureAwait(true);
        return files.Count > 0 ? files[0] : null;
    }

    /// <summary>Asks for one or more existing files.</summary>
    /// <param name="owner">A control of the window the picker belongs to.</param>
    /// <param name="title">The picker title.</param>
    /// <param name="filters">The file types, or <c>null</c> for all files.</param>
    /// <param name="startFolder">The folder the picker starts in, or <c>null</c>.</param>
    /// <returns>The full paths; empty when cancelled.</returns>
    public static Task<IReadOnlyList<string>> OpenFilesAsync(Visual owner, string title, IReadOnlyList<FilePickerFileType>? filters = null, string? startFolder = null) =>
        OpenFilesCoreAsync(owner, title, filters, startFolder, true);

    /// <summary>Asks where to save a file.</summary>
    /// <param name="owner">A control of the window the picker belongs to.</param>
    /// <param name="title">The picker title.</param>
    /// <param name="suggestedName">The file name offered.</param>
    /// <param name="defaultExtension">The extension added when the user types none, for example <c>.txt</c>.</param>
    /// <param name="filters">The file types, or <c>null</c>.</param>
    /// <param name="startFolder">The folder the picker starts in, or <c>null</c>.</param>
    /// <returns>The full path, or <c>null</c>.</returns>
    public static async Task<string?> SaveFileAsync(Visual owner, string title, string? suggestedName = null, string? defaultExtension = null, IReadOnlyList<FilePickerFileType>? filters = null, string? startFolder = null)
    {
        try
        {
            IStorageProvider? provider = ProviderOf(owner);
            if (provider is null || !provider.CanSave) return null;
            var options = new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = defaultExtension?.TrimStart('.'),
                FileTypeChoices = filters,
                ShowOverwritePrompt = true,
                SuggestedStartLocation = await FolderAsync(provider, startFolder).ConfigureAwait(true),
            };
            IStorageFile? file = await provider.SaveFilePickerAsync(options).ConfigureAwait(true);
            return file?.TryGetLocalPath();
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("The save picker failed.", exception);
            return null;
        }
    }

    /// <summary>Asks for a folder.</summary>
    /// <param name="owner">A control of the window the picker belongs to.</param>
    /// <param name="title">The picker title.</param>
    /// <param name="startFolder">The folder the picker starts in, or <c>null</c>.</param>
    /// <returns>The full path, or <c>null</c>.</returns>
    public static async Task<string?> PickFolderAsync(Visual owner, string title, string? startFolder = null)
    {
        try
        {
            IStorageProvider? provider = ProviderOf(owner);
            if (provider is null || !provider.CanPickFolder) return null;
            var options = new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await FolderAsync(provider, startFolder).ConfigureAwait(true),
            };
            IReadOnlyList<IStorageFolder> folders = await provider.OpenFolderPickerAsync(options).ConfigureAwait(true);
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("The folder picker failed.", exception);
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> OpenFilesCoreAsync(Visual owner, string title, IReadOnlyList<FilePickerFileType>? filters, string? startFolder, bool multiple)
    {
        try
        {
            IStorageProvider? provider = ProviderOf(owner);
            if (provider is null || !provider.CanOpen) return Array.Empty<string>();
            var options = new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = multiple,
                FileTypeFilter = filters,
                SuggestedStartLocation = await FolderAsync(provider, startFolder).ConfigureAwait(true),
            };
            IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(options).ConfigureAwait(true);
            return files.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).ToArray();
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("The file picker failed.", exception);
            return Array.Empty<string>();
        }
    }

    private static IStorageProvider? ProviderOf(Visual owner) => owner is null ? null : TopLevel.GetTopLevel(owner)?.StorageProvider;

    private static async Task<IStorageFolder?> FolderAsync(IStorageProvider provider, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            string path = File.Exists(folder) ? Path.GetDirectoryName(folder) ?? folder : folder;
            return Directory.Exists(path) ? await provider.TryGetFolderFromPathAsync(path).ConfigureAwait(true) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>The clipboard of a window, without exceptions.</summary>
/// <example><c>await ClipboardHelper.SetTextAsync(this, chat.SelectedText);</c></example>
public static class ClipboardHelper
{
    /// <summary>Puts text on the clipboard.</summary>
    /// <param name="owner">A control of the window.</param>
    /// <param name="text">The text.</param>
    /// <returns>Whether it worked.</returns>
    public static async Task<bool> SetTextAsync(Visual owner, string text)
    {
        try
        {
            IClipboard? clipboard = owner is null ? null : TopLevel.GetTopLevel(owner)?.Clipboard;
            if (clipboard is null) return false;
            await clipboard.SetTextAsync(text ?? string.Empty).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("The clipboard could not be written.", exception);
            return false;
        }
    }

    /// <summary>Reads text from the clipboard.</summary>
    /// <param name="owner">A control of the window.</param>
    /// <returns>The text, or <c>null</c> when there is none or it cannot be read.</returns>
    public static async Task<string?> GetTextAsync(Visual owner)
    {
        try
        {
            IClipboard? clipboard = owner is null ? null : TopLevel.GetTopLevel(owner)?.Clipboard;
            return clipboard is null ? null : await clipboard.TryGetTextAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("The clipboard could not be read.", exception);
            return null;
        }
    }
}
