using System.Diagnostics;
using System.Net.Sockets;

if (args.Length == 1 && args[0] == "legacy-stdio")
{
    var helloLine = Console.ReadLine();
    if (helloLine is null) return 31;
    using var helloDocument = System.Text.Json.JsonDocument.Parse(helloLine);
    var helloId = helloDocument.RootElement.GetProperty("messageId").GetString()!;
    Console.WriteLine("{\"version\":\"1.0\",\"messageType\":\"response\",\"messageId\":\"" + helloId
        + "\",\"payload\":{\"protocolVersion\":\"1.0\",\"capabilities\":[\"events.replay\"]},\"causationId\":\""
        + helloId + "\"}");
    await Console.Out.FlushAsync();
    var replayLine = Console.ReadLine();
    if (replayLine is null) return 32;
    using var replayDocument = System.Text.Json.JsonDocument.Parse(replayLine);
    var replayId = replayDocument.RootElement.GetProperty("messageId").GetString()!;
    Console.WriteLine("{\"version\":\"1.0\",\"messageType\":\"response\",\"messageId\":\"" + replayId
        + "\",\"payload\":{\"events\":[]},\"causationId\":\"" + replayId + "\"}");
    await Console.Out.FlushAsync();
    await Console.In.ReadLineAsync();
    return 0;
}

if (args.Length == 3 && args[0] == "session-lock")
{
    return OmniCore.SandboxTestProcess.SessionLockProbe.Run(args[1], args[2]);
}

if (args.Length == 1 && args[0] == "stream")
{
    Console.WriteLine("streamed stdout line");
    await Console.Out.FlushAsync();
    await Console.Error.WriteAsync(new string('e', 1_100_000));
    return 42;
}

if (args.Length == 1 && args[0] == "claude-stream-invalid")
{
    Console.WriteLine("not-json");
    await Console.Out.FlushAsync();
    return 0;
}

if (args.Length == 1 && args[0] == "extension-rpc")
{
    var request = Console.ReadLine();
    if (request is null) return 25;
    using var document = System.Text.Json.JsonDocument.Parse(request);
    var root = document.RootElement;
    if (root.GetProperty("jsonrpc").GetString() != "2.0"
        || root.GetProperty("method").GetString() != "tools/call"
        || root.GetProperty("id").GetString() != "1") return 26;
    Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"fixture-ok\"}]}}");
    return 0;
}

if (args.Length == 3 && args[0] == "extension-rpc-write-marker")
{
    var request = Console.ReadLine();
    if (request is null) return 41;
    using var document = System.Text.Json.JsonDocument.Parse(request);
    var rpc = document.RootElement;
    if (rpc.GetProperty("jsonrpc").GetString() != "2.0"
        || rpc.GetProperty("method").GetString() != "tools/call"
        || rpc.GetProperty("id").GetString() != "1"
        || rpc.GetProperty("params").GetProperty("name").GetString() != "mutate") return 42;

    // ExtensionRpcProcessInvoker intentionally starts children in the package directory.
    // The fixture passes the test workspace explicitly so the support process can verify
    // and write only the exact pre-created workspace marker authorized by its manifest.
    var workspace = Path.GetFullPath(args[1]);
    if (!string.Equals(args[2], "m9-extension-marker.txt", StringComparison.Ordinal)) return 44;
    var marker = Path.GetFullPath(args[2], workspace);
    var workspacePrefix = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    if (!marker.StartsWith(workspacePrefix, OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return 43;

    await File.WriteAllTextAsync(marker, "extension-applied");
    Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"extension-applied\"}]}}");
    return 0;
}

if (args.Length == 1 && args[0] == "stdin-echo")
{
    Console.Write(Console.ReadLine());
    return 0;
}

if (args.Length == 3 && args[0] == "environment-probe")
{
    Console.WriteLine(Environment.GetEnvironmentVariable(args[1]) ?? "<null>");
    Console.WriteLine(Environment.GetEnvironmentVariable(args[2]) is null ? "excluded" : "included");
    return 0;
}

if (args.Length == 1 && args[0] == "stdin-state") return StandardInputProbe.Run();

if (args.Length == 1 && args[0] is "mcp" or "mcp-error" or "mcp-stall")
{
    var line = Console.ReadLine();
    if (line is null) return 27;
    using var request = System.Text.Json.JsonDocument.Parse(line);
    var root = request.RootElement;
    if (root.GetProperty("jsonrpc").GetString() != "2.0"
        || root.GetProperty("id").GetInt64() != 1
        || root.GetProperty("params").GetProperty("_meta")
            .GetProperty("io.modelcontextprotocol/protocolVersion").GetString() != "2026-07-28") return 28;
    if (args[0] == "mcp-stall")
    {
        await Task.Delay(TimeSpan.FromMinutes(5));
        return 0;
    }
    if (args[0] == "mcp-error")
    {
        Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32603,\"message\":\"fixture error\"}}");
        return 0;
    }
    var method = root.GetProperty("method").GetString();
    var result = method == "server/discover"
        ? "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{}}"
        : "{\"tools\":[{\"name\":\"fixture.lookup\",\"inputSchema\":{\"type\":\"object\"}}]}";
    Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + result + "}");
    return 0;
}

if (args.Length == 1 && args[0] is "claude-stream" or "claude-stream-fail")
{
    Console.WriteLine("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"fixture-session\",\"model\":\"claude-haiku-5-5\",\"tools\":[\"Read\"]}");
    await Console.Out.FlushAsync();
    Console.WriteLine("{\"type\":\"result\",\"session_id\":\"fixture-session\",\"is_error\":false,\"result\":\"fixture result\",\"structured_output\":{\"outcome\":\"Succeeded\",\"summary\":\"fixture result\",\"findings\":[],\"filesChanged\":[],\"remainingIssues\":[],\"confidence\":\"Medium\",\"proposedPlanMutations\":[]}}");
    await Console.Out.FlushAsync();
    return args[0] == "claude-stream-fail" ? 7 : 0;
}

if (args.Length == 2 && args[0] == "read")
{
    try
    {
        Console.Write(File.ReadAllText(args[1]));
        return 0;
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
    {
        Console.Write("DENIED");
        return 23;
    }
}

if (args.Length == 2 && args[0] == "spawn")
{
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        ArgumentList = { "child", args[1] },
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Failed to start child process.");
    Console.WriteLine(child.Id);
    await child.WaitForExitAsync();
    return 0;
}

if (args.Length == 2 && args[0] == "child")
{
    var grandchild = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        ArgumentList = { "sleep" },
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Failed to start grandchild process.");
    File.WriteAllText(args[1], grandchild.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Console.WriteLine(grandchild.Id);
    await grandchild.WaitForExitAsync();
    return 0;
}

if (args.Length == 2 && args[0] == "connect")
{
    try
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", int.Parse(args[1]), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Console.Write("CONNECTED");
        return 0;
    }
    catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
    {
        Console.Write("DENIED");
        return 24;
    }
}

if (args.Length == 2 && args[0] == "stdio-stall")
{
    File.WriteAllText(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}

if (args.Length == 1 && args[0] == "sleep")
{
    await Task.Delay(TimeSpan.FromMinutes(10));
    return 0;
}

return 2;
