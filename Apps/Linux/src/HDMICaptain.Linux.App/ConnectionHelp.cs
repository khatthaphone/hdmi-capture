namespace HDMICaptain.Linux.App;

/// <summary>Connection Help (docs/copy.md): the five common problems with numbered steps, Linux wording.</summary>
public static class ConnectionHelp
{
    public static void Show(Gtk.Window parent)
    {
        var page = Adw.PreferencesPage.New();
        foreach (var (title, steps) in new (string, string[])[]
        {
            (Strings.HelpNotFoundTitle, Strings.HelpNotFound),
            (Strings.HelpNoSignalTitle, Strings.HelpNoSignal),
            (Strings.HelpBlackTitle, Strings.HelpBlack),
            (Strings.HelpLagTitle, Strings.HelpLag),
            (Strings.HelpSoundTitle, Strings.HelpSound),
        })
        {
            var group = Adw.PreferencesGroup.New();
            group.SetTitle(title);
            for (var i = 0; i < steps.Length; i++)
            {
                var label = Gtk.Label.New($"{i + 1}. {steps[i]}");
                label.SetWrap(true);
                label.SetXalign(0);
                label.SetMarginTop(6);
                label.SetMarginBottom(6);
                label.SetMarginStart(12);
                label.SetMarginEnd(12);
                label.SetSelectable(true);
                group.Add(label);
            }
            page.Add(group);
        }
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(page);
        var window = Adw.Window.New();
        window.SetTitle(Strings.HelpTitle);
        window.SetTransientFor(parent);
        window.SetModal(true);
        window.SetDefaultSize(560, 640);
        window.SetContent(toolbar);
        window.Present();
    }
}
