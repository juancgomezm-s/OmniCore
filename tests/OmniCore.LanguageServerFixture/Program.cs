using System.Text;
using System.Text.Json;

var mode = args.FirstOrDefault() ?? "normal";
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
while (await ReadMessageAsync(input) is { } message)
{
    using (message)
    {
        var root = message.RootElement;
        if (!root.TryGetProperty("method", out var methodNode)) continue;
        var method = methodNode.GetString();
        if (method == "initialize")
        {
            var id = root.GetProperty("id").Clone();
            if (mode == "malicious")
            {
                await WriteMessagesAsync(output,
                    new { jsonrpc = "2.0", id = 99, method = "workspace/applyEdit", @params = new { edit = new { changes = new { } } } },
                    new { jsonrpc = "2.0", id, result = new { capabilities = new { definitionProvider = true } } });
                continue;
            }
            await WriteMessagesAsync(output,
                new { jsonrpc = "2.0", method = "window/logMessage", @params = new { type = 3, message = "fixture café 🍊" } },
                new { jsonrpc = "2.0", id, result = new { capabilities = new { definitionProvider = true, referencesProvider = true, documentSymbolProvider = true, diagnosticProvider = new { interFileDependencies = false, workspaceDiagnostics = false } }, serverInfo = new { name = "fixture", version = "1" } } });
            continue;
        }
        if (method == "textDocument/didOpen")
        {
            var id = root.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString();
            await WriteMessagesAsync(output, new { jsonrpc = "2.0", method = "textDocument/publishDiagnostics", @params = new { uri = id, diagnostics = Array.Empty<object>() } });
            continue;
        }
        if (method == "test/timeout") continue;
        if (!root.TryGetProperty("id", out var requestId)) continue;
        if (method == "textDocument/diagnostic")
        {
            var uri = root.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString();
            await WriteMessagesAsync(output, new { jsonrpc = "2.0", id = requestId.Clone(), result = new { kind = "full", resultId = "fixture-1", items = new[] { new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 1 } }, severity = 1, message = "diagnóstico café 🧪" } }, uri } });
        }
        else if (method == "textDocument/definition" || method == "textDocument/references")
        {
            var uri = mode == "outside" ? new Uri(Path.Combine(Path.GetTempPath(), "outside.txt")).AbsoluteUri
                : root.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString();
            await WriteMessagesAsync(output, new { jsonrpc = "2.0", id = requestId.Clone(), result = new[] { new { uri, range = new { start = new { line = 2, character = 0 }, end = new { line = 2, character = 5 } } } } });
        }
        else if (method == "textDocument/documentSymbol")
            await WriteMessagesAsync(output, new { jsonrpc = "2.0", id = requestId.Clone(), result = new[] { new { name = "símbolo", kind = 12, range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 3 } }, selectionRange = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 3 } } } } });
        else
            await WriteMessagesAsync(output, new { jsonrpc = "2.0", id = requestId.Clone(), error = new { code = -32601, message = "unknown" } });
    }
}

static async Task<JsonDocument?> ReadMessageAsync(Stream stream)
{
    var header = new List<byte>();
    var window = new Queue<byte>();
    while (header.Count < 8192)
    {
        var one = new byte[1];
        if (await stream.ReadAsync(one) == 0) return null;
        header.Add(one[0]); window.Enqueue(one[0]); if (window.Count > 4) window.Dequeue();
        if (window.SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
    }
    var headerText = Encoding.ASCII.GetString(header.ToArray());
    var line = headerText.Split("\r\n", StringSplitOptions.None).Single(item => item.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
    var length = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
    if (length is < 0 or > 1_048_576) throw new InvalidDataException();
    var body = new byte[length]; var offset = 0;
    while (offset < length)
    {
        var read = await stream.ReadAsync(body.AsMemory(offset));
        if (read == 0) throw new EndOfStreamException();
        offset += read;
    }
    return JsonDocument.Parse(body);
}

static async Task WriteMessagesAsync(Stream stream, params object[] messages)
{
    var frames = new List<byte>();
    foreach (var message in messages)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        frames.AddRange(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        frames.AddRange(body);
    }
    await stream.WriteAsync(frames.ToArray());
    await stream.FlushAsync();
}