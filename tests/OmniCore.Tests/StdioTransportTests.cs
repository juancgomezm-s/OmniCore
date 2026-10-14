using System.Diagnostics;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class StdioTransportTests
{
    [Fact]
    public void Wire_frame_is_bounded_and_decodes_utf8_by_bytes()
    {
        const string message = "español français italiano — 日本語";
        using var encoded = new MemoryStream();
        WireFrame.Write(encoded, "{\"text\":\"" + JsonObj.Escape(message) + "\"}");
        encoded.Position = 0;
        Assert.Contains(message, WireFrame.Read(encoded), StringComparison.Ordinal);

        using var oversized = new MemoryStream(new byte[StdioProtocol.MaximumFrameBytes + 1]);
        Assert.Throws<InvalidDataException>(() => WireFrame.Read(oversized));
        using var invalidUtf8 = new MemoryStream([0xC3, 0x28, 0x0A]);
        Assert.Throws<InvalidDataException>(() => WireFrame.Read(invalidUtf8));
        using var truncated = new MemoryStream([0x7B]);
        Assert.Throws<EndOfStreamException>(() => WireFrame.Read(truncated));
        using var empty = new MemoryStream([0x0A]);
        Assert.Throws<InvalidDataException>(() => WireFrame.Read(empty));
    }

    [Fact]
    public void Versioned_golden_frames_are_valid_and_match_the_capability_contract()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol", "stdio", "1.0", "goldens.jsonl");
        var frames = File.ReadAllLines(path);
        Assert.Equal(8, frames.Length);
        foreach (var frame in frames)
        {
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(frame), 1, StdioProtocol.MaximumFrameBytes);
            var envelope = WireEnvelope.FromJson(frame);
            Assert.Equal("1.0", envelope.Version);
            using var parsed = System.Text.Json.JsonDocument.Parse(frame);
            Assert.Equal("1.0", parsed.RootElement.GetProperty("version").GetString());
        }
        using var negotiated = System.Text.Json.JsonDocument.Parse(frames[1]);
        var capabilities = negotiated.RootElement.GetProperty("payload").GetProperty("capabilities")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(StdioProtocol.SupportedCapabilities, capabilities);
    }

    [Fact]
    public void Oversized_replay_is_rejected_without_emitting_an_oversized_response()
    {
        using var requests = new MemoryStream();
        WireFrame.Write(requests, "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"h1\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}");
        WireFrame.Write(requests, WireEnvelope.Command("r1", "{\"operation\":\"subscribe\",\"fromSequence\":1}").ToJson());
        requests.Position = 0;
        using var response = new MemoryStream();
        using var diagnostics = new StringWriter();
        var exitCode = StdioProtocolHost.Run(new HugeReplayClient(), requests, response, diagnostics,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, exitCode);
        Assert.InRange(response.Length, 1, StdioProtocol.MaximumFrameBytes * 2L + 2);
        response.Position = 0;
        var hello = WireEnvelope.FromJson(WireFrame.Read(response)!);
        var replay = WireEnvelope.FromJson(WireFrame.Read(response)!);
        Assert.Equal(MessageTypes.Response, hello.MessageType);
        Assert.Equal(MessageTypes.Error, replay.MessageType);
        Assert.Contains("unsupported_request", replay.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_cli_stdio_input_and_query_use_a_persistent_child_host()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-stdio-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        var oldWorkingDirectory = Environment.CurrentDirectory;
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);

            var text = "Transport test: English, español, français, italiano, 東京";
            var input = await RunCliAsync("stdio", "input", text);
            Assert.Equal(0, input.ExitCode);
            using var ackJson = System.Text.Json.JsonDocument.Parse(input.Stdout.Split('\n')[0]);
            Assert.Equal("ok", ackJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("Accepted", ackJson.RootElement.GetProperty("outcome").GetString());
            Assert.True(ackJson.RootElement.GetProperty("firstSeq").GetInt64() > 0);
            Assert.True(ackJson.RootElement.GetProperty("lastSeq").GetInt64()
                >= ackJson.RootElement.GetProperty("firstSeq").GetInt64());
            Assert.Contains(text, input.Stdout, StringComparison.Ordinal);

            // This is a new CLI process and a new Host process. The read model and replay come
            // from the journal opened by the first child, without running a simulation.
            var query = await RunCliAsync("stdio", "query", "state");
            Assert.Equal(0, query.ExitCode);
            using var stateJson = System.Text.Json.JsonDocument.Parse(query.Stdout);
            Assert.Equal("omnicore", stateJson.RootElement.GetProperty("runtime").GetString());
            var identity = await RunCliAsync("stdio", "query", "sessionIdentity");
            Assert.Equal(0, identity.ExitCode);
            using var identityJson = System.Text.Json.JsonDocument.Parse(identity.Stdout);
            Assert.True(Guid.TryParse(identityJson.RootElement.GetProperty("sessionId").GetString(), out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldWorkingDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void In_process_client_and_trusted_user_action_boundary_remain_available()
    {
        var client = OmniHost.CreateInMemoryServer();
        Assert.IsAssignableFrom<IOmniClient>(client);
        Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
        Assert.Empty(client.SubscribeSince(0));
    }

    [Fact]
    public async Task Stdio_cli_usage_and_host_diagnostics_are_localized_for_all_four_locales()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-locale-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        var cases = new[] { ("es", "Uso:", "inválido"), ("en", "Usage:", "Invalid"),
            ("fr", "Usage :", "invalide"), ("it", "Uso:", "non valido") };
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.CurrentDirectory = workspace;
            foreach (var (locale, usage, diagnostic) in cases)
            {
                var help = await RunCliAsync("--locale", locale, "stdio");
                Assert.Equal(2, help.ExitCode);
                Assert.Contains(usage, help.Stdout, StringComparison.Ordinal);

                using var host = StartCli("host", "--stdio", "--locale", locale);
                await host.StandardInput.BaseStream.WriteAsync(new byte[] { 0xC3, 0x28, 0x0A },
                    TestContext.Current.CancellationToken);
                host.StandardInput.Close();
                var stdoutTask = host.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderrTask = host.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await WaitForExitAsync(host, TimeSpan.FromSeconds(15));
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                Assert.Equal(2, host.ExitCode);
                Assert.Equal(string.Empty, stdout);
                Assert.Contains(diagnostic, stderr, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Incompatible_protocol_version_is_rejected_by_real_host_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var data = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var config = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            var process = StartCli("host", "--stdio");
            using (process)
            {
                await process.StandardInput.WriteLineAsync("{\"version\":\"9.0\",\"messageType\":\"hello\",\"messageId\":\"m1\",\"payload\":{\"supportedVersions\":[\"9.0\"]}}");
                process.StandardInput.Close();
                string? response;
                try
                {
                    response = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                }
                catch { KillOwnedTestProcess(process); throw; }
                await WaitForExitAsync(process, TimeSpan.FromSeconds(15));
                Assert.Equal(3, process.ExitCode);
                Assert.Contains("incompatible_protocol", response, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Real_host_closes_on_invalid_bounded_frame_and_eof_without_inventing_a_response()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            using (var process = StartCli("host", "--stdio", "--locale", "es"))
            {
                var oversized = new byte[StdioProtocol.MaximumFrameBytes + 1];
                await process.StandardInput.BaseStream.WriteAsync(oversized, TestContext.Current.CancellationToken);
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await WaitForExitAsync(process, TimeSpan.FromSeconds(15));
                Assert.Equal(2, process.ExitCode);
                Assert.Equal(string.Empty, await stdout);
                Assert.Contains("inválido o incompleto", await stderr, StringComparison.OrdinalIgnoreCase);
            }

            using (var process = StartCli("host", "--stdio"))
            {
                process.StandardInput.Close();
                var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                await WaitForExitAsync(process, TimeSpan.FromSeconds(15));
                var stdout = await stdoutTask;
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(string.Empty, stdout);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Cancelled_request_and_disconnected_client_never_return_success()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-cancel-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.CurrentDirectory = workspace;
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            using var client = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace);
            Assert.DoesNotContain("human.authority", client.Capabilities);
            var forgedHumanAction = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"mode.default.set\",\"human\":true,\"mode\":\"Act\"}");
            var rejected = Assert.Throws<InvalidDataException>(() =>
                client.Send(forgedHumanAction, CancellationToken.None));
            Assert.Contains("unsupported_request", rejected.Message, StringComparison.Ordinal);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => client.Query("state", cancelled.Token));
            client.Dispose();
            Assert.Throws<ObjectDisposedException>(() => client.Query("state", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        using var process = StartCli(args);
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await WaitForExitAsync(process, TimeSpan.FromSeconds(45));
        return (process.ExitCode, await stdout, await stderr);
    }

    private static Process StartCli(params string[] args)
    {
        var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
        Assert.True(File.Exists(cliDll), "The CLI project must be built before stdio integration tests run.");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(cliDll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start CLI fixture process.");
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(timeout, TestContext.Current.CancellationToken);
        }
        catch
        {
            KillOwnedTestProcess(process);
            throw;
        }
    }

    private static void KillOwnedTestProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { process.WaitForExit(3000); } catch (InvalidOperationException) { }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "OmniCore.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the OmniCore worktree root.");
    }

    private sealed class HugeReplayClient : IOmniClient
    {
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken) => CommandAck.Ok(command.MessageId);
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) =>
            [WireEnvelope.Event("event-1", "{\"large\":\"" + new string('x', StdioProtocol.MaximumFrameBytes) + "\"}")];
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken) => null;
    }
}
