using System.Diagnostics;
using System.Net.Sockets;

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

if (args.Length == 1 && args[0] == "stdin-echo")
{
    Console.Write(Console.ReadLine());
    return 0;
}

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

if (args.Length == 1 && args[0] == "sleep")
{
    await Task.Delay(TimeSpan.FromMinutes(10));
    return 0;
}

return 2;
