using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class McpImageResultTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p1sAAAAASUVORK5CYII=";

    private static string Result(string data = Png, string mime = "image/png", int count = 1) =>
        JsonSerializer.Serialize(new { content = new object[] { new { type = "text", text = "window observation" } }
            .Concat(Enumerable.Range(0, count).Select(_ => (object)new { type = "image", mimeType = mime, data })).ToArray(), isError = false });

    [Fact]
    public void Inline_image_is_completely_decoded_sensitive_and_not_copied_into_text()
    {
        var root = Temp();
        try
        {
            var store = new FileArtifactStore(root);
            using var document = JsonDocument.Parse(Result());
            var result = McpImageResult.Materialize(document.RootElement, store);
            var artifact = Assert.IsType<ArtifactRef>(result.Artifact);
            Assert.Equal("image/png", artifact.MediaType);
            Assert.Equal(Sensitivity.Sensitive, artifact.Sensitivity);
            Assert.Equal(Convert.FromBase64String(Png), store.GetBytes(artifact.Hash, artifact.Size));
            Assert.DoesNotContain(Png, result.Json);
            Assert.Contains("window observation", result.Json);
            Assert.Contains(artifact.Hash.Value, result.Json);
            Assert.IsType<ImageBlock>(AttachmentContentMaterializer.Read(artifact, store));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("not-base64!", "image/png", 1)]
    [InlineData("YWJj", "image/png", 1)]
    [InlineData(Png, "image/jpeg", 1)]
    [InlineData(Png, "image/png", 2)]
    public void Invalid_or_multiple_images_are_rejected_before_publication(string bytes, string mime, int count)
    {
        var root = Temp();
        try
        {
            var store = new FileArtifactStore(root);
            using var document = JsonDocument.Parse(Result(bytes, mime, count));
            Assert.Throws<InvalidDataException>(() => McpImageResult.Materialize(document.RootElement, store));
            Assert.Empty(Directory.Exists(Path.Combine(root, "blobs"))
                ? Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories) : []);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_binary_store_rejects_inline_image_without_falling_back_to_base64_text()
    {
        using var document = JsonDocument.Parse(Result());
        Assert.Throws<InvalidDataException>(() => McpImageResult.Materialize(document.RootElement, null));
    }

    [Fact]
    public void Authorized_mcp_image_reaches_the_next_model_step_as_an_image_block()
    {
        var root = Temp();
        try
        {
            var artifacts = new FileArtifactStore(Path.Combine(root,"cas"));
            var server = new McpServerDefinition("image-fixture", "http", null, [], null,
                "https://fixture.invalid/mcp", ["fixture.invalid"], true, "user", "identity") { HonorReadOnlyHint = true };
            var transport = new FixedTransport("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":"+Result()+"}");
            var tool = new McpTool(server,"observe","fixture","{\"type\":\"object\"}",true,transport,()=>true);
            var catalog = new FakeCatalog().Add(tool);
            var executor = new ScriptedToolExecutor(catalog,
                ScriptedPermissionPolicy.WithTool(tool.Descriptor.Id.ToString(),PermissionDecision.Allow),root,
                boundary:null,audit:null,interactionResponder:null,isInteractive:false,artifacts:artifacts);
            var store = new InMemoryEventStore(); var codecs = EventCodecs.Create(); var session = SessionId.New();
            var run = TestRun.Open(store,session); var requests = new List<ModelRequest>();
            var turn = new ExplorerTurn((request,_) =>
            {
                requests.Add(request);
                if (requests.Count == 1)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(),"mcp-image",tool.Descriptor.Id.ToString(),"{}")],
                        StopReason.ToolUse,new TokenUsage(1,1,0,0,0),null,new ProviderMetadata("fixture","fixture",null));
                var result = Assert.Single(request.Messages.SelectMany(message=>message.Content).OfType<ToolResultBlock>());
                var image = Assert.Single(result.Content.OfType<ImageBlock>());
                Assert.Equal(Convert.FromBase64String(Png),image.Bytes.ToArray());
                Assert.DoesNotContain(Png,string.Join("",result.Content.OfType<TextBlock>().Select(text=>text.Text)));
                return new ModelResponse([new TextBlock("fixture received MCP image")],StopReason.EndTurn,
                    new TokenUsage(1,1,0,0,0),null,new ProviderMetadata("fixture","fixture",null));
            },executor,catalog,new ContextMaterializer(new FakeTokenCounter(),[]),
                new ExecutionFingerprint("fixture","h","t","c","o","mcp-image-fixture"),
                new ModelSelection(new ModelIdValue("fixture"),32768,ToolMode.Direct,null,inputModalities:["text","image"]),
                store,codecs,artifacts,new InMemoryAuditSink(),new RedactionPolicy());
            var outcome = turn.Ask("Observe the fixture","system",session,run.RunId,run.RootLane,root,CancellationToken.None);
            Assert.True(outcome.StopReason == StopReason.EndTurn,outcome.FailureDiagnostic);
            Assert.Equal(2,requests.Count); Assert.Equal(1,transport.Calls);
        }
        finally { Directory.Delete(root,true); }
    }

    private sealed class FixedTransport(string response) : IMcpRequestTransport
    {
        public int Calls { get; private set; }
        public Task<string> SendAsync(McpServerDefinition server,string method,string parametersJson,CancellationToken token)
        { Calls++;return Task.FromResult(response); }
    }
    private static string Temp()
    {
        var root=Path.Combine(Path.GetTempPath(),"omni-mcp-image-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);return root;
    }
}
