using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Runtime.InteropServices;

if (OperatingSystem.IsWindows()
    && string.Equals(Path.GetFileName(Environment.ProcessPath), "git.exe", StringComparison.OrdinalIgnoreCase))
{
    return await OmniCore.SandboxTestProcess.WorktreeRetentionGitProxy.RunAsync(args);
}

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

if (args.Length == 3 && args[0] == "managed-llama-fixture")
{
    var port = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    var log = args[2];
    var key = Environment.GetEnvironmentVariable("LLAMA_API_KEY") ?? "";
    File.AppendAllText(log, "STARTED " + Environment.ProcessId + Environment.NewLine);
    // Exercise the host's redirected-output drain before opening the listener. This is
    // intentionally one long line, large enough to exceed the pipe buffer several times.
    Console.Write(new string('x', 3_000_000));
    Console.Out.Flush();

    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    while (true)
    {
        using var client = listener.AcceptTcpClient();
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var request = reader.ReadLine();
        if (string.IsNullOrEmpty(request)) continue; // Empty probe closed without HTTP.
        var authorization = "";
        var contentLength = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null && line.Length != 0)
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                authorization = line[14..].Trim();
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[15..].Trim(), out var parsedLength)) contentLength = parsedLength;
        }
        if (line is null) continue; // Partial request closed before its header terminator.
        if (contentLength > 0)
        {
            var body = new char[contentLength];
            var read = 0;
            while (read < body.Length)
            {
                var count = reader.Read(body, read, body.Length - read);
                if (count == 0) break;
                read += count;
            }
        }

        var verb = request.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        string status;
        string responseBody;
        if (!string.Equals(authorization, "Bearer " + key, StringComparison.Ordinal))
        {
            File.AppendAllText(log, verb + " auth=bad" + Environment.NewLine);
            status = "401 Unauthorized";
            responseBody = "";
        }
        else if (verb == "GET")
        {
            File.AppendAllText(log, "GET auth=ok" + Environment.NewLine);
            status = "200 OK";
            responseBody = "{\"data\":[]}";
        }
        else if (verb == "POST")
        {
            File.AppendAllText(log, "POST auth=ok" + Environment.NewLine);
            status = "200 OK";
            responseBody = "data: {\"choices\":[{\"delta\":{\"content\":\"respuesta-managed\"},\"finish_reason\":null}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                + "data: [DONE]\n";
        }
        else
        {
            status = "404 Not Found";
            responseBody = "";
        }
        var responseBytes = Encoding.UTF8.GetBytes("HTTP/1.1 " + status + "\r\n"
            + (verb == "POST" && status == "200 OK" ? "Content-Type: text/event-stream\r\n" : "Content-Type: application/json\r\n")
            + "Content-Length: " + Encoding.UTF8.GetByteCount(responseBody).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\r\nConnection: close\r\n\r\n" + responseBody);
        stream.Write(responseBytes);
        stream.Flush();
    }
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

if (args.Length == 1 && args[0] == "terminal-echo")
{
    Console.WriteLine("READY");
    await Console.Out.FlushAsync();
    var line = Console.ReadLine();
    if (line is null) return 33;
    var inputConsole = ConsoleNative.GetConsoleMode(ConsoleNative.GetStdHandle(-10), out _);
    var outputConsole = ConsoleNative.GetConsoleMode(ConsoleNative.GetStdHandle(-11), out _);
    int columns;
    int rows;
    try { columns = Console.WindowWidth; rows = Console.WindowHeight; }
    catch (Exception ex) when (ex is IOException or InvalidOperationException)
    { columns = rows = 0; }
    if (!inputConsole || !outputConsole || columns != 100 || rows != 32)
    {
        Console.Error.WriteLine($"ConPTY probe failed: inputConsole={inputConsole}; outputConsole={outputConsole}; size={columns}x{rows}.");
        return !inputConsole ? 36 : !outputConsole ? 37 : 38;
    }
    Console.WriteLine($"ACK:{line};size={columns}x{rows}");
    await Console.Out.FlushAsync();
    return 0;
}

if (args.Length == 2 && args[0] == "persistent-private-writer")
{
    Console.InputEncoding = new UTF8Encoding(false);
    var markerName = args[1];
    if (string.IsNullOrWhiteSpace(markerName) || Path.GetFileName(markerName) != markerName
        || markerName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return 34;

    // Split the two-byte UTF-8 character across writes to exercise the host's incremental decoder.
    var stdout = Console.OpenStandardOutput();
    var readyPrefix = Encoding.UTF8.GetBytes("READY-");
    await stdout.WriteAsync(readyPrefix);
    await stdout.FlushAsync();
    var multibyte = Encoding.UTF8.GetBytes("ñ");
    await stdout.WriteAsync(multibyte.AsMemory(0, 1));
    await stdout.FlushAsync();
    await Task.Delay(20);
    await stdout.WriteAsync(multibyte.AsMemory(1, 1));
    await stdout.WriteAsync(Encoding.UTF8.GetBytes("\n"));
    await stdout.FlushAsync();

    var line = await Console.In.ReadLineAsync();
    if (line is null)
    {
        // EOF is a real control action. Emit a final UTF-8 frame after it so the Host's
        // passive terminal read can prove that buffered output remains visible after Run close.
        await stdout.WriteAsync(Encoding.UTF8.GetBytes("EOF-FINAL-ñ\n"));
        await stdout.FlushAsync();
        return 35;
    }
    if (!string.Equals(line, "finish", StringComparison.Ordinal)) return 35;
    await File.WriteAllTextAsync(markerName, "process-batch-published", new UTF8Encoding(false));
    await stdout.WriteAsync(Encoding.UTF8.GetBytes("ACK:finish\n"));
    await stdout.FlushAsync();
    return 0;
}

if (args.Length == 1 && args[0] == "persistent-process-control")
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.InputEncoding = new UTF8Encoding(false);
    await Console.Out.WriteLineAsync("READY-ñ");
    await Console.Out.FlushAsync();
    var first = await Console.In.ReadLineAsync();
    if (first is null)
    {
        await Console.Out.WriteLineAsync("EOF-FINAL-ñ");
        await Console.Out.FlushAsync();
        return 35;
    }
    if (!string.Equals(first, "hello", StringComparison.Ordinal)) return 47;
    await Console.Out.WriteLineAsync("ACK:hello");
    await Console.Out.FlushAsync();
    // Stay alive after acknowledging input so the Host can exercise a verifiable stop.
    _ = await Console.In.ReadLineAsync();
    return 0;
}

if (args.Length == 2 && args[0] == "spawn-delayed-marker")
{
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        ArgumentList = { "delayed-marker", args[1] },
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Failed to start marker descendant.");
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.WriteLine("DESCENDANT:" + child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Console.Out.FlushAsync();
    await Task.Delay(TimeSpan.FromMinutes(10));
    return 0;
}

if (args.Length == 2 && args[0] == "exit-after-spawn-marker")
{
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        ArgumentList = { "delayed-marker", args[1] },
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Failed to start marker descendant.");
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.WriteLine("DESCENDANT:" + child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Console.Out.FlushAsync();
    return 0;
}

if (args.Length == 2 && args[0] == "delayed-marker")
{
    await Task.Delay(TimeSpan.FromSeconds(2));
    await File.WriteAllTextAsync(args[1], "orphan-ran");
    return 0;
}

if (args.Length == 1 && args[0] == "verify-m9-process-marker")
{
    try
    {
        var marker = await File.ReadAllTextAsync("m9-process-marker.txt");
        return string.Equals(marker, "process-batch-published", StringComparison.Ordinal) ? 0 : 43;
    }
    catch (IOException) { return 44; }
    catch (UnauthorizedAccessException) { return 45; }
}

if (args.Length == 2 && args[0] == "private-process-exec")
{
    var markerName = args[1];
    if (string.IsNullOrWhiteSpace(markerName) || Path.GetFileName(markerName) != markerName
        || markerName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return 39;
    await File.WriteAllTextAsync(markerName, "process-exec-batch-published", new UTF8Encoding(false));
    Console.WriteLine("PROCESS-EXEC-COMPLETE");
    await Console.Out.FlushAsync();
    return 0;
}

if (args.Length == 1 && args[0] == "sdk-home-probe")
{
    var home = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME");
    if (string.IsNullOrWhiteSpace(home)) return 31;
    Directory.CreateDirectory(Path.Combine(home, ".dotnet"));
    File.WriteAllText(Path.Combine(home, ".dotnet", "first-run-sentinel"), "owned-process-state");
    Console.WriteLine("SDK-HOME-WRITABLE");
    Console.WriteLine(home);
    return 0;
}

if (args.Length == 3 && args[0] == "environment-probe")
{
    Console.WriteLine(Environment.GetEnvironmentVariable(args[1]) ?? "<null>");
    Console.WriteLine(Environment.GetEnvironmentVariable(args[2]) is null ? "excluded" : "included");
    return 0;
}

if (args.Length == 1 && args[0] == "stdin-state") return StandardInputProbe.Run();

if (args.Length == 1 && args[0] == "stdin-eof")
{
    var input = Console.In.ReadToEnd();
    Console.Write(input.Length == 0 ? "EOF" : "DATA");
    return input.Length == 0 ? 0 : 29;
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

if (args.Length == 4 && args[0] == "spawn-file-holder")
{
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        ArgumentList = { "hold-file", args[1], args[2] },
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    }) ?? throw new InvalidOperationException("Failed to start file-holder child process.");
    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (!File.Exists(args[2]) && !child.HasExited && DateTimeOffset.UtcNow < deadline)
        await Task.Delay(10);
    if (!File.Exists(args[2])) return 33;
    File.WriteAllText(args[3], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return 0;
}

if (args.Length == 3 && args[0] == "hold-file")
{
    using var held = new FileStream(args[1], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    File.WriteAllText(args[2], "locked");
    await Task.Delay(TimeSpan.FromMinutes(2));
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

static partial class ConsoleNative
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetStdHandle", SetLastError = true)]
    internal static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetConsoleMode", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleMode(nint handle, out uint mode);
}
