using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using HDMICaptain.Core;
using HDMICaptain.Media.Video;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace HDMICaptain.App.Views;

/// <summary>
/// Scopes (ScopesPanel.swift, task 097): luma waveform, CbCr vectorscope with 75 % bar targets, and a histogram,
/// fed by <see cref="ScopeSampler"/> while the window is open. A separate window, so nothing here can reach outputs.
/// </summary>
public sealed class ScopesWindow : Window
{
    private static ScopesWindow? _instance;

    private readonly ScopeSampler _sampler;
    private readonly WriteableBitmap _waveform = new(ScopeData.WaveformColumns, ScopeData.Levels);
    private readonly WriteableBitmap _vectorscope = new(ScopeData.VectorSize, ScopeData.VectorSize);
    private readonly WriteableBitmap _histogram = new(ScopeData.Levels, HistogramHeight);
    private const int HistogramHeight = 100;

    private ScopesWindow(ScopeSampler sampler)
    {
        _sampler = sampler;
        Title = Strings.Get("Scopes_Title");
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 760));

        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(16) };
        panel.Children.Add(Section(Strings.Get("Scopes_Waveform"), Scope(_waveform, 256, 200)));
        panel.Children.Add(Section(Strings.Get("Scopes_Vectorscope"), Vectorscope()));
        panel.Children.Add(Section(Strings.Get("Scopes_Histogram"), Scope(_histogram, 256, 90)));
        Content = new Grid { RequestedTheme = ElementTheme.Dark, Children = { new ScrollViewer { Content = panel } } };

        _sampler.DataReady += OnData;
        _sampler.Enabled = true;
        Closed += (_, _) =>
        {
            _sampler.Enabled = false;
            _sampler.DataReady -= OnData;
            _instance = null;
        };
    }

    /// <summary>Opens the single Scopes window, or brings it forward.</summary>
    public static void Show(ScopeSampler sampler)
    {
        _instance ??= new ScopesWindow(sampler);
        _instance.Activate();
    }

    public static void CloseIfOpen() => _instance?.Close();

    // Thread pool → UI thread.
    private void OnData(ScopeData data)
    {
        var waveform = data.WaveformPixels();
        var vectorscope = data.VectorscopePixels();
        var histogram = data.HistogramPixels(HistogramHeight);
        DispatcherQueue.TryEnqueue(() =>
        {
            Blit(_waveform, waveform);
            Blit(_vectorscope, vectorscope);
            Blit(_histogram, histogram);
        });
    }

    // ARGB uint pixels are BGRA bytes in memory, which is WriteableBitmap's layout.
    private static void Blit(WriteableBitmap bitmap, uint[] pixels)
    {
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(MemoryMarshal.AsBytes(pixels.AsSpan()));
        bitmap.Invalidate();
    }

    private static FrameworkElement Section(string title, FrameworkElement content)
    {
        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        section.Children.Add(content);
        return section;
    }

    private static Border Scope(WriteableBitmap bitmap, double width, double height) => new()
    {
        Background = new SolidColorBrush(Colors.Black),
        CornerRadius = new CornerRadius(6),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new Image { Source = bitmap, Width = width * 2, Height = height, Stretch = Stretch.Fill },
    };

    private FrameworkElement Vectorscope()
    {
        const double size = 256;
        var canvas = new Canvas { Width = size, Height = size, Background = new SolidColorBrush(Colors.Black) };
        canvas.Children.Add(new Image { Source = _vectorscope, Width = size, Height = size, Stretch = Stretch.Fill });
        var ring = new Ellipse { Width = size * 0.9, Height = size * 0.9, Stroke = new SolidColorBrush(Colors.White) { Opacity = 0.2 } };
        Canvas.SetLeft(ring, size * 0.05);
        Canvas.SetTop(ring, size * 0.05);
        canvas.Children.Add(ring);
        foreach (var (name, x, y) in ScopeData.BarTargets)
        {
            var box = new Rectangle { Width = 12, Height = 12, Stroke = new SolidColorBrush(Colors.White) { Opacity = 0.6 } };
            Canvas.SetLeft(box, x * size - 6);
            Canvas.SetTop(box, y * size - 6);
            canvas.Children.Add(box);
            var label = new TextBlock { Text = name, FontSize = 10, Opacity = 0.7 };
            Canvas.SetLeft(label, x * size + 8);
            Canvas.SetTop(label, y * size - 7);
            canvas.Children.Add(label);
        }
        return new Border { CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, Child = canvas };
    }
}
