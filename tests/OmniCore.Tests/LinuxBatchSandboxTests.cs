using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OmniCore.Execution;
using OmniCore.Sandbox;

namespace OmniCore.Tests;

/// <summary>Real Linux namespaces and process I/O; no mocked isolation or provider requests.</summary>
public sealed class LinuxBatchSandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-linux-batch-" + Guid.NewGuid().ToString("N"));

    private string Workspace()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Requires the Linux kernel and bubblewrap.");
        Assert.True(File.Exists("/usr/bin/bwrap"), "Linux acceptance requires bubblewrap.");
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static SandboxLaunchSpec Spec(string workspace, string executable, params string[] arguments) =>
        new(SandboxStrength.Strong, executable, arguments, workspace, new Dictionary<string, string>(),
            [new(workspace, SandboxPathAccess.Read | SandboxPathAccess.Write)], new(SandboxNetworkMode.Deny, []),
            TimeSpan.FromSeconds(30));

    private static async Task<SandboxProcessOutput> Run(SandboxLaunchSpec spec, CancellationToken cancellationToken)
    {
        var launcher = new PlatformSandboxProcessLauncher(new SystemProcessRuntime());
        Assert.Equal(StrongSandboxPreflightResult.Available, launcher.CheckStrongAvailability(spec));
        await using var process = await launcher.StartAsync(spec, cancellationToken);
        return await process.WaitForExitAsync(cancellationToken);
    }

    [Fact]
    public async Task Batch_captures_both_streams_exact_input_eof_and_real_exit_code()
    {
        var workspace = Workspace();
        var result = await Run(Spec(workspace, "/bin/sh", "-c", "cat; printf error >&2; exit 7")
            with { InitialStandardInput = "input-without-added-newline" }, TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("input-without-added-newline", Assert.Single(result.Chunks,
            chunk => chunk.Stream == SandboxOutputStream.StandardOutput).Text);
        Assert.Equal("error", Assert.Single(result.Chunks,
            chunk => chunk.Stream == SandboxOutputStream.StandardError).Text);
    }

    [Fact]
    public async Task A_short_lived_calling_thread_does_not_kill_the_owned_sandbox()
    {
        var workspace = Workspace();
        var (thread, started) = StartFromTemporaryThread(Spec(workspace, "/bin/sh", "-c",
            "printf 'ready\\n'; read signal; printf 'complete\\n'") with { Duplex = true },
            TestContext.Current.CancellationToken);
        var (control, reader, ready) = await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await using var owned = control;
        using var output = reader;
        Assert.Equal("ready", ready);
        Assert.True(await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken));

        await owned.StandardInput.WriteAsync("go\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await owned.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal("complete", await output.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await owned.WaitForExitCodeAsync(TestContext.Current.CancellationToken));
    }

    private static (Thread Thread, Task<(ISandboxPersistentProcessControl Control, StreamReader Reader, string? Ready)> Started)
        StartFromTemporaryThread(SandboxLaunchSpec spec, CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource<(ISandboxPersistentProcessControl, StreamReader, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var control = (ISandboxPersistentProcessControl)new LinuxBubblewrapProcessLauncher()
                    .StartAsync(spec, cancellationToken).AsTask().GetAwaiter().GetResult();
                var reader = new StreamReader(control.StandardOutput, leaveOpen: true);
                started.SetResult((control, reader, reader.ReadLine()));
            }
            catch (Exception error) { started.SetException(error); }
        }) { IsBackground = true };
        thread.Start();
        return (thread, started.Task);
    }

    [Fact]
    public async Task Terminal_disposal_preserves_exit_and_a_later_stop_is_idempotent()
    {
        var workspace = Workspace();
        var control = (ISandboxPersistentProcessControl)await new LinuxBubblewrapProcessLauncher()
            .StartAsync(Spec(workspace, "/bin/sh", "-c", "exit 35") with { Duplex = true },
                TestContext.Current.CancellationToken);
        await using var owned = control;
        Assert.Equal(35, await owned.WaitForExitCodeAsync(TestContext.Current.CancellationToken));
        await owned.DisposeAsync();
        await owned.TerminateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(35, await owned.WaitForExitCodeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Workspace_writes_are_visible_but_ungranted_parent_data_is_absent()
    {
        var workspace = Workspace();
        var outside = Path.Combine(_root, "outside-private.txt");
        File.WriteAllText(outside, "must-not-be-visible");
        var result = await Run(Spec(workspace, "/bin/sh", "-c",
            "printf accepted > result.txt; test ! -e \"$1\"", "probe", outside), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("accepted", File.ReadAllText(Path.Combine(workspace, "result.txt")));
        Assert.Equal("must-not-be-visible", File.ReadAllText(outside));
    }

    [Fact]
    public async Task Read_only_mount_rejects_writes_without_damaging_the_original()
    {
        var workspace = Workspace();
        var file = Path.Combine(workspace, "original.txt");
        File.WriteAllText(file, "original");
        var spec = Spec(workspace, "/bin/sh", "-c", "printf changed > original.txt")
            with { AllowedPaths = [new(workspace, SandboxPathAccess.Read)] };

        var result = await Run(spec, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("original", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_user_tool_does_not_expose_private_siblings_of_its_executable()
    {
        var workspace = Workspace();
        var toolchain = Path.Combine(_root, "toolchain");
        Directory.CreateDirectory(toolchain);
        var executable = Path.Combine(toolchain, "fixture-tool");
        var source = new FileInfo("/bin/sh").ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? "/bin/sh";
        File.Copy(source, executable);
        var sibling = Path.Combine(toolchain, "private.key");
        File.WriteAllText(sibling, "synthetic-private-sibling");

        var result = await Run(Spec(workspace, executable, "-c", "test ! -e \"$1\"", "probe", sibling),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("synthetic-private-sibling", File.ReadAllText(sibling));
    }

    [Theory]
    [InlineData(SandboxNetworkMode.Deny, false)]
    [InlineData(SandboxNetworkMode.AllowAll, true)]
    public async Task Network_policy_is_enforced_by_the_namespace(SandboxNetworkMode mode, bool allowed)
    {
        var workspace = Workspace();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var result = await Run(Spec(workspace, "/usr/bin/python3", "-c",
            $"import socket; s=socket.create_connection(('127.0.0.1',{port}),0.5); s.close()")
            with { Network = new(mode, []) }, TestContext.Current.CancellationToken);

        Assert.Equal(allowed, result.ExitCode == 0);
    }

    [Fact]
    public async Task Timeout_terminates_the_namespace_before_a_delayed_child_can_write()
    {
        var workspace = Workspace();
        var result = await Run(Spec(workspace, "/bin/sh", "-c", "(sleep 2; echo escaped > late.txt) & wait")
            with { Timeout = TimeSpan.FromMilliseconds(250) }, TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(workspace, "late.txt")));
    }

    [Fact]
    public async Task Process_environment_has_private_home_and_only_explicit_extra_variables()
    {
        var workspace = Workspace();
        var result = await Run(Spec(workspace, "/bin/sh", "-c",
            "printf '%s|%s|%s' \"$HOME\" \"${DBUS_SESSION_BUS_ADDRESS-unset}\" \"$VISIBLE\"")
            with { Environment = new Dictionary<string, string> { ["VISIBLE"] = "explicit" } },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/tmp/omnicore-home|unset|explicit", Assert.Single(result.Chunks).Text);
    }

    [Fact]
    public async Task Dotnet_sdk_builds_a_real_project_with_private_home_and_no_network()
    {
        var workspace = Workspace();
        File.WriteAllText(Path.Combine(workspace, "Probe.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>"
            + "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(workspace, "Program.cs"), "System.Console.WriteLine(\"built-inside-strong\");");
        var source = Path.Combine(workspace, "empty-package-source");
        Directory.CreateDirectory(source);
        // MTP may start this suite through its apphost; that executable is not the SDK.
        // Resolve the actual SDK command instead of inheriting the test runner's host.
        var result = await Run(Spec(workspace, "dotnet", "build", "Probe.csproj",
            "-p:RestoreSources=" + source, "-p:NuGetAudit=false", "-v:q")
            with { Timeout = TimeSpan.FromSeconds(90) }, TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, $"ExitCode={result.ExitCode}; TimedOut={result.TimedOut}\n"
            + string.Join("\n", result.Chunks.Select(chunk => chunk.Text)));
        Assert.False(result.TimedOut);
        Assert.True(File.Exists(Path.Combine(workspace, "bin", "Debug", "net10.0", "Probe.dll")));
    }

    [Fact]
    public async Task Native_bootstrap_applies_seccomp_no_new_privileges_and_resource_limits()
    {
        var workspace = Workspace();
        var result = await Run(Spec(workspace, "/usr/bin/python3", "-c",
            "import ctypes,json,resource; c=ctypes.CDLL(None,use_errno=True); p=c.ptrace(0,0,0,0); "
            + "print(json.dumps({'ptrace':p,'errno':ctypes.get_errno(),'status':open('/proc/self/status').read(),"
            + "'files':resource.getrlimit(resource.RLIMIT_NOFILE)[0],"
            + "'processes':resource.getrlimit(resource.RLIMIT_NPROC)[0],'core':resource.getrlimit(resource.RLIMIT_CORE)[0]}))"),
            TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, string.Join("\n", result.Chunks.Select(chunk => chunk.Text)));
        using var json = JsonDocument.Parse(Assert.Single(result.Chunks, chunk => chunk.Stream == SandboxOutputStream.StandardOutput).Text);
        Assert.Equal(-1, json.RootElement.GetProperty("ptrace").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("errno").GetInt32());
        Assert.Contains("NoNewPrivs:\t1", json.RootElement.GetProperty("status").GetString());
        Assert.Contains("Seccomp:\t2", json.RootElement.GetProperty("status").GetString());
        Assert.InRange(json.RootElement.GetProperty("files").GetInt64(), 1, 4096);
        Assert.InRange(json.RootElement.GetProperty("processes").GetInt64(), 1, 1024);
        Assert.Equal(0, json.RootElement.GetProperty("core").GetInt64());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
