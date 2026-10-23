using HDMICaptain.Core;

namespace HDMICaptain.Linux.App;

/// <summary>
/// Upgrade to Pro (task 119): what Pro adds, a buy link (the browser), and the key field. The copy follows the
/// other platforms' upgrade sheet; the price and store link are placeholders until the vendor is chosen.
/// </summary>
public static class UpgradeWindow
{
    public const string BuyUrl = "https://example.com/hdmi-captain/pro";

    public static void Show(Gtk.Window parent, LicenceStore licence, ProFeature? reason = null)
    {
        var page = Adw.StatusPage.New();
        page.SetIconName("starred-symbolic");
        page.SetTitle(licence.IsPro ? "HDMI Captain Pro is unlocked" : "Upgrade to HDMI Captain Pro");
        page.SetDescription(licence.IsPro
            ? $"Thank you! Licence {Mask(licence.Key)} is active on this computer."
            : (reason is { } f ? $"{f.Title()} is part of Pro. " : "") +
              "Pro adds recording and Save Recent (keep the last minutes and save them after the moment). One payment, no subscription; the viewer stays free.");

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        box.SetHalign(Gtk.Align.Center);
        var window = Adw.Window.New();
        if (!licence.IsPro)
        {
            var buy = Gtk.Button.NewWithLabel("Buy Pro…");
            buy.AddCssClass("pill");
            buy.AddCssClass("suggested-action");
            buy.OnClicked += (_, _) => Gtk.Functions.ShowUri(window, BuyUrl, 0);
            box.Append(buy);
            var entry = Gtk.Entry.New();
            entry.SetPlaceholderText("Licence key");
            entry.SetWidthChars(36);
            var activate = Gtk.Button.NewWithLabel("Activate");
            var error = Gtk.Label.New("");
            error.AddCssClass("error");
            error.SetWrap(true);
            activate.OnClicked += async (_, _) =>
            {
                activate.SetSensitive(false);
                var message = await licence.ActivateAsync(entry.GetText());
                activate.SetSensitive(true);
                if (message == null) window.Close();
                else error.SetLabel(message);
            };
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            row.Append(entry);
            row.Append(activate);
            box.Append(row);
            box.Append(error);
        }
        else
        {
            var deactivate = Gtk.Button.NewWithLabel("Deactivate on This Computer");
            deactivate.AddCssClass("pill");
            deactivate.OnClicked += (_, _) =>
            {
                licence.Deactivate();
                window.Close();
            };
            box.Append(deactivate);
        }
        page.SetChild(box);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(page);
        window.SetTitle("HDMI Captain Pro");
        window.SetTransientFor(parent);
        window.SetModal(true);
        window.SetDefaultSize(520, 520);
        window.SetContent(toolbar);
        window.Present();
    }

    private static string Mask(string? key) => key is { Length: > 8 } k ? $"{k[..4]}…{k[^4..]}" : "";
}
