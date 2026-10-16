using System.Runtime.CompilerServices;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Arrow traversal stays inside the current form, independent of the framework's focus groups.</summary>
#pragma warning disable CS0618
internal static class PreferenceNavigation
{
    private static readonly ConditionalWeakTable<View, object> Attached = new();

    public static void Attach(View scope)
    {
        foreach (var field in Descendants(scope).Where(IsField))
        {
            if (Attached.TryGetValue(field, out _)) continue;
            Attached.Add(field, new());
            field.KeyDown += (_, key) =>
            {
                if (key.Handled || key.KeyCode is not (KeyCode.CursorUp or KeyCode.CursorDown)) return;
                var delta = key.KeyCode == KeyCode.CursorDown ? 1 : -1;
                key.Handled = true;
                if (field is TextView)
                { field.InvokeCommand(delta > 0 ? Command.Down : Command.Up); return; }
                if (field is ListView list && list.Source is { Count: > 0 } rows)
                {
                    var next = (list.SelectedItem ?? 0) + delta;
                    if (next >= 0 && next < rows.Count)
                    { list.SelectedItem = next; list.EnsureSelectedItemVisible(); return; }
                }
                var fields = Descendants(scope).Where(view => IsField(view) && Available(view, scope)).ToArray();
                var index = Array.IndexOf(fields, field);
                if (index >= 0 && fields.Length > 0) fields[(index + delta + fields.Length) % fields.Length].SetFocus();
            };
        }
    }

    private static bool IsField(View view) => view.CanFocus && (view is Button or CheckBox or TextField or ListView or TextView)
        && view.Id is not ("preferences-search" or "preferences-categories");

    private static bool Available(View view, View scope)
    {
        for (View? current = view; current is not null; current = current.SuperView)
        { if (!current.Visible || !current.Enabled) return false; if (ReferenceEquals(current, scope)) return true; }
        return false;
    }

    private static IEnumerable<View> Descendants(View view)
    { foreach (var child in view.SubViews) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}
#pragma warning restore CS0618
