using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RuijieNetworkAssistant.Views;

public partial class MachineSoulWindow : Window
{
    private const int RenderedFrameSize = 420;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private readonly CancellationTokenSource _loadCancellation = new();
    private IReadOnlyList<AnimationFrame> _beginFrames = Array.Empty<AnimationFrame>();
    private IReadOnlyList<AnimationFrame> _loopFrames = Array.Empty<AnimationFrame>();
    private bool _playingBegin = true;
    private int _frameIndex;

    public MachineSoulWindow()
    {
        InitializeComponent();
        _timer.Tick += OnAnimationTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = _loadCancellation.Token;
            var frames = await Task.Run(() =>
            {
                var begin = LoadFrames("MachineSoulOverloadBegin.gif", token);
                var loop = LoadFrames("MachineSoulOverloadLoop.gif", token);
                return (begin, loop);
            }, token);

            if (!IsLoaded || token.IsCancellationRequested)
            {
                return;
            }

            _beginFrames = frames.begin;
            _loopFrames = frames.loop;
            _playingBegin = true;
            _frameIndex = 0;
            ShowCurrentFrame();
        }
        catch (OperationCanceledException)
        {
            // The window was closed while the packaged animation was being decoded.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"动画资源无法加载：{ex.Message}",
                "机魂安抚",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Close();
        }
    }

    private static IReadOnlyList<AnimationFrame> LoadFrames(string fileName, CancellationToken cancellationToken)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute);
        var resource = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException($"找不到机魂动画资源：{fileName}");

        using var stream = resource.Stream;
        var decoder = new GifBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frames = new List<AnimationFrame>(decoder.Frames.Count);

        foreach (var frame in decoder.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scaled = new TransformedBitmap(frame, new ScaleTransform(
                (double)RenderedFrameSize / frame.PixelWidth,
                (double)RenderedFrameSize / frame.PixelHeight));
            scaled.Freeze();

            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
            converted.Freeze();
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            var bitmap = BitmapSource.Create(
                converted.PixelWidth,
                converted.PixelHeight,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                pixels,
                stride);
            bitmap.Freeze();

            frames.Add(new AnimationFrame(bitmap, GetFrameDelay(frame)));
        }

        return frames;
    }

    private static TimeSpan GetFrameDelay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/grctlext/Delay") is { } value)
            {
                var hundredths = Convert.ToInt32(value);
                if (hundredths > 0)
                {
                    return TimeSpan.FromMilliseconds(Math.Clamp(hundredths * 10, 20, 1000));
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or FormatException)
        {
            // Some GIF encoders omit frame timing metadata; use a calm fallback cadence.
        }

        return TimeSpan.FromMilliseconds(100);
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var frames = _playingBegin ? _beginFrames : _loopFrames;
        if (frames.Count == 0)
        {
            return;
        }

        if (_frameIndex + 1 < frames.Count)
        {
            _frameIndex++;
        }
        else if (_playingBegin)
        {
            // Begin's last pose and loop's first pose match; switch on the exact frame boundary.
            _playingBegin = false;
            _frameIndex = 0;
        }
        else
        {
            _frameIndex = 0;
        }

        ShowCurrentFrame();
    }

    private void ShowCurrentFrame()
    {
        var frames = _playingBegin ? _beginFrames : _loopFrames;
        if (frames.Count == 0)
        {
            return;
        }

        var frame = frames[_frameIndex];
        AnimationImage.Source = frame.Bitmap;
        _timer.Interval = frame.Delay;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _loadCancellation.Cancel();
        _timer.Stop();
        _timer.Tick -= OnAnimationTick;
        AnimationImage.Source = null;
        _loadCancellation.Dispose();
    }

    private sealed record AnimationFrame(BitmapSource Bitmap, TimeSpan Delay);
}
