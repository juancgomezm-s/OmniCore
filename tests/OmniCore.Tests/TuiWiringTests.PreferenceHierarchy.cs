using System.Drawing;
using OmniCore.Client;
using OmniCore.Cli;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private static void ChooseLocaleDraft(TuiFixture fx, int index) => fx.Invoke(() =>
    {
        var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale");
        list.SelectedItem = index; list.SetFocus(); list.InvokeCommand(Command.Accept);
    });

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void Language_arrows_only_browse_click_or_Enter_chooses_and_Save_applies(int width) => RunTuiTest(fx =>
    {
        var changed = new List<string>();
        fx.StartTui(initialColumns: width, initialRows: 40, localeChanged: changed.Add);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, "general");
        var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale");
        KeyWithEffect(fx, KeyCode.CursorDown, () => list.SelectedItem == 1, "browse English");
        Assert.Equal("es", fx.App.PreferencesLocale); Assert.Equal("es", fx.App.PreferencesDraftLocale); Assert.Empty(changed);
        fx.Invoke(() =>
        {
            var point = list.ViewportToScreen(new Point(2, 2));
            fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = point, Flags = MouseFlags.LeftButtonClicked });
        });
        fx.Wait(() => fx.App.PreferencesDraftLocale == "fr", "one click chooses French without applying it");
        Assert.Equal("es", fx.App.PreferencesLocale); Assert.Empty(changed);
        KeyWithEffect(fx, KeyCode.CursorDown, () => list.SelectedItem == 3, "browse Italian without changing the chosen draft");
        Assert.Equal("fr", fx.App.PreferencesDraftLocale);
        fx.Invoke(() => Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-save").SetFocus());
        ClickThroughApplication(fx, "preferences-save");
        fx.Wait(() => fx.App.PreferencesLocale == "fr", "Save applies the explicitly chosen language");
        Assert.Equal("fr", fx.App.PreferencesLocaleSnapshot!.Locale);
        Assert.Equal(new[] { "fr" }, changed);
    }, noColor: false);

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void Every_category_keeps_arrows_inside_its_submenu_and_information_below_controls(int width) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: width, initialRows: 40);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        foreach (var category in PreferenceCategoryCatalog.All)
        {
            ChoosePreferenceCategory(fx, category.Id);
            var owner = fx.App.Overlay!;
            var panel = Descendants(owner).Single(view => view.Id == "preferences-panel");
            foreach (var info in Descendants(panel).OfType<MenuInformationLabel>())
            {
                Assert.False(info.CanFocus);
                Assert.True(info.Frame.Left >= MenuIndent.Information, info.Id);
            }
            for (var step = 0; step < 24; step++)
            {
                var key = step < 12 ? KeyCode.CursorUp : KeyCode.CursorDown;
                var previous = Descendants(panel).First(view => view.HasFocus && view is Button or CheckBox or TextField or ListView);
                var item = (previous as ListView)?.SelectedItem;
                KeyWithEffect(fx, key, () => !previous.HasFocus || previous is ListView list && list.SelectedItem != item,
                    "arrow moves within " + category.Id);
                fx.Invoke(() =>
                {
                    Assert.Same(owner, fx.App.Overlay);
                    Assert.True(panel.HasFocus, category.Id + " must retain submenu focus at either end");
                    Assert.DoesNotContain(Descendants(owner), view => view.Id == "preferences-categories" && view.HasFocus);
                    Assert.Equal("es", fx.App.PreferencesLocale); Assert.Equal("es", fx.App.PreferencesDraftLocale);
                });
            }
            ExportPreferenceFrame(fx, width, 40, "hierarchy-" + category.Id, owner.Title!.Trim());
            if (width < 100) ClickId(fx, "preferences-back");
        }
    }, noColor: false);

    private static void ActivateSidebarAction(TuiFixture fx, string widgetId, string action, bool mouse = false, string? rowText = null,
        Action? beforeActivate = null)
    {
        if (!fx.App.Sidebar!.Visible) KeyWithEffect(fx, KeyCode.F2, () => fx.App.Sidebar.Visible, "sidebar visible for activation");
        fx.Wait(() => fx.App.SidebarContent!.Viewport.Width > 0 && fx.App.SidebarContent.Lines > 0, "sidebar rows laid out");
        fx.Invoke(() => fx.App.SidebarContent!.SelectWidget(widgetId));
        bool Matches() => fx.App.SidebarContent!.FocusedAction is { } row && row.Action == action
            && (action != "sidebar.expand" || row.Target == widgetId) && (rowText is null || row.Text.Contains(rowText, StringComparison.Ordinal));
        for (var attempt = 0; attempt < 100 && !Matches(); attempt++)
        {
            var before = fx.App.SidebarContent!.FocusedAction;
            KeyWithEffect(fx, KeyCode.Tab, () => fx.App.SidebarContent.FocusedAction != before, "Tab focuses an actual sidebar link");
        }
        Assert.True(Matches(), "Requested sidebar action is reachable with Tab");
        beforeActivate?.Invoke();
        Assert.True(Matches(), "A sidebar refresh must preserve the focused link");
        var activated = false;
        void Observe(WidgetRowModel row) => activated |= row.Action == action && (action != "sidebar.expand" || row.Target == widgetId)
            && (rowText is null || row.Text.Contains(rowText, StringComparison.Ordinal));
        fx.Invoke(() => fx.App.SidebarContent!.RowActivated += Observe);
        try
        {
            if (mouse) fx.Invoke(() =>
            {
                var view = fx.App.SidebarContent!;
                var point = view.ViewportToScreen(new Point(1, view.InsertionPoint.Y - view.Viewport.Y));
                fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = point, Flags = MouseFlags.LeftButtonClicked });
            });
            // Retry only while no activation was delivered; expanding a section must
            // never be toggled twice when the driver delays processing an injected key.
            else
            {
                try { KeyWithEffect(fx, KeyCode.Enter, () => activated, "Enter activates the focused sidebar link"); }
                catch (Exception error)
                {
                    throw new InvalidOperationException("Sidebar activation: focus=" + fx.App.SidebarContent!.HasFocus
                        + "; cursor=" + fx.App.SidebarContent.InsertionPoint + "; row=" + fx.App.SidebarContent.FocusedAction
                        + "; overlay=" + fx.App.Overlay?.Id, error);
                }
            }
            fx.Wait(() => activated, "native sidebar action was delivered");
        }
        finally { fx.Invoke(() => fx.App.SidebarContent!.RowActivated -= Observe); }
    }

    [Fact]
    public void Sidebar_keeps_the_focused_link_when_a_refresh_inserts_wrapped_rows() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 105, initialRows: 40);
        if (!fx.App.Sidebar!.Visible) KeyWithEffect(fx, KeyCode.F2, () => fx.App.Sidebar.Visible, "sidebar open");
        var widget = fx.App.SidebarComposition!.Body.First(section => section.Widget.Id != "core.files").Widget.Id;
        ActivateSidebarAction(fx, widget, "sidebar.expand", beforeActivate: () => fx.Invoke(() =>
        {
            var body = fx.App.SidebarComposition!.Body.Select((section, index) => (section.Widget,
                section.Model is ListWidgetModel model && index == 0 ? (WidgetModel)(model with
                { Rows = new[] { new WidgetRowModel(new string('x', 200)) }.Concat(model.Rows).ToArray() }) : section.Model)).ToArray();
            fx.App.SidebarContent!.Render(body);
        }));
        fx.Wait(() => ((ListWidgetModel)fx.App.SidebarComposition!.Body.Single(section => section.Widget.Id == widget).Model)
            .Rows.Any(row => row.Text == "Menos"), "Enter expands the link after reflow");
    }, noColor: false);

    [Fact]
    public void Sidebar_shows_all_category_summaries_with_independent_More_links() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 105, initialRows: 40);
        if (!fx.App.Sidebar!.Visible) KeyWithEffect(fx, KeyCode.F2, () => fx.App.Sidebar.Visible, "sidebar open");
        Assert.DoesNotContain(Descendants(fx.App.Sidebar!), view => view.Id is "sidebar-prev" or "sidebar-next" or "sidebar-details");
        Assert.True(fx.App.SidebarComposition!.Body.Count > 1);
        Assert.All(fx.App.SidebarComposition.Body, section => Assert.Contains(((ListWidgetModel)section.Model).Rows,
            row => row.Action == "sidebar.expand" && row.Target == section.Widget.Id && row.Text == "Más…"));
        var widget = fx.App.SidebarComposition.Body.First(section => section.Widget.Id != "core.files").Widget.Id;
        ActivateSidebarAction(fx, widget, "sidebar.expand", mouse: true);
        fx.Wait(() => ((ListWidgetModel)fx.App.SidebarComposition!.Body.Single(section => section.Widget.Id == widget).Model).Rows.Any(row => row.Text == "Menos"), "click expands just this category");
        Assert.All(fx.App.SidebarComposition!.Body.Where(section => section.Widget.Id != widget), section =>
            Assert.Contains(((ListWidgetModel)section.Model).Rows, row => row.Text == "Más…"));
        ActivateSidebarAction(fx, widget, "sidebar.expand");
        fx.Wait(() => ((ListWidgetModel)fx.App.SidebarComposition!.Body.Single(section => section.Widget.Id == widget).Model).Rows.Any(row => row.Text == "Más…"), "Enter collapses the same category");
    }, noColor: false);
}
