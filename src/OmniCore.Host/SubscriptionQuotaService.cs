using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Protocol;

namespace OmniCore.Host;

/// <summary>Uses the same official CLI surfaces as OmniCoder's SubscriptionUsageService. Never copies login tokens.</summary>
public sealed class SubscriptionQuotaService(string? codexExecutable = null, string? claudeExecutable = null)
{
    public async Task<ProviderQuotaSnapshot> QueryAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (provider is not ("chatgpt" or "claude")) return Missing(provider, "unsupported", "No subscription quota adapter for this provider.");
        var source = provider == "chatgpt" ? "Codex app-server:account/rateLimits/read" : "Claude Code:/usage";
        var executable = Resolve(provider == "chatgpt" ? codexExecutable : claudeExecutable, provider);
        if (executable is null) return Missing(provider, source, "Official CLI executable not found.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetTempPath(), UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(provider == "chatgpt" ? "app-server" : "--ax-screen-reader");
        using var process = new Process { StartInfo = start };
        Task<string>? error = null;
        try
        {
            if (!process.Start()) return Missing(provider, source, "Official CLI did not start.");
            error = ReadBounded(process.StandardError, timeout.Token);
            if (provider != "chatgpt")
            {
                await process.StandardInput.WriteLineAsync("/usage".AsMemory(), timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                var output = await ReadBounded(process.StandardOutput, timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? SubscriptionQuotaParser.Claude(output, DateTimeOffset.UtcNow)
                    : Missing(provider, source, "Claude CLI rejected /usage; no model prompt was submitted.");
            }
            async Task<JsonElement> Rpc(int id, string method, string parameters)
            {
                await process.StandardInput.WriteLineAsync(("{\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + parameters + "}").AsMemory(), timeout.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                var length = 0;
                for (var count = 0; count < 256; count++)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                        ?? throw new IOException("CLI ended before RPC response.");
                    if ((length += line.Length) > 65536) throw new IOException("RPC response exceeds bound.");
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed) || parsed != id) continue;
                    if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("RPC rejected.");
                    return root.GetProperty("result").Clone();
                }
                throw new IOException("RPC response not found.");
            }
            await Rpc(1, "initialize", "{\"clientInfo\":{\"name\":\"omnicore\",\"version\":\"0.1.0\"},\"capabilities\":{}}").ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), timeout.Token).ConfigureAwait(false);
            var account = await Rpc(2, "account/read", "{\"refreshToken\":false}").ConfigureAwait(false);
            var limits = await Rpc(3, "account/rateLimits/read", "{}").ConfigureAwait(false);
            // Stable account fingerprint, never an email, credential, or OAuth claim dump.
            var accountId = account.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(email.GetString())
                ? "account-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.GetString()!)))[..16].ToLowerInvariant() : null;
            return SubscriptionQuotaParser.Codex(limits, accountId, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Missing(provider, source, "Quota query timed out."); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return Missing(provider, source, "Quota query unavailable: " + ex.GetType().Name); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            if (error is not null) { try { _ = await error.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { } }
        }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (text.Length + read > 65536) throw new IOException("CLI output exceeds bound.");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
    private static string? Resolve(string? explicitPath, string provider)
    {
        var name = provider == "chatgpt" ? "codex.exe" : "claude.exe";
        var candidates = new[] { explicitPath, Environment.GetEnvironmentVariable(provider == "chatgpt" ? "OMNICODER_CODEX_CLI" : "OMNICODER_CLAUDE_CLI"),
            Path.Combine(AppContext.BaseDirectory, provider == "chatgpt" ? "codex" : "claude", name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"),
            provider == "claude" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name) : null }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => p.Length > 0).Select(p => Path.Combine(p, name)));
        return candidates.Where(p => !string.IsNullOrWhiteSpace(p) && (provider == "chatgpt" || !p!.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(p => File.Exists(p));
    }
    private static ProviderQuotaSnapshot Missing(string provider, string source, string reason) =>
        new(provider, null, source, DateTimeOffset.UtcNow, MetricAvailability.Unknown, [], [], reason);
}

public static class SubscriptionQuotaParser
{
    public static ProviderQuotaSnapshot Codex(JsonElement result, string? account, DateTimeOffset date)
    {
        var windows = new List<ProviderUsageWindow>();
        var credits = new List<CreditMeasurement>();
        void Read(JsonElement limits, string? limitId)
        {
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object ||
                    !w.TryGetProperty("usedPercent", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0 || used > 100) continue;
                var duration = w.TryGetProperty("windowDurationMins", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var minutes) && minutes > 0 ? minutes : (int?)null;
                DateTimeOffset? reset = null;
                if (w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var epoch) && epoch >= 0 && epoch <= 253402300799) reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
                windows.Add(new((limitId ?? "default") + ":" + name, limitId, duration,
                    new(MetricAvailability.Reported, used, "Codex app-server", date), new(MetricAvailability.Reported, 100 - used, "100-provider-usedPercent", date), reset, null));
            }
            if (limits.TryGetProperty("credits", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                decimal? balance = c.TryGetProperty("balance", out var b) && decimal.TryParse(b.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : null;
                credits.Add(new(CreditScope.AccountBalance, new(balance is null ? MetricAvailability.Unknown : MetricAvailability.Reported,
                    balance, "Codex:credits.balance", date), "credits", null, null));
            }
        }
        if (result.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object && map.EnumerateObject().Any())
        { foreach (var pair in map.EnumerateObject()) if (pair.Value.ValueKind == JsonValueKind.Object) Read(pair.Value, pair.Name); }
        else if (result.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object) Read(legacy, null);
        return new("chatgpt", account, "Codex app-server:account/rateLimits/read", date,
            windows.Count > 0 ? MetricAvailability.Reported : MetricAvailability.Unknown, windows, credits,
            windows.Count > 0 ? null : "Account response did not publish usage windows.");
    }
    public static ProviderQuotaSnapshot Claude(string output, DateTimeOffset date)
    {
        var windows = new List<ProviderUsageWindow>();
        foreach (Match match in Regex.Matches(output, @"(?im)^(?<label>Current session|Current week(?: \(all models\))?):\s*(?<used>\d+(?:\.\d+)?)%\s+used\s*[·\-]\s*resets\s+(?<reset>.+)$"))
        {
            if (!double.TryParse(match.Groups["used"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var used) || used > 100) continue;
            var label = match.Groups["label"].Value;
            var resetText = match.Groups["reset"].Value.Trim();
            DateTimeOffset? reset = DateTimeOffset.TryParseExact(resetText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            windows.Add(new(label, null, null, new(MetricAvailability.Reported, used, "Claude Code:/usage", date),
                new(MetricAvailability.Reported, 100 - used, "100-provider-usedPercent", date), reset, resetText));
        }
        return new("claude", null, "Claude Code:/usage", date, windows.Count > 0 ? MetricAvailability.Reported : MetricAvailability.Unknown,
            windows, [], windows.Count > 0 ? "CLI did not publish account identity or numeric window duration; reset text is preserved." : "CLI did not publish usage windows.");
    }
}
