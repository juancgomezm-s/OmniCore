using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>A one-row native button with no reserved shadow row, including during initialization.</summary>
internal sealed class CompanionRowButton : Button
{
    protected override void OnInitializingShadowStyle(ValueChangingEventArgs<ShadowStyles?> args)
    {
        base.OnInitializingShadowStyle(args);
        args.NewValue = null;
    }
}
