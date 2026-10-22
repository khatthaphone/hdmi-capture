using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HDMICaptain.App.Views;

/// <summary>Connection Help (HelpSheet.swift): the problems people actually hit with capture cards, each with steps.</summary>
public static class ConnectionHelp
{
    private static readonly (string Glyph, string Title, string[] Steps)[] Topics =
    [
        ("\uE7F4", "No HDMI signal",
        [
            "Turn the source on and make sure it isn't asleep.",
            "Unplug the HDMI cable at both ends and plug it back in.",
            "Pick the source's HDMI output (not its built-in screen) and a standard resolution such as 1080p.",
        ]),
        ("\uE72E", "Black picture from a PlayStation or Apple TV",
        [
            "These sources protect video with HDCP, and capture cards can't show protected video.",
            "PS5: Settings › System › HDMI › turn off Enable HDCP. PS4: Settings › System › turn off Enable HDCP.",
            "Apple TV and streaming apps always use HDCP for movies and TV; games and menus usually work.",
        ]),
        ("\uE74F", "No sound",
        [
            "Check that the source isn't muted and that sound is on in HDMI Captain (speaker button on the bar).",
            "Allow desktop apps in Settings › Privacy & security › Microphone; capture cards deliver sound as a microphone.",
            "Choose the audio output in Settings › System › Sound.",
        ]),
        ("\uE8CD", "Capture card not found",
        [
            "Use a UVC capture card (most USB HDMI capture cards are). Cards that need a driver may not appear.",
            "Try another USB port or cable; some cables only charge.",
            "Allow desktop apps in Settings › Privacy & security › Camera.",
        ]),
        ("\uEC4A", "Picture lags or stutters",
        [
            "Plug the card straight into the PC, not through a hub, when you can.",
            "Two cards on one USB 2.0 bus share bandwidth; use separate ports or lower the format in Settings.",
            "Turn on Show Diagnostics to see frame rate and dropped frames.",
        ]),
    ];

    public static async Task ShowAsync(XamlRoot root)
    {
        var panel = new StackPanel { Spacing = 18 };
        foreach (var (glyph, title, steps) in Topics)
        {
            var topic = new StackPanel { Spacing = 6 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
            header.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            topic.Children.Add(header);
            for (var i = 0; i < steps.Length; i++)
                topic.Children.Add(new TextBlock { Text = $"{i + 1}. {steps[i]}", TextWrapping = TextWrapping.WrapWholeWords, Margin = new Thickness(26, 0, 0, 0), Opacity = 0.85 });
            panel.Children.Add(topic);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Connection Help",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        await dialog.ShowAsync();
    }
}
