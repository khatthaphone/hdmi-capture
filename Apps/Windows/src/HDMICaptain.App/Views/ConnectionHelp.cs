using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HDMICaptain.App.Views;

/// <summary>Connection Help (HelpSheet.swift): the problems people actually hit with capture cards, each with steps.</summary>
public static class ConnectionHelp
{
    private static readonly (string Glyph, string Title, string[] Steps)[] Topics =
    [
        ("\uE7F4", Strings.Get("Status_NoSignalTitle"),
        [
            Strings.Get("Help_NoSignal1"),
            Strings.Get("Help_NoSignal2"),
            Strings.Get("Help_NoSignal3"),
        ]),
        ("\uE72E", Strings.Get("Help_BlackTitle"),
        [
            Strings.Get("Help_Black1"),
            Strings.Get("Help_Black2"),
            Strings.Get("Help_Black3"),
        ]),
        ("\uE74F", Strings.Get("Help_SoundTitle"),
        [
            Strings.Get("Help_Sound1"),
            Strings.Get("Help_Sound2"),
            Strings.Get("Help_Sound3"),
        ]),
        ("\uE8CD", Strings.Get("Help_NotFoundTitle"),
        [
            Strings.Get("Help_NotFound1"),
            Strings.Get("Help_NotFound2"),
            Strings.Get("Help_NotFound3"),
        ]),
        ("\uEC4A", Strings.Get("Help_LagTitle"),
        [
            Strings.Get("Help_Lag1"),
            Strings.Get("Help_Lag2"),
            Strings.Get("Help_Lag3"),
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
                topic.Children.Add(new TextBlock { Text = Strings.Format("Help_Step", i + 1, steps[i]), TextWrapping = TextWrapping.WrapWholeWords, Margin = new Thickness(26, 0, 0, 0), Opacity = 0.85 });
            panel.Children.Add(topic);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Strings.Get("Help_Title"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = Strings.Get("Common_Done"),
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        await dialog.ShowAsync();
    }
}
