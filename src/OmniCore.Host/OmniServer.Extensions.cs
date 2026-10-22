using System.Text.Json;
using System.Text;
using OmniCore.Domain;
using OmniCore.Abstractions;
using OmniCore.Extensions;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SupervisedExtensionProcess> _extensionProcesses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _extensionStarts = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _extensionActivationFailures = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _extensionActivation = new(1, 1);
    private readonly Dictionary<string, (string Content, ExtensionView View)> _extensionViewCache = new(StringComparer.Ordinal);

    internal IExtensionRpcInvoker CreateExtensionInvoker() => new VersionedExtensionInvoker(this);
    private sealed class VersionedExtensionInvoker(OmniServer owner) : IExtensionRpcInvoker
    {
        private readonly ExtensionRpcProcessInvoker _legacy = new();
        public async Task<string> InvokeAsync(DiscoveredExtension extension, string method, string parametersJson,
            ResourceClaims claims, CancellationToken cancellationToken)
        {
            if (extension.Manifest?.ManifestVersion != 2)
                return await _legacy.InvokeAsync(extension, method, parametersJson, claims, cancellationToken).ConfigureAwait(false);
            if (extension.Trust != TrustLevel.Trusted || extension.Status != ExtensionDiscoveryStatus.Ready
                || method is not ("tools/call" or "hooks/execute" or "context/contribute" or "completion/validate" or "completion/gate"))
                throw new UnauthorizedAccessException("extension_rpc_not_exposed");
            var manifest = extension.Manifest;
            var state = new ExtensionPackageManager(owner._platformPathsFactory()).Inspect(manifest.Id!);
            var source = new ComponentSource(SourceKind.Extension, ScopeLevel.User, TrustLevel.Trusted,
                manifest.Id!, manifest.Version!, extension.PackageSha256);
            if (owner._workspaceRoot is null || claims.Process is not { } process
                || process.Executable != ExtensionPackageManager.PackagePath(extension.Directory, manifest.Runtime!.Command!)
                || !process.Args.SequenceEqual(manifest.Runtime.Args ?? [], StringComparer.Ordinal)
                || !new ExtensionBoundary(manifest.Requests?.Permissions ?? new(), manifest.Id!, owner._workspaceRoot, source)
                    .AllowsClaims(claims)
                || state.ManifestSha256 != extension.ManifestSha256 || state.PackageSha256 != extension.PackageSha256)
                throw new UnauthorizedAccessException("extension_intent_boundary_denied");
            using var parameters = JsonDocument.Parse(parametersJson);
            using var boundParameters = new MemoryStream();
            using (var writer = new Utf8JsonWriter(boundParameters))
            {
                writer.WriteStartObject();
                foreach (var property in parameters.RootElement.EnumerateObject())
                    if (property.Name != "_context") property.WriteTo(writer);
                writer.WritePropertyName("_context"); writer.WriteStartObject();
                writer.WriteString("origin", manifest.Id); writer.WriteString("sessionId", owner._lastSessionId?.ToString());
                writer.WriteString("correlationId", OmniCore.Engine.CausationScope.Current switch
                {
                    CommandCausation command => command.CommandId.ToString(), EventCausation sourceEvent => sourceEvent.EventId.ToString(), _ => null,
                });
                writer.WriteEndObject(); writer.WriteEndObject();
            }
            using var bound = JsonDocument.Parse(boundParameters.ToArray());
            var runtime = await owner.ActivateExtensionAsync(extension, state, owner._lastSessionId?.ToString(),
                method == "hooks/execute" ? "onSession" : "onCommand", cancellationToken).ConfigureAwait(false);
            var result = await runtime.InvokeAsync(method, bound.RootElement, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":" + result.GetRawText() + "}";
        }
    }

    internal SessionQueryResult QueryExtensionPlatform(string name, CancellationToken token)
    {
        if (name is "extensions.context" or "extensions.context.catalog") return QueryExtensionContext(name);
        if (name.StartsWith("extensions.notifications:", StringComparison.Ordinal)) return QueryExtensionNotifications(name);
        var manager = new ExtensionPackageManager(_platformPathsFactory());
        if (name.StartsWith("extensions.configuration:", StringComparison.Ordinal))
        {
            if (name.Length > 8192) throw new ArgumentException("extension_request_limit");
            using var configurationRequest = JsonDocument.Parse(Uri.UnescapeDataString(name[25..]));
            var configurationExtension = manager.Find(configurationRequest.RootElement.GetProperty("extensionId").GetString()!);
            return new("extensions.configuration", ExtensionConfiguration(configurationExtension, "config.get", configurationRequest.RootElement).GetRawText());
        }
        if (name == "extensions.packages")
        {
            var packages = manager.List().Select(package =>
            {
                package = package with { ContextContributors = manager.Find(package.Id).Manifest?.Provides?.ContextContributors };
                var running = _extensionProcesses.FirstOrDefault(pair => pair.Key.StartsWith(package.Id + ":", StringComparison.Ordinal)).Value;
                if (running is null && _extensionActivationFailures.TryGetValue(package.Id, out var failure))
                    return package with { Protection = "blocked", Diagnostic = failure };
                return running is null ? package : package with { State = package.Enabled ? running.State : package.State,
                    Protection = running.State == "active" ? running.Protection : "blocked",
                    Diagnostic = running.Diagnostics.LastOrDefault() ?? package.Diagnostic };
            }).ToArray();
            return new(name, JsonSerializer.Serialize(packages, ExtensionJson.Default.ExtensionPackageStateArray));
        }
        if (name == "extensions.views")
        {
            if (_workspaceRoot is null) return new(name, "[]");
            var views = ProjectExtensionPackageCatalog.Ready(_workspaceRoot!, _platformPathsFactory(),
                new WorkspaceTrustStore(_platformPathsFactory()).IsTrusted(_workspaceRoot!))
                .SelectMany(package => (package.Extension.Manifest?.Provides?.Views ?? []).Select(view =>
                    "{" + JsonObj.Field("extensionId", package.Extension.Manifest!.Id!) + "," + JsonObj.Field("viewId", view.Id!)
                    + "," + JsonObj.Field("kind", view.Kind!) + "}"));
            return new(name, "[" + string.Join(",", views) + "]");
        }
        if (name.Length > 8192) throw new ArgumentException("extension_request_limit");
        var request = ExtensionProtocol.DecodeRequest(Uri.UnescapeDataString(name[16..]));
        var extension = manager.Find(request.ExtensionId); var state = manager.Inspect(request.ExtensionId);
        if (!state.Enabled || state.Diagnostic is not null) throw new UnauthorizedAccessException("extension_not_enabled");
        var view = extension.Manifest?.Provides?.Views?.SingleOrDefault(item => item.Id == request.ViewId)
            ?? throw new ArgumentException("extension_view_missing");
        if (request.ClientKinds is not null && !request.ClientKinds.Contains(view.Kind!, StringComparer.Ordinal))
            return new(name, ExtensionProtocol.Encode(new(request.ExtensionId + "." + request.ViewId, request.ExtensionId,
                view.Kind!, 0, "unsupported", [], [], "extension.renderer.unavailable")));
        if (!state.GrantedCapabilities.Contains("presentation.read", StringComparer.Ordinal)) throw new UnauthorizedAccessException("extension_capability_denied");
        var session = _lastSessionId?.ToString();
        ExtensionView result;
        if (view.Source == "extension")
        {
            // Never activate from a catalog/list query; only explicit view invocation reaches here.
            var process = CompleteWorkspaceTask(() => ActivateExtensionAsync(extension, state, session, "onView", token));
            using var parameters = JsonDocument.Parse(ExtensionProtocol.EncodeRequest(request));
            var response = CompleteWorkspaceTask(() => process.InvokeAsync("view.read", parameters.RootElement, TimeSpan.FromSeconds(15), token));
            result = ExtensionProtocol.Decode(response.GetRawText());
            if (result.ExtensionId != request.ExtensionId || result.Id != request.ExtensionId + "." + request.ViewId
                || result.Items.Count > ExtensionProtocol.MaximumItems || result.Kind != view.Kind)
                throw new InvalidDataException("extension_view_identity_invalid");
        }
        else
        {
            var method = view.Source switch { "workspace" => request.Search.Length == 0 ? "workspace.directory" : "workspace.search", "history" => "history.timeline", _ => "sessions.agents" };
            using var parameters = JsonDocument.Parse("{" + JsonObj.Field("path", request.Path) + "," + JsonObj.Field("search", request.Search) + "}");
            var data = CompleteWorkspaceTask(() => ExtensionServiceAsync(extension, state, session, method, parameters.RootElement, token));
            var items = ViewItems(view.Source!, data);
            JsonElement? metadata = null;
            if (view.Source == "workspace")
            {
                metadata = CompleteWorkspaceTask(() => ExtensionServiceAsync(extension, state, session, "git.status", parameters.RootElement, token));
                if (state.GrantedCapabilities.Contains("history.read", StringComparer.Ordinal) && session is not null)
                {
                    var activity = CompleteWorkspaceTask(() => ExtensionServiceAsync(extension, state, session, "history.timeline", parameters.RootElement, token));
                    var timeline = activity.GetProperty("timeline").EnumerateArray().ToArray();
                    items = items.Select(item =>
                    {
                        var latest = timeline.LastOrDefault(row => row.GetProperty("files").EnumerateArray().Any(file => file.GetString() == item.Id));
                        return latest.ValueKind == JsonValueKind.Undefined ? item : item with { Detail = latest.GetProperty("activity").GetString()
                            + " · " + latest.GetProperty("agent").ToString() };
                    }).ToArray();
                }
            }
            if (view.Source == "agents" && data.ValueKind == JsonValueKind.Object)
            {
                using var summary = JsonDocument.Parse("{" + string.Join(",", data.EnumerateObject()
                    .Where(property => property.Name != "lanes").Select(property => JsonObj.FieldRaw(property.Name, property.Value.GetRawText()))) + "}");
                metadata = summary.RootElement.Clone();
            }
            var actions = view.Source == "history" ? items.Where(item => item.Data is { } point && HasRecoverableCheckpoint(point))
                .Select(item => new ExtensionViewAction("alternative", "extension.action.alternative", true, item.Id)).ToArray()
                : Array.Empty<ExtensionViewAction>();
            result = new(request.ExtensionId + "." + request.ViewId, request.ExtensionId, view.Kind!, 0,
                items.Count == ExtensionProtocol.MaximumItems || data.ValueKind == JsonValueKind.Null || session is null && view.Source == "history"
                    || data.ValueKind == JsonValueKind.Object && data.TryGetProperty("projectionUnavailable", out var unavailable) && unavailable.ValueKind == JsonValueKind.True
                    ? "partial" : "ready", items, actions,
                view.Source == "history" ? session is null ? "extension.session.unavailable" : "extension.history.files_and_conversation_only"
                    : data.ValueKind == JsonValueKind.Null ? "extension.session.unavailable" : null, Metadata: metadata,
                Columns: view.Source == "agents" ? [new("objective", "extension.agent.objective"),
                    new("model", "extension.agent.model"), new("laneState", "extension.agent.state"),
                    new("tool", "extension.agent.tool"), new("tokens", "extension.agent.tokens", "number", "right")] : null);
        }
        if (_workspaceRoot is null) throw new IOException("workspace_not_bound");
        result = result with { Actions = result.Actions.Select(action => action.ConfigurationKey is null ? action : action with
        {
            Enabled = action.Enabled && state.GrantedCapabilities.Contains("configuration.write", StringComparer.Ordinal)
                && extension.Manifest?.Configuration?.Contains(action.ConfigurationKey, StringComparer.Ordinal) == true
                && (action.ConfigurationScope != "session" || session is not null),
            DisabledReason = state.GrantedCapabilities.Contains("configuration.write", StringComparer.Ordinal)
                && extension.Manifest?.Configuration?.Contains(action.ConfigurationKey, StringComparer.Ordinal) == true
                && (action.ConfigurationScope != "session" || session is not null) ? action.DisabledReason : "extension.form.permission",
        }).ToArray() };
        result = result with { Scope = new(WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(_workspaceRoot)).ToString(), session,
            state.ManifestSha256, state.PackageSha256) };
        var cacheKey = string.Join('\0', _workspaceRoot, session, request.ExtensionId, request.ViewId, request.Path, request.Search);
        lock (_extensionViewCache)
        {
            var content = ExtensionProtocol.Encode(result with { Revision = 0 });
            _extensionViewCache.TryGetValue(cacheKey, out var prior);
            var revision = prior.View is null ? 1 : prior.Content == content ? prior.View.Revision : checked(prior.View.Revision + 1);
            result = result with { Revision = revision, Scope = result.Scope with { SnapshotId = prior.View is not null && prior.Content == content
                ? prior.View.Scope?.SnapshotId : Guid.NewGuid().ToString("N") } };
            if (_extensionViewCache.Count >= 128) _extensionViewCache.Remove(_extensionViewCache.Keys.First());
            _extensionViewCache[cacheKey] = (content, result);
            if (request.AfterRevision is { } after && prior.View is not null && after == prior.View.Revision)
                result = result with { Reset = false,
                    ItemOrder = result.Items.Select(item => item.Id).ToArray(),
                    Items = result.Items.Where(item => !prior.View.Items.Any(old => old.Id == item.Id && old.Label == item.Label
                        && old.Detail == item.Detail && old.ParentId == item.ParentId && old.HasChildren == item.HasChildren
                        && old.Data?.GetRawText() == item.Data?.GetRawText()
                        && (old.Cells is null && item.Cells is null || old.Cells is not null && item.Cells is not null
                            && old.Cells.Count == item.Cells.Count && old.Cells.All(cell => item.Cells.TryGetValue(cell.Key, out var nextCell)
                                && nextCell.GetRawText() == cell.Value.GetRawText())))).ToArray(),
                    RemovedIds = prior.View.Items.Where(item => !result.Items.Any(next => next.Id == item.Id)).Select(item => item.Id).ToArray() };
        }
        var json = ExtensionProtocol.Encode(result);
        if (Encoding.UTF8.GetByteCount(json) > ExtensionProtocol.MaximumFrameBytes) throw new IOException("extension_view_frame_limit");
        return new("extensions.view", json);
    }

    private static bool HasRecoverableCheckpoint(JsonElement point) => point.ValueKind == JsonValueKind.Object
        && point.TryGetProperty("checkpoint", out var checkpoint) && checkpoint.ValueKind == JsonValueKind.Object
        && checkpoint.TryGetProperty("snapshot", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object
        && (!checkpoint.TryGetProperty("unavailableReason", out var reason) || reason.ValueKind == JsonValueKind.Null);

    private static IReadOnlyList<ExtensionViewItem> ViewItems(string source, JsonElement data)
    {
        var field = source switch { "workspace" => "files", "history" => "timeline", _ => "lanes" };
        if (data.ValueKind == JsonValueKind.Null) return [];
        if (source == "workspace" && data.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
            return matches.EnumerateArray().Take(ExtensionProtocol.MaximumItems)
                .Select(path => new ExtensionViewItem(path.GetString()!, path.GetString()!)).ToArray();
        if (!data.TryGetProperty(field, out var rows) || rows.ValueKind != JsonValueKind.Array)
            return [new("summary", "extension.view.summary", Data: data.Clone())];
        return rows.EnumerateArray().Take(ExtensionProtocol.MaximumItems).Select((row, index) =>
        {
            string Text(string key, string fallback) => row.TryGetProperty(key, out var value) ? value.ToString() : fallback;
            var id = source == "workspace" ? Text("path", index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : Text("sequence", Text("laneId", Text("id", index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            var label = source == "workspace" ? Text("name", id) : source == "history" ? Text("kind", "") + " · " + Text("text", id)
                : Text("laneState", "") + " · " + Text("objective", id);
            Dictionary<string, JsonElement>? cells = null;
            if (source == "agents")
            {
                cells = new(StringComparer.Ordinal);
                foreach (var key in new[] { "objective", "model", "laneState" })
                    if (row.TryGetProperty(key, out var cell)) cells[key] = cell.Clone();
                if (row.TryGetProperty("budget", out var budget) && budget.ValueKind == JsonValueKind.Object
                    && budget.TryGetProperty("tokensUsed", out var tokens)) cells["tokens"] = tokens.Clone();
                if (row.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
                {
                    var tool = transcript.EnumerateArray().LastOrDefault(entry => entry.TryGetProperty("kind", out var kind) && kind.GetString() == "tool");
                    if (tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("text", out var toolName)) cells["tool"] = toolName.Clone();
                }
            }
            return new ExtensionViewItem(id, label.Length > 240 ? label[..240] : label,
                HasChildren: row.TryGetProperty("directory", out var directory) && directory.ValueKind == JsonValueKind.True,
                Detail: source == "history" ? HasRecoverableCheckpoint(row) ? "extension.history.recoverable" : "extension.history.unrecoverable" : null,
                Data: row.Clone(), Cells: cells);
        }).ToArray();
    }

    private async Task<JsonElement> ExtensionServiceAsync(DiscoveredExtension extension, ExtensionPackageState original,
        string? boundSession, string method, JsonElement parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var manager = new ExtensionPackageManager(_platformPathsFactory()); var state = manager.Inspect(original.Id);
        if (!state.Enabled || state.Diagnostic is not null || state.ManifestSha256 != original.ManifestSha256
            || state.PackageSha256 != original.PackageSha256 || boundSession != _lastSessionId?.ToString())
            throw new UnauthorizedAccessException("extension_scope_revoked");
        if (parameters.TryGetProperty("sessionId", out var requestedSession) && requestedSession.GetString() != boundSession)
            throw new UnauthorizedAccessException("extension_session_scope_denied");
        var capability = method switch
        {
            "workspace.directory" or "workspace.search" or "workspace.file" or "git.status" or "git.diff" => "workspace.read",
            "history.timeline" or "activity.read" => "history.read",
            "sessions.agents" or "sessions.current" => "sessions.read",
            "config.get" => "configuration.read", "config.set" => "configuration.write",
            "presentation.notify" => "presentation.notify",
            _ => throw new UnauthorizedAccessException("extension_service_not_exposed"),
        };
        if (!state.GrantedCapabilities.Contains(capability, StringComparer.Ordinal)) throw new UnauthorizedAccessException("extension_capability_denied");
        if (method == "presentation.notify") return PublishExtensionNotification(state, boundSession, parameters);
        if (capability == "workspace.read" && extension.Manifest?.Requests?.Permissions?.Paths?.Read?.Contains("**", StringComparer.Ordinal) != true)
            throw new UnauthorizedAccessException("extension_workspace_ceiling_requires_explicit_read");
        if (method.StartsWith("config.", StringComparison.Ordinal)) return ExtensionConfiguration(extension, method, parameters);
        if (method == "activity.read") return ReadExtensionActivity(parameters, token);
        string query;
        if (method is "sessions.agents" or "sessions.current") query = method == "sessions.agents" ? "agents" : "sessionSidebar";
        else
        {
            var kind = method switch { "workspace.directory" => "directory", "workspace.search" => "search",
                "workspace.file" => "file", "git.status" => "git", "git.diff" => "diff", _ => "timeline" };
            string Read(string key) => parameters.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            var request = new WorkspacePanelRequest(kind, Read("path"), Read("search"));
            query = "workspace.panel:" + Uri.EscapeDataString(JsonSerializer.Serialize(request, WorkspacePanelJson.Default.WorkspacePanelRequest));
        }
        var result = Query(query, token) ?? throw new IOException("extension_service_unavailable");
        if (Encoding.UTF8.GetByteCount(result.Json) > ExtensionProtocol.MaximumFrameBytes / 2) throw new IOException("extension_service_result_limit");
        using var parsed = JsonDocument.Parse(result.Json);
        await Task.CompletedTask.ConfigureAwait(false); return parsed.RootElement.Clone();
    }

    private JsonElement ReadExtensionActivity(JsonElement parameters, CancellationToken token)
    {
        var after = parameters.TryGetProperty("afterSequence", out var supplied) ? supplied.GetInt64() : 0;
        var limit = parameters.TryGetProperty("limit", out var size) ? size.GetInt32() : 64;
        if (after < 0 || limit is < 1 or > 128) throw new ArgumentException("extension_activity_cursor_invalid");
        var panel = JsonSerializer.Deserialize(QueryWorkspacePanel("workspace.panel:" + Uri.EscapeDataString("{\"kind\":\"timeline\"}"), token).Json,
            WorkspacePanelJson.Default.WorkspacePanelResult)!;
        if (after > panel.Revision) throw new ArgumentException("extension_activity_cursor_future");
        var timeline = panel.Timeline ?? [];
        var remaining = timeline.Where(item => item.Sequence > after).ToArray();
        var selected = remaining.Take(limit).Select(item => new ExtensionActivityEvent(item.Sequence, item.Kind, item.Text,
            item.RunId, item.ExecutionId, item.Agent, item.Files, item.Activity, item.Checkpoint?.Id,
            item.Checkpoint is { Snapshot: not null, UnavailableReason: null }, item.ExtensionId, item.ToolId,
            item.CorrelationId, item.CausationId)).ToArray();
        while (true)
        {
            var page = new ExtensionActivityPage(panel.SessionId,
                remaining.Length > selected.Length ? selected[^1].Sequence : panel.Revision, panel.Revision,
                timeline.Count >= 300 && after < timeline[0].Sequence, timeline.Count > 0 ? timeline[0].Sequence : null, selected);
            var result = JsonSerializer.SerializeToElement(page, ExtensionJson.Default.ExtensionActivityPage);
            if (Encoding.UTF8.GetByteCount(result.GetRawText()) <= ExtensionProtocol.MaximumFrameBytes / 2) return result;
            if (selected.Length <= 1) throw new IOException("extension_activity_item_limit");
            selected = selected[..(selected.Length / 2)];
        }
    }

    private JsonElement ExtensionConfiguration(DiscoveredExtension extension, string method, JsonElement parameters)
    {
        var key = parameters.GetProperty("key").GetString() ?? "";
        if (extension.Manifest?.Configuration?.Contains(key, StringComparer.Ordinal) != true
            || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))) throw new UnauthorizedAccessException("extension_config_key_not_declared");
        // This store is extension-owned data, not permission/provider/workspace configuration.
        var scope = parameters.TryGetProperty("scope", out var requestedScope) ? requestedScope.GetString() : "workspace";
        var scopeRoot = scope switch
        {
            "user" => _platformPathsFactory().DataDirectory,
            "workspace" => OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), _workspaceRoot!),
            "session" when _lastSessionId is { } session => Path.Combine(OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), _workspaceRoot!), "sessions", session.ToString()),
            _ => throw new ArgumentException("extension_configuration_scope_invalid"),
        };
        var directory = Path.Combine(scopeRoot, "extension-config", extension.Manifest.Id!);
        if (method == "config.get" && !Directory.Exists(directory))
        { using var empty = JsonDocument.Parse("null"); return empty.RootElement.Clone(); }
        Directory.CreateDirectory(directory); ExtensionPackageManager.RejectLinks(directory);
        var file = Path.Combine(directory, key + ".json");
        if (method == "config.set")
        {
            var value = parameters.GetProperty("value").GetRawText();
            if (value.Length > 8192) throw new ArgumentException("extension_config_limit");
            AtomicPanelWrite(file, value);
            lock (_extensionViewCache)
                foreach (var cached in _extensionViewCache.Keys.Where(item => item.Contains("\0" + extension.Manifest.Id + "\0", StringComparison.Ordinal)).ToArray())
                    _extensionViewCache.Remove(cached);
        }
        using var document = JsonDocument.Parse(File.Exists(file) ? File.ReadAllText(file) : "null"); return document.RootElement.Clone();
    }

    private async Task<SupervisedExtensionProcess> ActivateExtensionAsync(DiscoveredExtension extension,
        ExtensionPackageState state, string? session, string activationEvent, CancellationToken token)
    {
        await _extensionActivation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var manifest = extension.Manifest!; var workspace = _workspaceRoot!;
            var key = state.Id + ":" + workspace + ":" + session;
            if (_extensionProcesses.TryGetValue(key, out var running) && running.State == "active") return running;
            if (state.IsolationPolicyVersion != 1 || manifest.Runtime is null
                || manifest.ActivationEvents?.Contains(activationEvent, StringComparer.Ordinal) != true)
                throw new UnauthorizedAccessException("extension_activation_not_authorized");
            var current = new ExtensionPackageManager(_platformPathsFactory()).Inspect(state.Id);
            if (!current.Enabled || current.Diagnostic is not null || current.IsolationPolicyVersion != 1) throw new UnauthorizedAccessException("extension_activation_revoked");
            var starts = _extensionStarts.GetValueOrDefault(key);
            if (starts >= 3) throw new IOException("extension_restart_limit");
            // A normal Session switch is not a crash/restart of the previous scope.
            _extensionStarts[key] = starts + 1;
            foreach (var prior in _extensionProcesses.Keys.Where(prior => prior != key && prior.StartsWith(state.Id + ":", StringComparison.Ordinal)).ToArray())
                if (_extensionProcesses.TryRemove(prior, out var previous)) await previous.DisposeAsync().ConfigureAwait(false);
            if (running is not null) await running.DisposeAsync().ConfigureAwait(false);
            var entry = ExtensionPackageManager.PackagePath(extension.Directory, manifest.Runtime.Command!);
            ExtensionPackageManager.RejectLinks(entry);
            var executable = entry;
            if (manifest.Runtime.Kind == "node")
            {
                try { executable = new OmniCore.Execution.SystemExecutableResolver().Resolve(
                    Environment.GetEnvironmentVariable("OMNICORE_EXTENSION_NODE") ?? "node", _workspaceRoot!).ResolvedPath; }
                catch (Exception error) when (error is ExecutableNotFoundException or ExecutableRequiresShellException)
                { throw new IOException("extension_node_not_available_from_user_configuration", error); }
                await SupervisedExtensionProcess.VerifyNodeLtsAsync(executable, token).ConfigureAwait(false);
            }
            var args = (manifest.Runtime.Kind == "node" ? new[] { entry } : Array.Empty<string>()).Concat(manifest.Runtime.Args ?? []).ToArray();
            SupervisedExtensionProcess process;
            try
            {
                using (new ExtensionPackageManager(_platformPathsFactory()).AcquireActivationLease(state))
                    process = new SupervisedExtensionProcess(executable, args, extension.Directory,
                    (method, parameters, cancel) => _workspaceRoot != workspace
                        ? Task.FromException<JsonElement>(new UnauthorizedAccessException("extension_workspace_changed"))
                        : ExtensionServiceAsync(extension, state, session, method, parameters, cancel),
                    () => _workspaceRoot == workspace && session == _lastSessionId?.ToString()
                        && new ExtensionPackageManager(_platformPathsFactory()).HasProcessGrant(state),
                    Path.Combine(_platformPathsFactory().DataDirectory, "extension-runtime"), token);
            }
            catch (IOException)
            { _extensionActivationFailures[state.Id] = "extension_isolation_start_failed_no_downgrade"; throw; }
            _extensionProcesses[key] = process;
            try { await process.InitializeAsync(new(ExtensionProtocol.Version, state.Id, state.GrantedCapabilities, session), token).ConfigureAwait(false); }
            catch
            {
                _extensionActivationFailures[state.Id] = "extension_activation_failed";
                await process.DisposeAsync().ConfigureAwait(false); throw;
            }
            _extensionActivationFailures.TryRemove(state.Id, out _);
            return process;
        }
        finally { _extensionActivation.Release(); }
    }

    private CommandAck ChangeExtensionPackage(WireEnvelope command, Dictionary<string, string> fields, bool trusted, CancellationToken token)
    {
        if (!trusted) return new(command.MessageId, "error", "trusted_user_action_required", RuntimeCommandOutcome.Rejected());
        try
        {
            var manager = new ExtensionPackageManager(_platformPathsFactory());
            var action = fields.GetValueOrDefault("action") ?? ""; var id = fields.GetValueOrDefault("extensionId") ?? "";
            if (action is "context-preview" or "context-approve" or "context-revoke")
            {
                ChangeExtensionContext(action, fields, token);
                return new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
            }
            if (action is "enable" or "disable" or "uninstall" or "update" or "rollback") InvalidateExtensionContext(id);
            if (action is "enable" or "uninstall" or "update" or "rollback")
            {
                var expected = manager.Inspect(id);
                if (fields.GetValueOrDefault("expectedManifestSha256") != expected.ManifestSha256
                    || fields.GetValueOrDefault("expectedPackageSha256") != expected.PackageSha256)
                    throw new InvalidOperationException("extension_consent_identity_changed");
            }
            if (action == "install") manager.Install(fields.GetValueOrDefault("source") ?? "", token);
            else if (action == "submit-view") SubmitExtensionForm(id, fields.GetValueOrDefault("submission") ?? "", token);
            else if (action is "update" or "rollback")
            {
                StopExtensionProcesses(id, token);
                if (action == "update") manager.Install(fields.GetValueOrDefault("source") ?? "", token, id,
                    fields.GetValueOrDefault("expectedManifestSha256"), fields.GetValueOrDefault("expectedPackageSha256"));
                else manager.Rollback(id, fields["expectedManifestSha256"], fields["expectedPackageSha256"], token);
            }
            else if (action is "disable" or "uninstall")
            {
                if (action == "disable") manager.Change(id, action);
                else manager.Change(id, "disable");
                StopExtensionProcesses(id, token);
                if (action == "uninstall") manager.Uninstall(id, fields.GetValueOrDefault("expectedManifestSha256"), fields.GetValueOrDefault("expectedPackageSha256"));
            }
            else if (action == "enable") manager.Change(id, action, fields.GetValueOrDefault("trustProcess") == "true",
                fields.GetValueOrDefault("expectedManifestSha256"), fields.GetValueOrDefault("expectedPackageSha256"));
            else if (action == "configure")
            {
                var extension = manager.Find(id);
                using var parameters = JsonDocument.Parse("{" + JsonObj.Field("key", fields.GetValueOrDefault("key") ?? "")
                    + ",\"value\":" + (fields.GetValueOrDefault("value") ?? "null") + "}");
                ExtensionConfiguration(extension, "config.set", parameters.RootElement);
            }
            else throw new ArgumentException("extension_action_invalid");
            return new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        { return new(command.MessageId, "error", "extension_lifecycle_failed:" + error.Message, RuntimeCommandOutcome.Rejected()); }
    }

    private void SubmitExtensionForm(string extensionId, string json, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw new ArgumentException("extension.form.invalid");
        var input = JsonSerializer.Deserialize(json, ExtensionJson.Default.ExtensionFormSubmission)
            ?? throw new ArgumentException("extension.form.invalid");
        if (input.ExtensionId != extensionId) throw new ArgumentException("extension.form.invalid");
        _ = ExtensionProtocol.DecodeRequest(ExtensionProtocol.EncodeRequest(new(input.ExtensionId, input.ViewId, input.Path, input.Search)));
        if (input.Scope is null || input.ExpectedRevision < 1) throw new ArgumentException("extension.form.stale");
        lock (_modeAuthorityMutationGate)
        lock (_extensionViewCache)
        {
            if (_workspaceRoot is null || input.Scope.WorkspaceId != WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(_workspaceRoot)).ToString()
                || input.Scope.SessionId != _lastSessionId?.ToString()) throw new InvalidOperationException("extension.form.stale");
            var key = string.Join('\0', _workspaceRoot, _lastSessionId?.ToString(), input.ExtensionId, input.ViewId, input.Path, input.Search);
            if (!_extensionViewCache.TryGetValue(key, out var cached) || cached.View.Scope != input.Scope
                || cached.View.Revision != input.ExpectedRevision || cached.View.State != "ready" || cached.View.Kind != "form")
                throw new InvalidOperationException("extension.form.stale");
            var action = cached.View.Actions.SingleOrDefault(action => action.Id == input.ActionId && action.ItemId is null);
            if (action is not { RequiresUserAuthority: true, ConfigurationKey: not null, Enabled: true }) throw new UnauthorizedAccessException("extension.form.action_unavailable");
            ExtensionProtocol.ValidateFormValues(cached.View, input.Values);
            var manager = new ExtensionPackageManager(_platformPathsFactory());
            var state = manager.Inspect(input.ExtensionId);
            if (state.ManifestSha256 != input.Scope.ManifestSha256 || state.PackageSha256 != input.Scope.PackageSha256)
                throw new InvalidOperationException("extension.form.stale");
            using var lease = manager.AcquireConfigurationLease(state);
            var extension = manager.Find(input.ExtensionId);
            using var parameters = JsonDocument.Parse("{" + JsonObj.Field("key", action.ConfigurationKey)
                + "," + JsonObj.Field("scope", action.ConfigurationScope) + ",\"value\":" + input.Values.GetRawText() + "}");
            ExtensionConfiguration(extension, "config.set", parameters.RootElement);
            _extensionViewCache.Remove(key); // Invalidate the consumed snapshot; its nonce cannot be replayed after refresh/eviction.
        }
    }

    private void StopExtensionProcesses(string id, CancellationToken token) => CompleteWorkspaceTask(async () =>
    {
        await _extensionActivation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var key in _extensionProcesses.Keys.Where(key => key.StartsWith(id + ":", StringComparison.Ordinal)).ToArray())
                if (_extensionProcesses.TryRemove(key, out var process)) await process.DisposeAsync().ConfigureAwait(false);
            foreach (var key in _extensionStarts.Keys.Where(key => key.StartsWith(id + ":", StringComparison.Ordinal)).ToArray())
                _extensionStarts.Remove(key);
        }
        finally { _extensionActivation.Release(); }
        lock (_extensionViewCache) _extensionViewCache.Clear();
    });
}
