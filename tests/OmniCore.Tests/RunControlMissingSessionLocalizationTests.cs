using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class RunControlMissingSessionLocalizationTests
{
    [Theory]
    [InlineData("cancel", "es", "No hay una sesión activa.")]
    [InlineData("cancel", "en", "There is no active session.")]
    [InlineData("cancel", "fr", "Aucune session n’est active.")]
    [InlineData("cancel", "it", "Non c’è una sessione attiva.")]
    [InlineData("interrupt", "es", "No hay una sesión activa.")]
    [InlineData("interrupt", "en", "There is no active session.")]
    [InlineData("interrupt", "fr", "Aucune session n’est active.")]
    [InlineData("interrupt", "it", "Non c’è una sessione attiva.")]
    public async Task Real_host_guard_is_rendered_in_the_client_locale(string command, string locale, string expected)
    {
        var server = OmniHost.CreateInMemoryServer();
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"run." + command + "\"}"), TestContext.Current.CancellationToken);
        Assert.Equal("error", ack.Status);
        Assert.Equal("runtime.session.required", ack.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        Assert.True(ClientCommandRegistry.Default.TryResolve(new(command, [], "Typed"), out var action));
        var text = new Localization(locale);
        Assert.False(text.ResolveDetailed("runtime.session.required").UsedFallback);
        var result = await new ClientActionHandler().ExecuteAsync(action!, new ClientContext(server, text),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Text);
        Assert.Null(server.LastSessionId());
        Assert.Null(server.LastRunId());
    }
}
