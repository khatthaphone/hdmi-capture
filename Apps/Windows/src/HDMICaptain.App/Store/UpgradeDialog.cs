using HDMICaptain.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HDMICaptain.App.Store;

/// <summary>The upgrade dialog (UpgradeView.swift, task 064 copy): what Pro adds, one-time price, Restore Purchase.</summary>
public static class UpgradeDialog
{
    /// <summary>Pro features shipped on Windows so far (streaming and virtual camera stay hidden, spec §13.8).</summary>
    private static readonly ProFeature[] Shown = [ProFeature.Recording, ProFeature.ReplayBuffer];

    private static string Summary(ProFeature feature) => feature switch
    {
        ProFeature.Recording => Strings.Get("Pro_RecordingSummary"),
        ProFeature.ReplayBuffer => Strings.Get("Pro_ReplaySummary"),
        _ => "",
    };

    // Segoe Fluent Icons: a filled record dot and History (the Save Recent symbol).
    private static string Glyph(ProFeature feature) => ((char)(feature == ProFeature.Recording ? 0xE7C8 : 0xE81C)).ToString();

    /// <summary>Shows the dialog; true when Pro is unlocked when it closes.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, ProGate gate, ProFeature? reason)
    {
        var title = gate.IsPro ? Strings.Get("Pro_TitleOwned") : reason switch
        {
            ProFeature.Recording => Strings.Get("Pro_TitleRecording"),
            ProFeature.ReplayBuffer => Strings.Get("Pro_TitleReplay"),
            _ => Strings.Get("Pro_Title"),
        };
        var panel = new StackPanel { Spacing = 14, MaxWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = gate.IsPro ? Strings.Get("Pro_Thanks") : Strings.Get("Pro_OneTime"),
            Opacity = 0.8,
        });
        foreach (var feature in Shown)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var icon = new FontIcon { Glyph = Glyph(feature), FontSize = 18, Width = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            if (feature == ProFeature.Recording) icon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
            row.Children.Add(icon);
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = feature.Title(), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            text.Children.Add(new TextBlock { Text = Summary(feature), TextWrapping = TextWrapping.WrapWholeWords, Opacity = 0.75, MaxWidth = 360 });
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock { Text = Strings.Get("Pro_FreeNote"), TextWrapping = TextWrapping.WrapWholeWords, Opacity = 0.75 });
        var error = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed), TextWrapping = TextWrapping.WrapWholeWords, Visibility = Visibility.Collapsed };
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = panel,
            RequestedTheme = ElementTheme.Dark,
        };
        if (gate.IsPro)
        {
            dialog.CloseButtonText = Strings.Get("Common_Done");
        }
        else
        {
            dialog.PrimaryButtonText = gate.Price is { } price ? Strings.Format("Pro_BuyPrice", price) : Strings.Get("Pro_Buy");
            dialog.SecondaryButtonText = Strings.Get("Pro_Restore");
            dialog.CloseButtonText = Strings.Get("Pro_NotNow");
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                if (!await gate.PurchaseAsync())
                {
                    error.Text = Strings.Get("Pro_PurchaseFailed");
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
                deferral.Complete();
            };
            dialog.SecondaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                await gate.RestoreAsync();
                if (!gate.IsPro)
                {
                    error.Text = Strings.Get("Pro_NothingToRestore");
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
                deferral.Complete();
            };
        }
        await dialog.ShowAsync();
        return gate.IsPro;
    }
}
