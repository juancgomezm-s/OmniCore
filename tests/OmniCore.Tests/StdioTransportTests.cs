using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class StdioTransportTests
{
    [Fact]
    public async Task Session_writer_lock_is_exclusive_between_processes_and_released_on_owner_death()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var session = OmniCore.Domain.SessionId.New();
        using (new SqliteEventStore(journal)) { }
        var helper = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
            ? "OmniCore.SandboxTestProcess.exe" : "OmniCore.SandboxTestProcess");
        var start = new ProcessStartInfo(helper)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("session-lock");
        start.ArgumentList.Add(journal);
        start.ArgumentList.Add(session.ToString());
        using var owner = Process.Start(start)!;
        var ready = await owner.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("READY", ready);
        try
        {
            using var contender = new SqliteEventStore(journal);
            Assert.Throws<SessionWriterLeaseUnavailableException>(() => contender.AcquireSessionWriterLease(session));
            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            contender.AcquireSessionWriterLease(session);
            contender.Close();
        }
        finally
        {
            try { if (!owner.HasExited) owner.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { if (!owner.HasExited) await owner.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Command_receipt_survives_reopen_and_never_replays_changed_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var id = Guid.NewGuid().ToString("D");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("safe payload"u8));
        var store = new SqliteEventStore(journal);
        var first = store.ReserveCommandReceipt(id, hash, sessionId: null);
        Assert.Equal(CommandReceiptState.Reserved, first.State);
        var sessionId = OmniCore.Domain.SessionId.New().ToString();
        store.CompleteCommandReceipt(id, hash, "ok", "Accepted", null, 1, 2, sessionId: sessionId);
        store.PurgeSession(OmniCore.Domain.SessionId.Parse(sessionId), TestContext.Current.CancellationToken);
        store.Close();

        using var reopened = new SqliteEventStore(journal);
        var duplicate = reopened.ReserveCommandReceipt(id, hash, sessionId: "session-a");
        Assert.Equal(CommandReceiptState.Completed, duplicate.State);
        Assert.Equal(sessionId, duplicate.Receipt.SessionId);
        Assert.Equal(OmniCore.Infrastructure.CommandReceiptState.Completed, duplicate.State);
        Assert.Equal(CommandReceiptState.IdConflict,
            reopened.ReserveCommandReceipt(id, new string('0', 64), sessionId: "session-a").State);
        Assert.Equal("ok", reopened.ReadCommandReceipt(id)?.AckStatus);
        reopened.Close();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Unread_ack_reconnect_lookup_and_unknown_reservation_never_repeat_command_effects()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-lost-ack-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        Process? firstHost = null;
        SqliteEventStore OpenJournal(string journal, bool readOnly = false)
        {
            try { return new SqliteEventStore(journal, readOnly); }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                var files = string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(journal)!)
                    .Select(path => Path.GetFileName(path) + "=" + new FileInfo(path).Length));
                throw new InvalidOperationException($"Journal open failed: readOnly={readOnly}; "
                    + $"SQLite={error.SqliteErrorCode}/{error.SqliteExtendedErrorCode}; "
                    + $"hostExited={firstHost?.HasExited}; files={files}", error);
            }
        }
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.CurrentDirectory = workspace;
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            var command = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"persist once after an unread acknowledgment\"}");
            firstHost = StartRawHost(cliDll, workspace);
            WireFrame.Write(firstHost.StandardInput.BaseStream,
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"hello-lost-ack\",\"payload\":{\"supportedVersions\":[\"1.0\"],\"client\":\"test\"}}");
            var hello = WireEnvelope.FromJson(WireFrame.Read(firstHost.StandardOutput.BaseStream)!);
            Assert.Equal("response", hello.MessageType);
            var request = WireEnvelope.Command("outer-lost-ack", "{\"operation\":\"command\",\"command\":"
                + command.ToJson() + "}");
            WireFrame.Write(firstHost.StandardInput.BaseStream, request.ToJson());

            CommandReceipt? receipt = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(journal))
                {
                    using var read = OpenJournal(journal, readOnly: true);
                    receipt = read.ReadCommandReceipt(command.MessageId);
                    if (receipt?.State == CommandReceiptState.Completed) break;
                }
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            Assert.NotNull(receipt);
            Assert.Equal(CommandReceiptState.Completed, receipt.State);
            Assert.False(firstHost.HasExited);
            var session = OmniCore.Domain.SessionId.Parse(receipt.SessionId!);
            long committedCount;
            using (var read = OpenJournal(journal, readOnly: true))
                committedCount = read.CountEvents(session);
            firstHost.Kill(entireProcessTree: true); // response was deliberately left unread by this client.
            await firstHost.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The next writer performs WAL crash recovery. Compare its replay against
            // the count observed before the crash, rather than requiring a read-only
            // connection to recover an abruptly terminated Windows WAL.
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            var unknownId = "00000000-0000-7000-8000-000000000099";
            using (var reserveUnknown = OpenJournal(journal))
                Assert.Equal(CommandReceiptState.Reserved,
                    reserveUnknown.ReserveCommandReceipt(unknownId, hash, sessionId: receipt.SessionId).State);

            using var reconnected = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken);
            var recovered = reconnected.LookupCommandResult(command, TestContext.Current.CancellationToken);
            Assert.Equal(receipt.SessionId, recovered.SessionId);
            Assert.Equal(receipt.FirstSequence, recovered.Ack.FirstSeq);
            var duplicate = reconnected.Send(command, TestContext.Current.CancellationToken);
            Assert.Equal(recovered.Ack.FirstSeq, duplicate.FirstSeq);
            var unknown = WireEnvelope.Command(unknownId, command.PayloadJson);
            var unknownAck = reconnected.Send(unknown, TestContext.Current.CancellationToken);
            Assert.Equal("unknown", unknownAck.Status);
            Assert.Contains("command_result_unknown", unknownAck.Error, StringComparison.Ordinal);
            using var after = OpenJournal(journal, readOnly: true);
            Assert.Equal(committedCount, after.CountEvents(session));
            Assert.Equal(CommandReceiptState.Unknown, after.ReadCommandReceipt(unknownId)?.State);
        }
        finally
        {
            if (firstHost is not null)
            {
                try { if (!firstHost.HasExited) firstHost.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { if (!firstHost.HasExited) await firstHost.WaitForExitAsync(TestContext.Current.CancellationToken); }
                catch (InvalidOperationException) { }
                firstHost.Dispose();
            }
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static Process StartRawHost(string cliDll, string workspace)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(cliDll);
        start.ArgumentList.Add("host");
        start.ArgumentList.Add("--stdio");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start raw stdio Host fixture.");
    }

    [Fact]
    public async Task Reconnected_stdio_client_recovers_receipt_without_resending_and_rejects_changed_id_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-reconnect-" + Guid.NewGuid().ToString("N"));
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
            var id = OmniCore.Protocol.Ids.NewV7();
            var command = WireEnvelope.Command(id, "{\"cmd\":\"session.input\",\"text\":\"persisted once, español français italiano\"}");
            CommandAck first;
            using (var client = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken))
                first = client.Send(command, TestContext.Current.CancellationToken);
            Assert.Equal("ok", first.Status);
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, first.Outcome?.Kind);

            using var reconnected = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken);
            var recovered = reconnected.LookupCommandResult(command, TestContext.Current.CancellationToken);
            var replayed = recovered.Ack;
            Assert.Equal(first.FirstSeq, replayed.FirstSeq);
            Assert.Equal(first.LastSeq, replayed.LastSeq);
            Assert.Equal(first.Outcome?.Kind, replayed.Outcome?.Kind);

            var duplicate = reconnected.Send(command, TestContext.Current.CancellationToken);
            Assert.Equal(first.FirstSeq, duplicate.FirstSeq);
            Assert.Equal(first.LastSeq, duplicate.LastSeq);

            var page = reconnected.ReadReplayPage(1, recovered.SessionId, pageSize: 2,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(page.HasGap);
            Assert.True(page.NextSequence <= 3);
            var pinnedHighWater = page.HighWater;
            var cursor = page.NextSequence;
            var pages = 1;
            while (!page.Complete)
            {
                page = reconnected.ReadReplayPage(cursor, page.SessionId, pageSize: 2,
                    highWater: pinnedHighWater, cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(page.HasGap);
                Assert.Equal(pinnedHighWater, page.HighWater);
                Assert.True(page.NextSequence > cursor);
                cursor = page.NextSequence;
                pages++;
                Assert.InRange(pages, 1, 100);
            }
            Assert.True(page.Complete);

            var modified = WireEnvelope.Command(id,
                "{\"cmd\":\"session.input\",\"text\":\"different request\"}");
            var conflict = reconnected.Send(modified, TestContext.Current.CancellationToken);
            Assert.Equal("error", conflict.Status);
            Assert.Contains("command_id_conflict", conflict.Error, StringComparison.Ordinal);
            Assert.NotNull(reconnected.Query("state", TestContext.Current.CancellationToken));
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

        using var noMultiFrame = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => WireFrame.Write(noMultiFrame, "{\"one\":1}\n{\"two\":2}"));
        Assert.Empty(noMultiFrame.ToArray());
        Assert.Throws<InvalidDataException>(() => WireFrame.Write(noMultiFrame, "{\"one\":1}\r"));
        Assert.Empty(noMultiFrame.ToArray());
        WireFrame.Write(noMultiFrame, "{\"text\":\"line1\\nline2\"}");
        Assert.Equal("{\"text\":\"line1\\nline2\"}\n", System.Text.Encoding.UTF8.GetString(noMultiFrame.ToArray()));
    }

    [Fact]
    public void Client_uses_negotiated_legacy_replay_and_rejects_unadvertised_operations()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-legacy-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
                ? "OmniCore.SandboxTestProcess.exe" : "OmniCore.SandboxTestProcess");
            using var client = StdioOmniClient.Start(helper, ["legacy-stdio"], root,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("events.replay", client.Capabilities);
            Assert.DoesNotContain("events.replay.page", client.Capabilities);
            Assert.Empty(client.SubscribeSince(1));
            Assert.Throws<NotSupportedException>(() => client.ReadReplayPage(1,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Throws<NotSupportedException>(() => client.LookupCommandResult(
                WireEnvelope.Command("m9-legacy-cmd", "{\"cmd\":\"session.input\",\"text\":\"x\"}"),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Empty_replay_completes_and_utf8_budgeted_pages_retain_all_events()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-replay-budget-" + Guid.NewGuid().ToString("N"));
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
            using (var emptyHost = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken))
            {
                Assert.Empty(emptyHost.SubscribeSince(1));
                var emptyPage = emptyHost.ReadReplayPage(1, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(emptyPage.Complete);
                Assert.Empty(emptyPage.Events);
                Assert.Null(emptyPage.SessionId);
            }

            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            var session = OmniCore.Domain.SessionId.New();
            var codecs = EventCodecs.Create();
            using (var seed = new SqliteEventStore(journal))
            {
                var stream = new EventStream(seed, codecs, session);
                stream.Append(new OmniCore.Domain.SessionCreated(session,
                    OmniCore.Domain.WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(workspace)).ToString(),
                    workspace, OmniCore.Domain.ProfileId.New(), DateTimeOffset.UtcNow));
                var run = OmniCore.Domain.RunId.New();
                stream.Append(new OmniCore.Domain.RunCreated(run, session, "large replay", OmniCore.Domain.RunMode.Act,
                    OmniCore.Domain.ExecutionStrategy.Direct, OmniCore.Domain.FailurePolicy.BlockDependents,
                    new OmniCore.Domain.TaskBudget(null, null, null, null), OmniCore.Domain.TaskId.New(), DateTimeOffset.UtcNow));
                stream.Append(new OmniCore.Domain.RunStarted(run));
                for (var i = 0; i < 129; i++)
                    stream.Append(new OmniCore.Domain.UserInputReceived(run,
                        System.Text.Json.JsonSerializer.Serialize(new string('x', 10_000)), null));
            }

            using var client = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken);
            var page = client.ReadReplayPage(1, session.ToString(), pageSize: 128,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(page.HasGap);
            Assert.Equal(132, page.HighWater);
            Assert.InRange(page.Events.Count, 1, 127); // the 128-row response exceeds the UTF-8 frame budget.
            var events = new List<WireEnvelope>(page.Events);
            var cursor = page.NextSequence;
            var pages = 1;
            while (!page.Complete)
            {
                page = client.ReadReplayPage(cursor, page.SessionId, pageSize: 128, highWater: 132,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(page.HasGap);
                Assert.Equal(132, page.HighWater);
                Assert.True(page.NextSequence > cursor);
                events.AddRange(page.Events);
                cursor = page.NextSequence;
                Assert.InRange(++pages, 1, 10);
            }
            Assert.Equal(131, events.Count);
            Assert.Equal(131, events.Select(item => item.MessageId).Distinct(StringComparer.Ordinal).Count());

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
    public void Malformed_requests_return_canonical_errors_and_host_continues_without_dispatching_them()
    {
        using var requests = new MemoryStream();
        WireFrame.Write(requests, "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"h1\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}");
        var malformed = new[]
        {
            ("q-missing", "{\"operation\":\"query\"}"),
            ("q-type", "{\"operation\":\"query\",\"name\":3}"),
            ("s-missing", "{\"operation\":\"subscribe\"}"),
            ("s-range", "{\"operation\":\"subscribe\",\"fromSequence\":9223372036854775808}"),
            ("c-missing", "{\"operation\":\"command\"}"),
        };
        foreach (var (id, payload) in malformed)
            WireFrame.Write(requests, WireEnvelope.Command(id, payload).ToJson());
        WireFrame.Write(requests, WireEnvelope.Command("q-valid", "{\"operation\":\"query\",\"name\":\"state\"}").ToJson());
        requests.Position = 0;
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();
        var client = new RecordingClient();
        Assert.Equal(0, StdioProtocolHost.Run(client, requests, output, diagnostics,
            TestContext.Current.CancellationToken));
        Assert.Equal(string.Empty, diagnostics.ToString());
        Assert.Equal(1, client.QueryCount);
        Assert.Equal(0, client.SendCount);
        Assert.Equal(0, client.SubscribeCount);

        output.Position = 0;
        Assert.Equal(MessageTypes.Response, WireEnvelope.FromJson(WireFrame.Read(output)!).MessageType);
        foreach (var _ in malformed)
        {
            var error = WireEnvelope.FromJson(WireFrame.Read(output)!);
            Assert.Equal(MessageTypes.Error, error.MessageType);
            Assert.Contains("unsupported_request", error.PayloadJson, StringComparison.Ordinal);
        }
        var valid = WireEnvelope.FromJson(WireFrame.Read(output)!);
        Assert.Equal(MessageTypes.Response, valid.MessageType);
        Assert.Contains("state", valid.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_timeout_and_cancellation_kill_the_owned_child_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-stall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "child.pid");
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
            ? "OmniCore.SandboxTestProcess.exe" : "OmniCore.SandboxTestProcess");
        try
        {
            var timedOut = Assert.Throws<TimeoutException>(() => StdioOmniClient.Start(executable,
                ["stdio-stall", pidFile], root, TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
            Assert.Contains("negotiation", timedOut.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(pidFile));
            AssertChildExited(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture));

            File.Delete(pidFile);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(2));
#pragma warning disable xUnit1051 // The linked token also carries the test cancellation token.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => StdioOmniClient.Start(
                executable, ["stdio-stall", pidFile], root,
                TimeSpan.FromSeconds(5), cancellation.Token)));
#pragma warning restore xUnit1051
            Assert.True(File.Exists(pidFile));
            AssertChildExited(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Versioned_golden_frames_are_valid_and_match_the_capability_contract()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol", "stdio", "1.0", "goldens.jsonl");
        var frames = File.ReadAllLines(path);
        Assert.Equal(12, frames.Length);
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
    public void Stdio_cli_does_not_treat_a_rejected_outcome_as_success_when_legacy_status_is_ok()
    {
        Assert.False(CliApp.IsStdioAckSuccessful(new CommandAck("c1", "ok", null,
            RuntimeCommandOutcome.Rejected())));
        Assert.True(CliApp.IsStdioAckSuccessful(new CommandAck("c2", "ok", null,
            RuntimeCommandOutcome.Accepted())));
        Assert.False(CliApp.IsStdioAckSuccessful(new CommandAck("c3", "error", null,
            RuntimeCommandOutcome.Accepted())));
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
            using var client = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken);
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

    private static void AssertChildExited(int processId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var child = Process.GetProcessById(processId);
                if (child.HasExited) return;
            }
            catch (ArgumentException) { return; }
            Thread.Sleep(25);
        }
        Assert.Fail($"Owned handshake fixture process {processId} did not exit after cancellation/timeout.");
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

    private sealed class RecordingClient : IOmniClient
    {
        public int QueryCount { get; private set; }
        public int SendCount { get; private set; }
        public int SubscribeCount { get; private set; }
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        {
            SendCount++;
            return CommandAck.Ok(command.MessageId);
        }
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence)
        {
            SubscribeCount++;
            return [];
        }
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
        {
            QueryCount++;
            return new SessionQueryResult(name, "{\"ready\":true}");
        }
    }
}
