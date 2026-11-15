using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RemoteControl.Core;
using SkiaSharp;

namespace RemoteControl.Ui.Common;

/// <summary>
/// Shows a still or animated picture stretched to its bounds, as the background of the RC window header
/// (<c>reloadBackground</c>, <c>onGraphicalDraw</c>, <c>TRemoteFrame.cpp:2095-2141, 748-771</c>). PNG, JPEG, BMP, GIF and WebP are
/// decoded with SkiaSharp's <c>SKCodec</c> (animated GIF and WebP play with their frame durations, at least 10 ms, and loop);
/// MNG is read with <see cref="MngImage"/>. An optional tint is painted over the picture with alpha 0.35, or opaque when
/// <see cref="TintSolid"/> is set; a tint whose alpha is 0 is skipped unless solid. The animation timer runs only while the control
/// is in a visible window's visual tree. A missing or broken file shows nothing and writes one log line.
/// </summary>
/// <example>
/// <code>
/// var header = new AnimatedImage();
/// header.ApplyBackgroundOptions(services.Paths, services.Options.Current);   // option background, backgroundtint, backgroundtintsolid
/// // or: header.Load("/path/to/banner.webp"); header.SetTint("#1e1e2e60", solid: false);
/// </code>
/// </example>
public sealed class AnimatedImage : Control
{
    /// <summary>The file shown; setting it loads the file.</summary>
    public static readonly StyledProperty<string?> SourcePathProperty = AvaloniaProperty.Register<AnimatedImage, string?>(nameof(SourcePath));

    /// <summary>The tint, or <c>null</c> for none.</summary>
    public static readonly StyledProperty<Color?> TintColorProperty = AvaloniaProperty.Register<AnimatedImage, Color?>(nameof(TintColor));

    /// <summary>Whether the tint is opaque instead of alpha 0.35.</summary>
    public static readonly StyledProperty<bool> TintSolidProperty = AvaloniaProperty.Register<AnimatedImage, bool>(nameof(TintSolid));

    /// <summary>The largest picture accepted, in pixels.</summary>
    public const long MaximumPixels = 40_000_000;

    /// <summary>The shortest frame duration in milliseconds.</summary>
    public const int MinimumFrameDuration = 10;

    private FrameSource? _source;
    private WriteableBitmap? _frame;
    private DispatcherTimer? _timer;
    private bool _attached;
    private bool _loading;

    static AnimatedImage()
    {
        AffectsRender<AnimatedImage>(TintColorProperty, TintSolidProperty);
    }

    /// <summary>The file shown.</summary>
    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    /// <summary>The tint, or <c>null</c>.</summary>
    public Color? TintColor
    {
        get => GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    /// <summary>Whether the tint is opaque.</summary>
    public bool TintSolid
    {
        get => GetValue(TintSolidProperty);
        set => SetValue(TintSolidProperty, value);
    }

    /// <summary>Where problems are logged (<see cref="AppLog.Default"/> by default).</summary>
    public AppLog Log { get; set; } = AppLog.Default;

    /// <summary>Whether a picture is shown.</summary>
    public bool HasImage => _frame is not null;

    /// <summary>The size of the picture in pixels (0 by 0 without one).</summary>
    public PixelSize PixelSize => _source is null ? default : new PixelSize(_source.Width, _source.Height);

    /// <summary>How many frames the picture has (1 for a still picture, 0 without one).</summary>
    public int FrameCount => _source?.FrameCount ?? 0;

    /// <summary>The frame shown.</summary>
    public int CurrentFrame { get; private set; }

    /// <summary>Whether the picture is animated.</summary>
    public bool IsAnimated => FrameCount > 1;

    /// <summary>Whether the animation timer runs.</summary>
    public bool IsPlaying => _timer?.IsEnabled == true;

    /// <summary>The bitmap of the frame shown (tests and pictures of the control).</summary>
    public Bitmap? CurrentBitmap => _frame;

    /// <summary>The duration of a frame in milliseconds (at least <see cref="MinimumFrameDuration"/>).</summary>
    /// <param name="frame">The frame index.</param>
    /// <returns>The duration; 0 for a still picture.</returns>
    public int FrameDuration(int frame) => _source is null || _source.FrameCount <= 1 ? 0 : Math.Max(MinimumFrameDuration, _source.Duration(frame));

    /// <summary>Loads a picture file; <c>null</c> or empty removes the picture.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Whether a picture is shown; a missing or broken file is logged and shows nothing.</returns>
    public bool Load(string? path)
    {
        _loading = true;
        try
        {
            SetCurrentValue(SourcePathProperty, path);
        }
        finally
        {
            _loading = false;
        }
        return LoadCore(path);
    }

    /// <summary>Shows a picture from memory.</summary>
    /// <param name="content">The file content.</param>
    /// <param name="name">A name for the log.</param>
    /// <returns>Whether a picture is shown.</returns>
    public bool LoadBytes(byte[] content, string name = "picture")
    {
        ArgumentNullException.ThrowIfNull(content);
        Reset();
        try
        {
            FrameSource? source = FrameSource.Create(content, out string? error);
            if (source is null)
            {
                Log.Warn($"The picture {name} could not be read: {error}");
                InvalidateVisual();
                return false;
            }
            if ((long)source.Width * source.Height > MaximumPixels)
            {
                source.Dispose();
                Log.Warn($"The picture {name} is too large ({source.Width} by {source.Height}).");
                InvalidateVisual();
                return false;
            }
            _source = source;
            _frame = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            if (!ShowFrame(0))
            {
                Log.Warn($"The first frame of {name} could not be decoded.");
                Reset();
                InvalidateVisual();
                return false;
            }
            UpdateTimer();
            InvalidateMeasure();
            InvalidateVisual();
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn($"The picture {name} could not be shown.", exception);
            Reset();
            InvalidateVisual();
            return false;
        }
    }

    /// <summary>Sets the tint from an option value (<c>#rrggbbaa</c> or any colour <see cref="ColorParser"/> reads); an unreadable value means no tint.</summary>
    /// <param name="tint">The option <c>backgroundtint</c>.</param>
    /// <param name="solid">The option <c>backgroundtintsolid</c>.</param>
    public void SetTint(string? tint, bool solid)
    {
        TintColor = ColorParser.TryParse(tint, out Color color) ? color : null;
        TintSolid = solid;
    }

    /// <summary>Shows the header background of the options: <c>background</c> (through <see cref="AppPaths.BackgroundFile"/>), <c>backgroundtint</c> and <c>backgroundtintsolid</c>.</summary>
    /// <param name="paths">The program's folders.</param>
    /// <param name="options">The options.</param>
    /// <returns>Whether the picture is shown.</returns>
    public bool ApplyBackgroundOptions(AppPaths paths, RcOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        SetTint(options.BackgroundTint, options.BackgroundTintSolid);
        string path = paths.BackgroundFile(options);
        return string.Equals(path, SourcePath, StringComparison.Ordinal) && HasImage || Load(path);
    }

    /// <summary>Shows the next frame now (tests; the timer does this while the control is shown).</summary>
    /// <returns>Whether a frame was shown.</returns>
    public bool Advance()
    {
        if (_source is null || _source.FrameCount <= 1) return false;
        int next = (CurrentFrame + 1) % _source.FrameCount;
        bool shown = ShowFrame(next);
        if (!shown) StopTimer();
        InvalidateVisual();
        return shown;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (_frame is { } frame) context.DrawImage(frame, new Rect(frame.Size), bounds);
        if (TintColor is { } tint && (tint.A > 0 || TintSolid))
        {
            byte alpha = TintSolid ? (byte)255 : (byte)Math.Round(0.35 * 255);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B)), bounds);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        Size natural = _frame?.Size ?? default;
        return new Size(double.IsInfinity(availableSize.Width) ? natural.Width : availableSize.Width, double.IsInfinity(availableSize.Height) ? natural.Height : availableSize.Height);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourcePathProperty && !_loading) LoadCore(change.GetNewValue<string?>());
        else if (change.Property == IsVisibleProperty) UpdateTimer();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTimer();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        StopTimer();
        base.OnDetachedFromVisualTree(e);
    }

    private bool LoadCore(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Reset();
            InvalidateVisual();
            return false;
        }
        byte[] content;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                Log.Warn("The picture " + path + " does not exist.");
                Reset();
                InvalidateVisual();
                return false;
            }
            if (info.Length > MngImage.MaximumInputSize)
            {
                Log.Warn("The picture " + path + " is too large.");
                Reset();
                InvalidateVisual();
                return false;
            }
            content = File.ReadAllBytes(path);
        }
        catch (Exception exception)
        {
            Log.Warn("The picture " + path + " could not be read.", exception);
            Reset();
            InvalidateVisual();
            return false;
        }
        return LoadBytes(content, path);
    }

    private bool ShowFrame(int index)
    {
        if (_source is null || _frame is null) return false;
        try
        {
            if (!_source.Render(index)) return false;
            using ILockedFramebuffer buffer = _frame.Lock();
            _source.CopyTo(buffer.Address, buffer.RowBytes);
            CurrentFrame = index;
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn("A picture frame could not be decoded.", exception);
            return false;
        }
    }

    private void UpdateTimer()
    {
        if (!_attached || !IsVisible || _source is null || _source.FrameCount <= 1)
        {
            StopTimer();
            return;
        }
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += OnTick;
        }
        _timer.Interval = TimeSpan.FromMilliseconds(FrameDuration(CurrentFrame));
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_attached || _source is null)
        {
            StopTimer();
            return;
        }
        if (!Advance()) return;
        if (_timer is not null) _timer.Interval = TimeSpan.FromMilliseconds(FrameDuration(CurrentFrame));
    }

    private void Reset()
    {
        StopTimer();
        _source?.Dispose();
        _source = null;
        _frame?.Dispose();
        _frame = null;
        CurrentFrame = 0;
    }

    /// <summary>Decodes frames into one canvas: SKCodec for the formats Skia reads, the PNG frames of an MNG otherwise.</summary>
    private abstract class FrameSource : IDisposable
    {
        protected FrameSource(int width, int height)
        {
            Width = width;
            Height = height;
            Canvas = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        }

        public int Width { get; }

        public int Height { get; }

        public abstract int FrameCount { get; }

        protected SKBitmap Canvas { get; }

        public static FrameSource? Create(byte[] content, out string? error)
        {
            error = null;
            if (MngImage.IsMng(content))
            {
                MngParseResult result = MngImage.Parse(content);
                if (result.Animation is not { Frames.Count: > 0 } animation)
                {
                    error = result.Error ?? "the MNG has no frames";
                    return null;
                }
                return MngSource.Open(animation, out error);
            }
            return CodecSource.Open(content, out error);
        }

        public abstract int Duration(int frame);

        public abstract bool Render(int frame);

        public void CopyTo(IntPtr destination, int destinationRowBytes)
        {
            int rowBytes = Width * 4;
            IntPtr source = Canvas.GetPixels();
            int sourceRowBytes = Canvas.RowBytes;
            var row = new byte[rowBytes];
            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(source + y * sourceRowBytes, row, 0, rowBytes);
                Marshal.Copy(row, 0, destination + y * destinationRowBytes, rowBytes);
            }
        }

        public virtual void Dispose() => Canvas.Dispose();
    }

    private sealed class CodecSource : FrameSource
    {
        private readonly SKCodec _codec;
        private readonly SKCodecFrameInfo[] _frames;
        private readonly SKImageInfo _info;
        private int _decoded = -1;

        private CodecSource(SKCodec codec, int width, int height) : base(width, height)
        {
            _codec = codec;
            _frames = codec.FrameInfo ?? Array.Empty<SKCodecFrameInfo>();
            _info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        }

        public override int FrameCount => Math.Max(1, _frames.Length);

        public static CodecSource? Open(byte[] content, out string? error)
        {
            error = null;
            SKCodec? codec = null;
            try
            {
                using var data = SKData.CreateCopy(content);
                codec = SKCodec.Create(data);
                if (codec is null)
                {
                    error = "the format is not known";
                    return null;
                }
                SKImageInfo info = codec.Info;
                if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaximumPixels)
                {
                    error = $"its size {info.Width} by {info.Height} is not accepted";
                    codec.Dispose();
                    return null;
                }
                return new CodecSource(codec, info.Width, info.Height);
            }
            catch (Exception exception)
            {
                codec?.Dispose();
                error = exception.Message;
                return null;
            }
        }

        public override int Duration(int frame) => frame >= 0 && frame < _frames.Length ? _frames[frame].Duration : 0;

        public override bool Render(int frame) => Decode(frame, 0);

        public override void Dispose()
        {
            _codec.Dispose();
            base.Dispose();
        }

        private bool Decode(int frame, int depth)
        {
            if (frame == _decoded) return true;
            if (_frames.Length <= 1 || frame <= 0 && _frames.Length == 0) return DecodeInto(0, -1);
            if (depth > _frames.Length) return false;
            int required = _frames[frame].RequiredFrame;
            if (required < 0)
            {
                Canvas.Erase(SKColors.Transparent);
                return DecodeInto(frame, -1);
            }
            bool priorUsable = _decoded >= required && _decoded < frame && _frames[_decoded].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious;
            if (!priorUsable && !Decode(required, depth + 1)) return false;
            return DecodeInto(frame, _decoded);
        }

        private bool DecodeInto(int frame, int prior)
        {
            var options = new SKCodecOptions(frame, prior);
            SKCodecResult result = _codec.GetPixels(_info, Canvas.GetPixels(), options);
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return false;
            _decoded = frame;
            return true;
        }
    }

    private sealed class MngSource : FrameSource
    {
        private readonly MngAnimation _animation;

        private MngSource(MngAnimation animation, int width, int height) : base(width, height)
        {
            _animation = animation;
        }

        public override int FrameCount => _animation.Frames.Count;

        public static MngSource? Open(MngAnimation animation, out string? error)
        {
            error = null;
            int width = (int)Math.Min(animation.Width, int.MaxValue);
            int height = (int)Math.Min(animation.Height, int.MaxValue);
            if (width <= 0 || height <= 0)
            {
                using SKBitmap? first = SKBitmap.Decode(animation.Frames[0].Png);
                if (first is null)
                {
                    error = "the first MNG frame is not a PNG";
                    return null;
                }
                width = first.Width;
                height = first.Height;
            }
            if ((long)width * height > MaximumPixels)
            {
                error = $"its size {width} by {height} is not accepted";
                return null;
            }
            return new MngSource(animation, width, height);
        }

        public override int Duration(int frame) => frame >= 0 && frame < _animation.Frames.Count ? (int)Math.Min(_animation.Frames[frame].DelayMilliseconds, int.MaxValue) : 0;

        public override bool Render(int frame)
        {
            if (frame < 0 || frame >= _animation.Frames.Count) return false;
            using SKBitmap? decoded = SKBitmap.Decode(_animation.Frames[frame].Png);
            if (decoded is null) return false;
            using var canvas = new SKCanvas(Canvas);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(decoded, new SKRect(0, 0, decoded.Width, decoded.Height));
            canvas.Flush();
            return true;
        }
    }
}
