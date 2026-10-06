namespace OmniCore.Tests;

using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

/// <summary>Dos respuestas SSE guionadas de Anthropic para el Fact de continuación del otro partial.</summary>
public sealed partial class ExplorerTurnAnthropicContinuationTests
{
    private const string ToolUseTurnStream = """
event: message_start
data: {"type":"message_start","message":{"id":"msg_fixture_tool","model":"claude-fixture","usage":{"input_tokens":12,"output_tokens":1}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Voy a revisar fixture.txt"}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG-LOCAL-FIXTURE"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Voy a leer fixture.txt"}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: content_block_start
data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_fixture","name":"filesystem.read","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\"fixture.txt\"}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":2}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}

event: message_stop
data: {"type":"message_stop"}

""";

    private const string TextDoneTurnStream = """
event: message_start
data: {"type":"message_start","message":{"id":"msg_fixture_done","model":"claude-fixture","usage":{"input_tokens":9,"output_tokens":0}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"listo"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}

event: message_stop
data: {"type":"message_stop"}

""";

    private static AnthropicMessagesProvider CreateProvider(TwoSseHandler handler) =>
        new(new ProviderDescriptor("anthropic", ProviderFamily.AnthropicMessages, "https://api.example.test",
            AuthConfig.None(), false, false, true), new DummySecrets(),
            () => new HttpClient(handler, disposeHandler: false));

    private sealed class DummySecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => Secret.Of("fixture-no-secret");
    }

    private sealed class TwoSseHandler(string? firstStream = null) : HttpMessageHandler
    {
        private int _calls;

        public List<string> RequestBodies { get; } = [];

        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > 2)
                throw new InvalidOperationException("Fixture: unexpected third Anthropic call.");
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return Sse(_calls == 1 ? firstStream ?? ToolUseTurnStream : TextDoneTurnStream);
        }
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };
}
