namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

public interface ITuiLocalEngineSupervisor
{
    /// <summary>Reports ownership kept by the currently-live runtime; does not probe or start anything.</summary>
    IReadOnlyList<LocalEngineRuntimeStatus> Read(CancellationToken cancellationToken);

    /// <summary>Explicitly starts a managed engine or checks reachability of an attach endpoint.</summary>
    LocalEngineRuntimeActionResult EnsureReady(string providerId, CancellationToken cancellationToken);

    /// <summary>Stops only a managed process owned by this exact live supervisor instance.</summary>
    LocalEngineRuntimeActionResult Stop(string providerId, CancellationToken cancellationToken);
}

public sealed record LocalEngineRuntimeStatus(string ProviderId, string Mode,
    bool ManagedOwned, bool Running, string StateKey, bool Transitioning = false);

public sealed record LocalEngineRuntimeActionResult(bool Success, string StateKey);

/// <summary>Small UI-safe adapter over the supervisor owned by OmniCliRuntime.</summary>
public sealed class TuiLocalEngineSupervisor(LocalServerSupervisor supervisor, IPlatformPaths paths)
    : ITuiLocalEngineSupervisor
{
    public IReadOnlyList<LocalEngineRuntimeStatus> Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = OmniHost.LoadUserConfiguration(paths);
        return loaded.Registry.Providers().Where(provider => provider.LocalHost is not null)
            .OrderBy(provider => provider.Id, StringComparer.Ordinal)
            .Select(provider => Status(provider.Id, provider.LocalHost!.Mode == LocalHostMode.Managed
                ? "managed" : "attach"))
            .ToArray();
    }

    public LocalEngineRuntimeActionResult EnsureReady(string providerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Serialize explicit starts with configuration edits/deletion. Read remains lock-free.
            using var lease = ProviderConfigurationLease.Acquire(paths.ConfigDirectory);
            var provider = OmniHost.LoadUserConfiguration(paths).Registry.Provider(providerId);
            if (provider?.LocalHost is null) return Error("notLocal");
            _ = supervisor.Ensure(provider, provider.BaseUrl);
            return Ok(provider.LocalHost.Mode == LocalHostMode.Managed ? "managedRunning" : "attachReachable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (LocalServerUnavailableException exception)
        {
            return Error(exception.Reason switch
            {
                "unreachable" => "unreachable",
                "startFailed" => "startFailed",
                "crashed" => "crashed",
                "restartLimit" => "restartLimit",
                _ => "unavailable",
            });
        }
        catch (Exception) { return Error("unavailable"); }
    }

    public LocalEngineRuntimeActionResult Stop(string providerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var lease = ProviderConfigurationLease.Acquire(paths.ConfigDirectory);
            var provider = OmniHost.LoadUserConfiguration(paths).Registry.Provider(providerId);
            if (provider?.LocalHost is not { Mode: LocalHostMode.Managed }) return Error("notManaged");
            if (!supervisor.IsManagedOwned(providerId)) return Error("notOwned");
            cancellationToken.ThrowIfCancellationRequested();
            supervisor.Stop(providerId);
            return Ok("managedStopped");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Error("unavailable"); }
    }

    private LocalEngineRuntimeStatus Status(string providerId, string mode)
    {
        var owned = mode == "managed" && supervisor.IsManagedOwned(providerId);
        var transitioning = owned && supervisor.IsManagedStarting(providerId);
        var running = owned && supervisor.IsManagedRunning(providerId);
        var state = mode == "attach" ? "attachNotChecked"
            : transitioning ? "managedTransitioning" : running ? "managedRunning" : owned ? "managedStopped" : "managedUnowned";
        return new(providerId, mode, owned, running, "localEngines.runtime." + state, transitioning);
    }

    private static LocalEngineRuntimeActionResult Ok(string state) =>
        new(true, "localEngines.runtime." + state);

    private static LocalEngineRuntimeActionResult Error(string error) =>
        new(false, "localEngines.runtime.error." + error);
}
