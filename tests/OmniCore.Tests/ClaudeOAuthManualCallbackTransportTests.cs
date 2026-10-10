using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests del modo manual (§3.3 del plan, grupo 10): parsing de AUTHORIZATION_CODE#STATE sin
/// leer stdin ni abrir navegador.
/// </summary>
public sealed class ClaudeOAuthManualCallbackTransportTests
{
    private const string State = "state-manual-0001";

    [Fact]
    public async Task Pasted_code_hash_state_is_split_into_the_callback()
    {
        var transport = new ClaudeOAuthManualCallbackTransport(_ => Task.FromResult("the-code#the-state"));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var code = await listener.WaitForAuthorizationAsync("the-state", TestContext.Current.CancellationToken);

        Assert.Equal("the-code", code);
    }

    [Fact]
    public async Task Surrounding_whitespace_is_trimmed()
    {
        var transport = new ClaudeOAuthManualCallbackTransport(_ => Task.FromResult("  the-code#the-state\n"));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var code = await listener.WaitForAuthorizationAsync("the-state", TestContext.Current.CancellationToken);

        Assert.Equal("the-code", code);
    }

    [Fact]
    public async Task Pasted_value_without_hash_fails_as_StateMismatch()
    {
        var transport = new ClaudeOAuthManualCallbackTransport(_ => Task.FromResult("solo-el-code"));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var ex = await Assert.ThrowsAsync<OAuthCallbackException>(() =>
            listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthCallbackFailure.StateMismatch, ex.Failure);
    }

    [Fact]
    public async Task Wrong_state_in_paste_is_rejected()
    {
        var transport = new ClaudeOAuthManualCallbackTransport(_ => Task.FromResult("the-code#other-state"));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var ex = await Assert.ThrowsAsync<OAuthCallbackException>(() =>
            listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthCallbackFailure.StateMismatch, ex.Failure);
    }

    [Fact]
    public async Task Manual_failure_reports_body_to_show_to_user()
    {
        var transport = new ClaudeOAuthManualCallbackTransport(_ => Task.FromResult("the-code#other-state"));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        await Assert.ThrowsAsync<OAuthCallbackException>(() =>
            listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken));

        Assert.Equal(400, transport.LastResponse.Status);
        Assert.Equal("Invalid state parameter", transport.LastResponse.Body);
    }

    [Fact]
    public async Task Manual_success_redirects_to_the_success_page()
    {
        var transport = new ClaudeOAuthManualCallbackTransport(_ => Task.FromResult("c#" + State));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        await listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken);

        Assert.Equal(302, transport.LastResponse.Status);
        Assert.Equal(ClaudeOAuthLoopbackListener.SuccessRedirectUrl, transport.LastResponse.Location);
    }

    [Fact]
    public void Manual_transport_has_no_port()
    {
        var transport = new ClaudeOAuthManualCallbackTransport(_ => Task.FromResult(string.Empty));

        Assert.Equal(0, transport.Port);
    }

    [Fact]
    public async Task Cancellation_propagates_out_of_the_input_reader()
    {
        using var cts = new CancellationTokenSource();
        var transport = new ClaudeOAuthManualCallbackTransport(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return string.Empty;
        });
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            listener.WaitForAuthorizationAsync(State, cts.Token));
    }

    [Fact]
    public void Constructor_requires_an_input_reader()
    {
        Assert.Throws<ArgumentNullException>(() => new ClaudeOAuthManualCallbackTransport(null!));
    }
}
