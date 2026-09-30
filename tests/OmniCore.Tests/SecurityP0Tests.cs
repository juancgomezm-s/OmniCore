using System.Security.Cryptography.X509Certificates;
using OmniCore.Abstractions;
using Task = System.Threading.Tasks.Task;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Regresiones de los siete problemas de seguridad P0 de la auditoría (2026-09-28): hash del
/// CAS, autorización infalsificable, catálogo y modo de Explorer, combinación de permisos,
/// enlaces simbólicos y secretos, TLS, rutas de plataforma y cifrado de credenciales.
/// </summary>
public sealed class SecurityP0Tests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-p0-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ToolIntent Intent(string tool, EffectClass effect, string[]? reads = null, string[]? writes = null) =>
        new(ToolCallId.New(), new ToolId(tool), "{}", effect,
            new ResourceClaims(reads ?? [], writes ?? [], [], null, []), ToolRisk.Low, null);

    private static ValidatedToolCall Call(string tool, string argsJson) =>
        new(ToolCallId.New(), new ToolId(tool), "c-" + Guid.NewGuid().ToString("N").Substring(0, 6), argsJson);

    // ── #4 SHA-256 y CAS ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Sha256_matches_the_standard_vector()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Sha256.Hex("abc"));
    }

    [Fact]
    public void Sha256_hashes_full_utf8_not_the_low_byte_of_each_char()
    {
        // Con el bug antiguo, U+0100 ('Ā') y U+0000 colisionaban: solo se hasheaba el byte bajo.
        Assert.NotEqual(Sha256.Hex("\u0000"), Sha256.Hex("\u0100"));
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("ñ€漢"))),
            Sha256.Hex("ñ€漢"));
    }

    [Fact]
    public void Artifact_store_round_trips_non_latin_text_and_detects_tampering()
    {
        var dir = TempDir();
        var store = new FileArtifactStore(dir);
        var reference = store.PutText("año 漢字 €", "text/plain", ArtifactKind.ToolOutput, Sensitivity.Normal);
        Assert.Equal("año 漢字 €", store.GetText(reference.Hash));
        Assert.True(store.Verify(reference.Hash, reference.Size));

        var hex = reference.Hash.Value;
        var blob = Path.Combine(dir, "blobs", "sha256", hex[..2], hex.Substring(2, 2), hex);
        File.WriteAllText(blob, "manipulado");
        Assert.Throws<InvalidDataException>(() => store.GetText(reference.Hash));
        Assert.False(store.Verify(reference.Hash, reference.Size));
    }

    // ── #1 Explorer de solo lectura ──────────────────────────────────────────────────────

    [Fact]
    public void Explorer_catalog_never_exposes_filesystem_patch()
    {
        var names = OmniHost.CreateExplorerTools().Catalog().Definitions().Select(t => t.Name).ToArray();
        Assert.DoesNotContain("filesystem.patch", names);
        Assert.Contains("filesystem.read", names);
    }

    [Fact]
    public void Explorer_executor_denies_writes_even_if_a_mutating_tool_is_in_the_catalog()
    {
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), "original");
        var catalog = new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
            includeMutationTools: true).Catalog();
        var executor = OmniHost.CreateExplorerExecutor(catalog, ws);
        var version = FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes("original"));

        var stream = new EventStream(new InMemoryEventStore(), EventCodecs.Create(), SessionId.New());

        var outcome = executor.ExecuteTool(Call("filesystem.patch",
            "{\"path\":\"doc.txt\",\"expectedVersion\":\"" + version + "\",\"oldText\":\"original\",\"newText\":\"x\"}"),
            true, CancellationToken.None, stream);

        Assert.False(outcome.Succeeded, "Explorer usa la capa de modo PLAN: el patch es Deny. " + outcome.Summary);
        Assert.Equal("original", File.ReadAllText(Path.Combine(ws, "doc.txt")));
    }

    [Fact]
    public void Patch_prepare_is_pure_and_reconciliation_is_computed_only_after_authorization()
    {
        // INV-013: Prepare no lee el archivo (ni siquiera para los metadatos de reconciliación).
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), "a\nb\n");
        var version = FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes("a\nb\n"));
        var args = "{\"path\":\"doc.txt\",\"expectedVersion\":\"" + version + "\",\"oldText\":\"b\",\"newText\":\"c\"}";
        var tool = new FilesystemPatchTool(new PathBoundaryValidator());

        var prepared = Assert.IsType<Prepared>(tool.Prepare(Call("filesystem.patch", args),
            new ToolPreparationContext(ws, DateTimeOffset.UtcNow)));
        Assert.Null(prepared.Intent.Reconciliation);

        // Tras autorizar, el ToolCallStarted sí lleva los metadatos (calculados antes del efecto).
        var events = new List<DomainEventPayload>();
        var runtime = ToolRuntime.For(
            new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false,
                includeMutationTools: true).Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow),
            e => { events.Add(e); return VoidBox.Instance; });
        runtime.Run(Call("filesystem.patch", args), new ToolPreparationContext(ws, DateTimeOffset.UtcNow),
            new ToolExecutionContext(ws), false, CancellationToken.None);
        var started = Assert.Single(events.OfType<ToolCallStarted>());
        Assert.NotNull(started.ReconciliationJson);
        Assert.Contains(version, started.ReconciliationJson);
    }

    // ── #7 Combinación de permisos ───────────────────────────────────────────────────────

    [Fact]
    public void Patch_intent_is_a_write_denied_in_plan_and_allowed_in_act()
    {
        var patch = Intent("filesystem.patch", EffectClass.NonIdempotent, ["f.cs"], ["f.cs"]);
        var plan = new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Plan).Evaluate(patch);
        var act = new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Act).Evaluate(patch);
        Assert.Equal(PermissionDecision.Deny, plan.Final);
        Assert.Equal(PermissionDecision.Allow, act.Final);
    }

    [Fact]
    public void Layers_combine_by_minimum_so_ask_and_deny_is_deny()
    {
        var write = Intent("custom.write", EffectClass.NonIdempotent, writes: ["f.cs"]);
        var record = ScriptedPermissionPolicy.WithTool("custom.write", PermissionDecision.Ask)
            .WithModeDefaults(RunMode.Plan).Evaluate(write);
        Assert.Equal(PermissionDecision.Deny, record.Final);
    }

    [Fact]
    public void Unknown_effect_without_claims_fails_closed()
    {
        var opaque = Intent("mystery.tool", EffectClass.NonIdempotent);
        var plan = new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Plan).Evaluate(opaque);
        var act = new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Act).Evaluate(opaque);
        Assert.Equal(PermissionDecision.Deny, plan.Final);
        Assert.Equal(PermissionDecision.Ask, act.Final);
    }

    [Fact]
    public void Approval_never_lifts_a_deny()
    {
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Deny);
        var intent = Intent("fake.write", EffectClass.NonIdempotent, writes: ["f.txt"]);
        Assert.Throws<PermissionDeniedException>(() => policy.AuthorizeApproved(intent, null));
    }

    [Fact]
    public void Approval_lifts_an_ask()
    {
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Ask);
        var intent = Intent("fake.write", EffectClass.NonIdempotent, writes: ["f.txt"]);
        var authorized = policy.AuthorizeApproved(intent, null);
        Assert.Equal(PermissionDecision.Allow, authorized.Decision.Final);
    }

    // ── #7 Rutas de secretos ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("config/appsettings.Production.json")]
    [InlineData("APPSETTINGS.PRODUCTION.JSON")]
    [InlineData(".ssh/config")]
    [InlineData("home/user/.ssh/known_hosts")]
    [InlineData("deploy/server.PEM")]
    public void Secret_paths_match_regardless_of_case_and_depth(string path)
    {
        Assert.True(new RedactionPolicy().IsSecretPath(path), path);
    }

    [Fact]
    public void Read_of_a_link_pointing_to_env_is_denied()
    {
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, ".env"), "API_KEY=supersecreto");
        if (!TryCreateFileLink(Path.Combine(ws, "notes.txt"), Path.Combine(ws, ".env")))
        {
            Assert.Skip("El entorno no permite crear enlaces simbólicos (modo desarrollador/permisos).");
        }

        var executor = ScriptedToolExecutor.WithWorkspace(
            new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false).Catalog(),
            new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Act), ws);
        var outcome = executor.ExecuteToolWithoutJournal(Call("filesystem.read", "{\"path\":\"notes.txt\"}"), false,
            CancellationToken.None);

        Assert.False(outcome.Succeeded, "Un enlace hacia .env no se lee. " + outcome.Summary);
        Assert.DoesNotContain("supersecreto", outcome.Summary ?? "");
    }

    [Fact]
    public void Symlink_chain_escaping_the_workspace_is_outside()
    {
        var root = TempDir();
        var ws = Path.Combine(root, "ws");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "target.txt"), "fuera");

        // ws/a → ws/b → outside/target.txt
        if (!TryCreateFileLink(Path.Combine(ws, "b"), Path.Combine(outside, "target.txt"))
            || !TryCreateFileLink(Path.Combine(ws, "a"), Path.Combine(ws, "b")))
        {
            Assert.Skip("El entorno no permite crear enlaces simbólicos (modo desarrollador/permisos).");
        }

        var boundary = new PathBoundaryValidator();
        Assert.False(boundary.IsWithin(Path.Combine(ws, "a"), ws));
        Assert.False(boundary.IsWithin(Path.Combine(ws, "b"), ws));
    }

    [Fact]
    public void Symlink_cycle_is_unresolvable_and_outside()
    {
        var ws = TempDir();
        if (!TryCreateFileLink(Path.Combine(ws, "x"), Path.Combine(ws, "y"))
            || !TryCreateFileLink(Path.Combine(ws, "y"), Path.Combine(ws, "x")))
        {
            Assert.Skip("El entorno no permite crear enlaces simbólicos (modo desarrollador/permisos).");
        }

        var boundary = new PathBoundaryValidator();
        Assert.Null(boundary.ResolvePhysical(Path.Combine(ws, "x")));
        Assert.False(boundary.IsWithin(Path.Combine(ws, "x"), ws));
    }

    [Fact]
    public void Directory_link_chain_escaping_the_workspace_is_outside()
    {
        var root = TempDir();
        var ws = Path.Combine(root, "ws");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "target.txt"), "fuera");

        // ws/j1 → ws/j2 → outside (junctions en Windows, symlinks en Unix)
        if (!TryCreateDirectoryLink(Path.Combine(ws, "j2"), outside)
            || !TryCreateDirectoryLink(Path.Combine(ws, "j1"), Path.Combine(ws, "j2")))
        {
            Assert.Skip("El entorno no permite crear enlaces de directorio.");
        }

        var boundary = new PathBoundaryValidator();
        Assert.False(boundary.IsWithin(Path.Combine(ws, "j1", "target.txt"), ws));
        Assert.Equal(Path.GetFullPath(Path.Combine(outside, "target.txt")).Replace('\\', '/'),
            boundary.ResolvePhysical(Path.Combine(ws, "j1", "target.txt")), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void Directory_link_cycle_is_unresolvable_and_outside()
    {
        var ws = TempDir();
        var a = Path.Combine(ws, "a");
        var b = Path.Combine(ws, "b");
        Directory.CreateDirectory(b);
        if (!TryCreateDirectoryLink(a, b))
        {
            Assert.Skip("El entorno no permite crear enlaces de directorio.");
        }

        // Se reemplaza b por un enlace hacia a: a → b → a.
        Directory.Delete(b);
        if (!TryCreateDirectoryLink(b, a))
        {
            Assert.Skip("El entorno no permite crear enlaces de directorio.");
        }

        var boundary = new PathBoundaryValidator();
        Assert.Null(boundary.ResolvePhysical(Path.Combine(a, "f.txt")));
        Assert.False(boundary.IsWithin(Path.Combine(a, "f.txt"), ws));
    }

    [Fact]
    public void Read_through_a_directory_link_into_ssh_is_denied()
    {
        var ws = TempDir();
        var ssh = Path.Combine(ws, ".ssh");
        Directory.CreateDirectory(ssh);
        File.WriteAllText(Path.Combine(ssh, "config"), "IdentityFile supersecreto");
        if (!TryCreateDirectoryLink(Path.Combine(ws, "docs"), ssh))
        {
            Assert.Skip("El entorno no permite crear enlaces de directorio.");
        }

        var executor = ScriptedToolExecutor.WithWorkspace(
            new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false).Catalog(),
            new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Act), ws);
        var outcome = executor.ExecuteToolWithoutJournal(Call("filesystem.read", "{\"path\":\"docs/config\"}"), false,
            CancellationToken.None);

        Assert.False(outcome.Succeeded, "docs/ apunta a .ssh/: la lectura se niega. " + outcome.Summary);
        Assert.DoesNotContain("supersecreto", outcome.Summary ?? "");
    }

    /// <summary>
    /// Enlace de directorio: symlink si el entorno lo permite y, en Windows sin privilegios, un
    /// junction (mklink /J no requiere modo desarrollador).
    /// </summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // ── #3 TLS ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("https://localhost:8443")]
    [InlineData("https://10.0.0.5/v1")]
    [InlineData("https://172.16.0.1")]
    [InlineData("https://172.31.255.255:9000")]
    [InlineData("https://192.168.1.10:8080/v1")]
    [InlineData("https://100.64.0.1")]
    [InlineData("https://[::1]:8080")]
    [InlineData("https://[fd12:3456::1]/v1")]
    public void Private_ip_literals_are_private(string url)
    {
        Assert.True(OmniHost.IsPrivateHost(url), url);
    }

    [Theory]
    [InlineData("https://10.evil.com/v1")]
    [InlineData("https://192.168.1.1.nip.io")]
    [InlineData("https://127.0.0.1.attacker.net")]
    [InlineData("https://172.32.0.1")]
    [InlineData("https://172.3.0.1")]
    [InlineData("https://fd00.example.com")]
    [InlineData("https://printer.local")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://8.8.8.8")]
    [InlineData("no es una url")]
    public void Names_and_public_hosts_are_never_private(string url)
    {
        Assert.False(OmniHost.IsPrivateHost(url), url);
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSignedCa(string cn)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=" + cn, key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public void Pinned_tls_accepts_only_the_configured_root()
    {
        var pinned = SelfSignedCa("192.168.1.104");
        var other = SelfSignedCa("192.168.1.104");
        const System.Net.Security.SslPolicyErrors untrusted = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;

        Assert.True(OmniHost.ValidateAgainstTrustedRoot(pinned, untrusted, pinned));
        Assert.False(OmniHost.ValidateAgainstTrustedRoot(other, untrusted, pinned),
            "Un certificado con el mismo nombre pero otra clave se rechaza");
        Assert.False(OmniHost.ValidateAgainstTrustedRoot(pinned,
            untrusted | System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, pinned),
            "El nombre del host debe coincidir aunque la raíz sea la fijada");
        Assert.False(OmniHost.ValidateAgainstTrustedRoot(null, untrusted, pinned));
    }

    [Fact]
    public async Task Pinned_tls_handler_works_end_to_end_against_a_real_tls_server()
    {
        // Servidor TLS real en 127.0.0.1 con un certificado autofirmado (CA) cuyo SAN es solo la IP,
        // como el del servidor local del usuario.
        X509Certificate2 serverCert;
        using (var key = System.Security.Cryptography.RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=127.0.0.1", key,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            serverCert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        }

        var dir = TempDir();
        var pinnedPath = Path.Combine(dir, "server.pem");
        File.WriteAllText(pinnedPath, serverCert.ExportCertificatePem());
        var otherPath = Path.Combine(dir, "other.pem");
        File.WriteAllText(otherPath, SelfSignedCa("127.0.0.1").ExportCertificatePem());

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var server = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                    or System.Net.Sockets.SocketException)
                {
                    return;
                }

                using (client)
                using (var ssl = new System.Net.Security.SslStream(client.GetStream()))
                {
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(serverCert);
                        // Se lee hasta el fin de las cabeceras de la petición (GET sin cuerpo).
                        var buffer = new byte[4096];
                        var received = new System.Text.StringBuilder();
                        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            var read = await ssl.ReadAsync(buffer, cts.Token);
                            if (read == 0)
                            {
                                break;
                            }

                            received.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                        }

                        await ssl.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), cts.Token);
                    }
                    catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
                    {
                        // El cliente rechazó el certificado: es lo esperado en los casos negativos.
                    }
                }
            }
        }, TestContext.Current.CancellationToken);

        async Task<bool> Get(string url, string pinned)
        {
            using var http = new HttpClient(OmniHost.CreateTlsHandler(url, pinned));
            try
            {
                return (await http.GetStringAsync(url, cts.Token)) == "ok";
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        try
        {
            Assert.True(await Get("https://127.0.0.1:" + port + "/v1/models", pinnedPath),
                "Con el certificado fijado la conexión valida");
            Assert.False(await Get("https://127.0.0.1:" + port + "/v1/models", otherPath),
                "Fijado a otra raíz, el servidor se rechaza aunque la IP sea privada");
            Assert.False(await Get("https://localhost:" + port + "/v1/models", pinnedPath),
                "El nombre debe coincidir con el SAN (solo 127.0.0.1)");
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await server;
        }
    }

    [Fact]
    public void Trusted_certificate_loads_from_pem_and_der_and_comes_from_providers_yaml()
    {
        var dir = TempDir();
        var ca = SelfSignedCa("localhost");
        var pem = Path.Combine(dir, "ca.pem");
        var der = Path.Combine(dir, "ca.crt");
        File.WriteAllText(pem, ca.ExportCertificatePem());
        File.WriteAllBytes(der, ca.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
        Assert.Equal(ca.Thumbprint, OmniHost.LoadTrustedCertificate(pem).Thumbprint);
        Assert.Equal(ca.Thumbprint, OmniHost.LoadTrustedCertificate(der).Thumbprint);

        var registry = new ConfigLoader().BuildRegistry(
            "providers:\n  lan: { baseUrl: https://192.168.1.104:8080/v1, auth: none, caCertificate: "
                + pem.Replace('\\', '/') + " }\n",
            null);
        Assert.Equal(pem.Replace('\\', '/'), registry.Provider("lan")!.TrustedCertificatePath);
        Assert.StartsWith("fijado a", OmniHost.DescribeTls("https://192.168.1.104:8080/v1", pem));
        Assert.StartsWith("AVISO", OmniHost.DescribeTls("https://192.168.1.104:8080/v1", null));
    }

    // ── #2 / #6 Rutas de plataforma y WorkspaceId ────────────────────────────────────────

    [Fact]
    public void WorkspaceId_is_a_short_hash_without_the_path()
    {
        var id = WorkspaceId.Of("C:/Users/alguien/repos/proyecto-secreto").ToString();
        Assert.Matches("^[0-9a-f]{16}$", id);
        Assert.DoesNotContain("secreto", id);
        Assert.Equal(id, WorkspaceId.Of("C:\\Users\\alguien\\repos\\proyecto-secreto\\").ToString());
        Assert.NotEqual(id, WorkspaceId.Of("C:/Users/alguien/repos/otro").ToString());
    }

    [Fact]
    public void Workspace_data_lives_under_the_platform_data_directory()
    {
        var data = TempDir();
        var paths = OmniHost.CreatePlatformPaths(data);
        var repo = TempDir();
        var wsDir = OmniHost.WorkspaceDataDirectory(paths, repo);

        Assert.StartsWith(Path.Combine(data, "workspaces"), wsDir, StringComparison.Ordinal);
        Assert.False(wsDir.StartsWith(repo, StringComparison.OrdinalIgnoreCase), "Los datos no van al repo");
        Assert.Throws<ArgumentException>(() => paths.WorkspaceDirectory("../fuera"));
    }

    [Fact]
    public void Model_registry_comes_from_the_user_config_directory()
    {
        var paths = OmniHost.CreatePlatformPaths(TempDir());
        Assert.NotEqual(Path.GetFullPath("."), Path.GetFullPath(paths.ConfigDirectory));
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"),
            "providers:\n  mine: { baseUrl: http://127.0.0.1:9999, auth: none }\n");
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), "models:\n  mi-modelo: { provider: mine }\n");

        var registry = OmniHost.LoadUserModelRegistry(paths);

        Assert.NotNull(registry.Provider("mine"));
        Assert.NotNull(registry.Model("mi-modelo"));
    }

    [Fact]
    public void Tests_never_touch_the_real_user_directories()
    {
        // OMNICORE_DATA_DIR/OMNICORE_CONFIG_DIR (TestEnvironment) cambian las rutas por defecto.
        var paths = new DefaultPlatformPaths();
        Assert.Equal(TestEnvironment.DataDirectory, paths.DataDirectory);
        var realLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var realRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.False(paths.DataDirectory.StartsWith(Path.Combine(realLocal, "OmniCore"), StringComparison.OrdinalIgnoreCase));
        Assert.False(paths.ConfigDirectory.StartsWith(Path.Combine(realRoaming, "OmniCore"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Api_key_from_environment_is_stored_and_reused_later()
    {
        var store = new FileCredentialStore(Path.Combine(TempDir(), "credentials.ini"));
        Assert.Null(OmniHost.ResolveApiKey(store, "local-qwen-key", null, CancellationToken.None));

        Assert.Equal("k-123", OmniHost.ResolveApiKey(store, "local-qwen-key", "k-123", CancellationToken.None));
        // Sin variable de entorno, la siguiente ejecución la toma del almacén cifrado.
        Assert.Equal("k-123", OmniHost.ResolveApiKey(store, "local-qwen-key", null, CancellationToken.None));
        Assert.Equal("k-123", OmniHost.ResolveApiKey(store, "local-qwen-key", "", CancellationToken.None));
    }

    // ── #6 Credenciales cifradas ─────────────────────────────────────────────────────────

    [Fact]
    public void Credentials_are_encrypted_and_round_trip_unicode()
    {
        var path = Path.Combine(TempDir(), "credentials.ini");
        var store = new FileCredentialStore(path);
        store.Save("k", "clave-ñ-€-漢", CancellationToken.None);

        Assert.Equal("clave-ñ-€-漢", store.Load("k", CancellationToken.None));
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("clave", raw);
        Assert.StartsWith("k=enc:v2:", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Tampered_or_legacy_credentials_are_treated_as_missing()
    {
        var path = Path.Combine(TempDir(), "credentials.ini");
        var store = new FileCredentialStore(path);
        store.Save("k", "valor", CancellationToken.None);
        var raw = File.ReadAllText(path);
        var tampered = raw.Substring(0, raw.Length - 4) + (raw.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");
        File.WriteAllText(path, tampered + "\nlegacy=enc:c2FsdDp2YWxvcg==\nplain=texto-plano");

        Assert.Null(store.Load("k", CancellationToken.None));
        Assert.Null(store.Load("legacy", CancellationToken.None));
        Assert.Null(store.Load("plain", CancellationToken.None));
    }
}
