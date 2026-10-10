using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests deterministas de la lógica del callback (§2.3 del plan, grupo 3).
/// Puerta de entrada: FakeClaudeOAuthCallbackTransport. Sin sockets ni puertos reales.
/// </summary>
public sealed class ClaudeOAuthLoopbackListenerTests
{
    private const string State = "state-antircs-0001";

    [Fact]
    public async Task WaitForAuthorization_returns_code_when_state_matches()
    {
        var transport = new FakeClaudeOAuthCallbackTransport();
        transport.Enqueue(ClaudeOAuthLoopbackListener.CallbackPath, ("code", "auth-code-xyz"), ("state", State));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var code = await listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken);

        Assert.Equal("auth-code-xyz", code);
    }

    [Fact]
    public async Task Successful_callback_redirects_browser_to_success_page()
    {
        var transport = new FakeClaudeOAuthCallbackTransport();
        transport.Enqueue(ClaudeOAuthLoopbackListener.CallbackPath, ("code", "c"), ("state", State));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        await listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken);

        Assert.Equal(302, transport.LastResponse.Status);
        Assert.Equal(ClaudeOAuthLoopbackListener.SuccessRedirectUrl, transport.LastResponse.Location);
    }

    [Fact]
    public async Task Different_state_is_rejected_as_StateMismatch()
    {
        var transport = new FakeClaudeOAuthCallbackTransport();
        transport.Enqueue(ClaudeOAuthLoopbackListener.CallbackPath, ("code", "c"), ("state", "state-otro-0002"));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var ex = await Assert.ThrowsAsync<OAuthCallbackException>(() =>
            listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthCallbackFailure.StateMismatch, ex.Failure);
        Assert.Equal(400, transport.LastResponse.Status);
    }

    [Fact]
    public async Task Missing_state_is_rejected_as_StateMismatch()
    {
        var transport = new FakeClaudeOAuthCallbackTransport();
        transport.Enqueue(ClaudeOAuthLoopbackListener.CallbackPath, ("code", "c"));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var ex = await Assert.ThrowsAsync<OAuthCallbackException>(() =>
            listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthCallbackFailure.StateMismatch, ex.Failure);
    }

    [Fact]
    public async Task Missing_code_is_rejected_as_NoCode()
    {
        var transport = new FakeClaudeOAuthCallbackTransport();
        transport.Enqueue(ClaudeOAuthLoopbackListener.CallbackPath, ("state", State));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var ex = await Assert.ThrowsAsync<OAuthCallbackException>(() =>
            listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthCallbackFailure.NoCode, ex.Failure);
        Assert.Equal(400, transport.LastResponse.Status);
    }

    [Fact]
    public async Task Other_path_answers_404_and_keeps_waiting_for_the_callback()
    {
        var transport = new FakeClaudeOAuthCallbackTransport();
        transport.Enqueue("/favicon.ico");
        transport.Enqueue(ClaudeOAuthLoopbackListener.CallbackPath, ("code", "after-noise"), ("state", State));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var code = await listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken);

        Assert.Equal("after-noise", code);
        Assert.Equal(404, transport.Responses[0].Response.Status);
        Assert.Equal(302, transport.Responses[1].Response.Status);
    }

    [Fact]
    public async Task Cancellation_before_any_request_cancels_without_leaking()
    {
        var transport = new FakeClaudeOAuthCallbackTransport();
        await using var listener = new ClaudeOAuthLoopbackListener(transport);
        using var cts = new CancellationTokenSource();
        var wait = listener.WaitForAuthorizationAsync(State, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Empty(transport.Responses);
    }

    [Fact]
    public async Task Start_reaches_the_transport()
    {
        var transport = new FakeClaudeOAuthCallbackTransport();
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        await listener.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(transport.Started);
    }

    [Fact]
    public async Task Port_comes_from_the_transport()
    {
        var transport = new FakeClaudeOAuthCallbackTransport { Port = 45678 };
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        Assert.Equal(45678, listener.Port);
    }

    [Fact]
    public async Task Exception_message_does_not_carry_the_code_nor_the_state()
    {
        // INV-016 / ADR-0018: los secretos no llegan a logs ni artefactos.
        var transport = new FakeClaudeOAuthCallbackTransport();
        transport.Enqueue(ClaudeOAuthLoopbackListener.CallbackPath, ("code", "SECRETCODE"), ("state", "SECRETSTATE"));
        await using var listener = new ClaudeOAuthLoopbackListener(transport);

        var ex = await Assert.ThrowsAsync<OAuthCallbackException>(() =>
            listener.WaitForAuthorizationAsync(State, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("SECRETCODE", ex.Message);
        Assert.DoesNotContain("SECRETSTATE", ex.Message);
        Assert.DoesNotContain(State, ex.Message);
    }
}
