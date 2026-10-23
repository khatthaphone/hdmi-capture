using System.Runtime.InteropServices;

namespace HDMICaptain.Linux.App;

/// <summary>
/// Accessible names for widgets without visible text (icon buttons, the volume slider). Gir.Core 0.8 does not bind
/// gtk_accessible_update_property (varargs), so this calls the array variant directly.
/// </summary>
internal static unsafe partial class A11y
{
    private const int PropertyLabel = 4; // GTK_ACCESSIBLE_PROPERTY_LABEL

    public static void SetLabel(Gtk.Widget widget, string label)
    {
        using var value = new GObject.Value(label);
        var property = PropertyLabel;
        gtk_accessible_update_property_value(widget.Handle.DangerousGetHandle(), 1, &property, value.Handle.DangerousGetHandle());
    }

    [LibraryImport("libgtk-4.so.1")]
    private static partial void gtk_accessible_update_property_value(nint self, int count, int* properties, nint values);
}
