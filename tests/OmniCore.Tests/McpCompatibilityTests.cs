using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Sandbox;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class McpCompatibilityTests
{
    private static McpServerDefinition Server => new("fixture","http",null,[],null,
        "https://fixture.invalid/mcp",["fixture.invalid"],true,"user","identity");
    private static HttpResponseMessage Json(string result, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":"+result+"}",Encoding.UTF8,"application/json") };
    private static HttpResponseMessage Error(int code) => new(HttpStatusCode.BadRequest)
    { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":"+code+",\"message\":\"fixture\",\"data\":{\"supported\":[\"1900-01-01\"]}}}",Encoding.UTF8,"application/json") };

    [Fact]
    public async Task Modern_http_preserves_metadata_and_safely_mirrors_primitive_parameter_headers()
    {
        var methods=new List<string>();
        using var handler=new Handler(async (request,cancellation)=>
        {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var method=body.RootElement.GetProperty("method").GetString()!;methods.Add(method);
            var parameters=body.RootElement.GetProperty("params");
            Assert.Equal(McpWire.Version,parameters.GetProperty("_meta").GetProperty(McpWire.MetadataVersion).GetString());
            Assert.Equal(McpWire.Version,request.Headers.GetValues("MCP-Protocol-Version").Single());
            if(method=="server/discover")return Json("{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{}}");
            Assert.Equal("lookup",request.Headers.GetValues("Mcp-Name").Single());
            var header=request.Headers.GetValues("Mcp-Param-Region").Single();
            Assert.StartsWith("=?base64?",header);
            Assert.Equal(" México ",Encoding.UTF8.GetString(Convert.FromBase64String(header[9..^2])));
            Assert.Equal("42",request.Headers.GetValues("Mcp-Param-Count").Single());
            return Json("{\"resultType\":\"complete\",\"content\":[]}");
        });
        using var transport=new McpStreamableHttpTransport(handler);
        using var schema=JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"region\":{\"type\":\"string\",\"x-mcp-header\":\"Region\"},\"count\":{\"type\":\"integer\",\"x-mcp-header\":\"Count\"}}}");
        transport.ConfigureTool(Server,"lookup",schema.RootElement);
        for(var i=0;i<2;i++)await transport.SendAsync(Server,"tools/call","{\"name\":\"lookup\",\"arguments\":{\"region\":\" México \",\"count\":42}}",CancellationToken.None);
        Assert.Equal(new[]{"server/discover","tools/call","tools/call"},methods);
    }

    [Fact]
    public async Task Legacy_http_negotiates_initializes_carries_session_and_deletes_it_at_disposal()
    {
        var methods=new List<string>();
        using var handler=new Handler(async(request,cancellation)=>
        {
            if(request.Method==HttpMethod.Delete)
            { methods.Add("DELETE");Assert.Equal("legacy-fixture",request.Headers.GetValues("Mcp-Session-Id").Single());return new(HttpStatusCode.NoContent); }
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var method=body.RootElement.GetProperty("method").GetString()!;methods.Add(method);
            if(method=="server/discover")return Error(-32602);
            if(method=="initialize")
            {
                Assert.False(body.RootElement.GetProperty("params").TryGetProperty("_meta",out _));
                var answer=Json("{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{\"tools\":{}}}");
                answer.Headers.Add("Mcp-Session-Id","legacy-fixture");return answer;
            }
            Assert.Equal("legacy-fixture",request.Headers.GetValues("Mcp-Session-Id").Single());
            if(method=="notifications/initialized") { Assert.False(body.RootElement.TryGetProperty("id",out _));return new(HttpStatusCode.Accepted); }
            return Json("{\"tools\":[]}");
        });
        using(var transport=new McpStreamableHttpTransport(handler))
        {
            var response=await transport.SendAsync(Server,"server/discover","{}",CancellationToken.None);
            Assert.Contains("2025-11-25",response);
            await transport.SendAsync(Server,"tools/list","{}",CancellationToken.None);
        }
        Assert.Equal(new[]{"server/discover","initialize","notifications/initialized","tools/list","DELETE"},methods);
    }

    [Theory]
    [InlineData(-32022)]
    [InlineData(-32020)]
    public async Task Recognized_modern_http_errors_preserve_code_and_never_fall_back_to_initialize(int code)
    {
        var count=0;
        using var handler=new Handler((_,_)=>{count++;return Task.FromResult(Error(code));});
        using var transport=new McpStreamableHttpTransport(handler);
        var error=await Assert.ThrowsAsync<McpRpcException>(()=>transport.SendAsync(Server,"server/discover","{}",CancellationToken.None));
        Assert.Equal(code,error.Code);Assert.NotNull(error.DataObject);Assert.Equal(1,count);
    }

    [Theory]
    [InlineData("input_required")]
    [InlineData("unknown_result")]
    public void Continuation_is_not_a_success_and_an_effectful_call_is_not_retried(string kind)
    {
        var transport=new FixtureTransport((_,method,_)=>method=="tools/call"
            ? "{\"resultType\":\""+kind+"\",\"inputRequests\":{}}" : "{}");
        var tool=new McpTool(Server,"mutate","fixture","{\"type\":\"object\"}",false,transport,()=>true);
        var catalog=new FakeCatalog().Add(tool);
        var executor=ScriptedToolExecutor.WithWorkspace(catalog,ScriptedPermissionPolicy.WithTool(tool.Descriptor.Id.ToString(),PermissionDecision.Allow),Path.GetTempPath());
        var result=executor.ExecuteToolWithoutJournal(new(ToolCallId.New(),tool.Descriptor.Id,"continuation","{}"),false,CancellationToken.None);
        Assert.False(result.Succeeded);Assert.NotEmpty(result.Events.OfType<ToolCallEffectUnknown>());Assert.Equal(1,transport.Calls);
    }

    [Fact]
    public void Permission_deny_prevents_any_mcp_tool_send()
    {
        var transport=new FixtureTransport((_,_,_)=>"{\"content\":[]}");
        var tool=new McpTool(Server,"mutate","fixture","{\"type\":\"object\"}",false,transport,()=>true);
        var executor=ScriptedToolExecutor.WithWorkspace(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool(tool.Descriptor.Id.ToString(),PermissionDecision.Deny),Path.GetTempPath());
        Assert.False(executor.ExecuteToolWithoutJournal(new(ToolCallId.New(),tool.Descriptor.Id,"deny","{}"),false,CancellationToken.None).Succeeded);
        Assert.Equal(0,transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discovery_reads_pages_past_64_and_isolates_an_unavailable_or_repeating_server(bool repeated)
    {
        var root=Temp();
        try
        {
            var paths=new DefaultPlatformPaths(Path.Combine(root,"data"));var store=new McpServerStore(paths,root,false);
            store.AddHttp("healthy","https://healthy.invalid/mcp");store.AddHttp("broken","https://broken.invalid/mcp");
            store.Trust("healthy",true);store.Trust("broken",true);
            var wire=new FixtureTransport((server,method,args)=>
            {
                if(server.Id=="broken"&&!repeated)throw new IOException("offline fixture");
                if(method=="server/discover")return "{\"supportedVersions\":[\"2026-07-28\"]}";
                var second=args.Contains("cursor",StringComparison.Ordinal);
                var tools=string.Join(",",Enumerable.Range(second?70:0,70).Select(i=>"{\"name\":\"lookup"+i+"\",\"inputSchema\":{\"type\":\"object\"}}"));
                return "{\"tools\":["+tools+"]"+(!second||server.Id=="broken"?",\"nextCursor\":\"second\"":"")+"}";
            });
            var diagnostics=new List<string>();
            var tools=store.DiscoverTools(wire,wire,CancellationToken.None,diagnostics.Add);
            Assert.Equal(140,tools.Count);Assert.All(tools,t=>Assert.StartsWith("mcp.healthy.",t.Descriptor.Id.ToString()));
            Assert.Contains(diagnostics,d=>d.Contains("broken",StringComparison.Ordinal));
        }
        finally{Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData("http://127.0.0.1:18743/mcp",true)]
    [InlineData("http://[::1]:18743/mcp",true)]
    [InlineData("https://remote.invalid/mcp",true)]
    [InlineData("http://192.168.1.1/mcp",false)]
    [InlineData("http://localhost/mcp",false)]
    [InlineData("https://user:pass@remote.invalid/mcp",false)]
    public void Configuration_uses_the_same_endpoint_validator_for_parse_and_cli(string endpoint,bool allowed)
    {
        var root=Temp();
        try
        {
            var paths=new DefaultPlatformPaths(Path.Combine(root,"data"));var store=new McpServerStore(paths,root,false);
            if(!allowed){Assert.Throws<InvalidDataException>(()=>store.AddHttp("fixture",endpoint));return;}
            store.AddHttp("fixture",endpoint);Assert.Equal(endpoint,Assert.Single(store.List()).Endpoint);
        }
        finally{Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData("{\"properties\":{\"x\":{\"type\":\"string\",\"x-mcp-header\":\"bad\\r\\nName\"}}}")]
    [InlineData("{\"properties\":{\"x\":{\"type\":\"number\",\"x-mcp-header\":\"Count\"}}}")]
    [InlineData("{\"items\":{\"properties\":{\"x\":{\"type\":\"string\",\"x-mcp-header\":\"X\"}}}}")]
    public void Invalid_header_annotations_are_rejected_before_a_tool_can_be_exposed(string schema)
    {using var parsed=JsonDocument.Parse(schema);Assert.Throws<InvalidDataException>(()=>McpHeaderBinding.Parse(parsed.RootElement));}

    [Theory]
    [InlineData("mcp",3)]
    [InlineData("mcp-legacy",4)]
    [InlineData("mcp-legacy-silent",3)]
    public async Task Strong_stdio_keeps_one_process_for_discovery_catalog_and_calls(string mode,int sequence)
    {
        if(!OperatingSystem.IsWindows()||!new WindowsAppContainerCapabilitiesProbe().Probe().StrongAvailable)
        {Assert.Skip("Strong Windows backend unavailable.");return;}
        var root=Temp();
        try
        {
            var server=new McpServerDefinition("stdio","stdio",Path.Combine(AppContext.BaseDirectory,"OmniCore.SandboxTestProcess.exe"),
                [mode],root,null,[],false,"user","identity") { StartupTimeoutSeconds = mode == "mcp-legacy-silent" ? 1 : 10 };
            using var transport=new McpStrongStdioTransport(new WindowsAppContainerProcessLauncher());
            await transport.SendAsync(server,"server/discover","{}",CancellationToken.None);
            await transport.SendAsync(server,"tools/list","{}",CancellationToken.None);
            var first=await transport.SendAsync(server,"tools/call","{\"name\":\"fixture.lookup\",\"arguments\":{}}",CancellationToken.None);
            var next=await transport.SendAsync(server,"tools/call","{\"name\":\"fixture.lookup\",\"arguments\":{}}",CancellationToken.None);
            Assert.Contains("sequence="+sequence,first);Assert.Contains("sequence="+(sequence+1),next);
            Assert.Equal(first.Split("pid=")[1],next.Split("pid=")[1]);
            // Independent identities sharing a cwd must not deadlock on an AppContainer profile lease.
            await transport.SendAsync(server with {Id="second"},"server/discover","{}",CancellationToken.None);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void Timeout_configuration_is_bounded_and_does_not_change_existing_sandbox_profile_identity()
    {
        using var json=JsonDocument.Parse("{\"id\":\"fixture\",\"transport\":\"http\",\"endpoint\":\"https://fixture.invalid/mcp\",\"allowedHosts\":[\"fixture.invalid\"],\"requestTimeoutSeconds\":90}");
        Assert.Equal(90,McpServerDefinition.Parse(json.RootElement,"user",false).RequestTimeoutSeconds);
        if(OperatingSystem.IsWindows())
        {
            var root=Path.GetTempPath();Assert.Equal(WindowsAppContainerProcessLauncher.GetProfileName(root),WindowsAppContainerProcessLauncher.GetProfileName(root,null));
            Assert.NotEqual(WindowsAppContainerProcessLauncher.GetProfileName(root,"first"),WindowsAppContainerProcessLauncher.GetProfileName(root,"second"));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);}
    private sealed class FixtureTransport(Func<McpServerDefinition,string,string,string> respond):IMcpRequestTransport
    {
        public int Calls{get;private set;}
        public Task<string> SendAsync(McpServerDefinition server,string method,string args,CancellationToken token)
        {Calls++;return Task.FromResult("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":"+respond(server,method,args)+"}");}
    }
    private static string Temp(){var path=Path.Combine(Path.GetTempPath(),"omni-mcp-compat-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
}
