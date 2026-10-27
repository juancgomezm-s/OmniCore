using OmniCore.Client;
using OmniCore.Cli;
using Terminal.Gui.Drivers;

namespace OmniCore.Tests;

public sealed class TerminalKeyChordTests
{
    [Theory]
    [InlineData(KeyCode.C | KeyCode.CtrlMask, "Ctrl+C")]
    [InlineData(KeyCode.C | KeyCode.CtrlMask | KeyCode.ShiftMask, "Ctrl+Shift+C")]
    [InlineData(KeyCode.F12 | KeyCode.AltMask, "Alt+F12")]
    [InlineData(KeyCode.CursorLeft | KeyCode.ShiftMask, "Shift+Left")]
    [InlineData(KeyCode.Tab | KeyCode.ShiftMask, "Shift+Tab")]
    [InlineData((KeyCode)'-', "Minus")]
    [InlineData((KeyCode)'+', "Plus")]
    [InlineData(KeyCode.Space, "Space")]
    public void Driver_identity_matches_user_configuration(KeyCode native, string configured)
    {
        Assert.True(TerminalKeyChord.TryConvert(native, out var actual));
        Assert.True(KeyChord.TryParse(configured, out var expected));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void All_keyboard_resources_resolve_without_fallback(string locale)
    {
        var text = new Localization(locale);
        foreach (var key in KeyBindingUiResources.Keys)
        {
            var result = text.ResolveDetailed(key);
            Assert.False(result.UsedFallback);
            Assert.Equal(locale, result.ResourceLocale);
            Assert.NotEqual(key, result.Text);
        }
    }
}
