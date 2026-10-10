using OmniCore.Abstractions;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests de la persistencia de credenciales OAuth (§6 del plan, grupo 9).
/// Almacén en memoria + disco temporal para la metadata.
/// </summary>
public sealed class ClaudeOAuthCredentialStoreTests : IDisposable
{
    private const string AccessToken = "access-token-value-0123456789";
    private const string RefreshToken = "refresh-token-value-0123456789abcd";
    private static readonly DateTimeOffset ExpiresAt = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AuthenticatedAt = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "omni-oauth-f4-" + Guid.NewGuid().ToString("N"));

    public ClaudeOAuthCredentialStoreTests() => Directory.CreateDirectory(_directory);

    private string MetadataPath() => Path.Combine(_directory, "claude-oauth-account.json");

    private static ClaudeOAuthCredential Credential(
        string access = AccessToken,
        string refresh = RefreshToken,
        string clientId = "cid-0001") => new(access, refresh, ExpiresAt, ["user:profile", "user:inference"], clientId)
        {
            AccountUuid = "acc-1",
            OrganizationUuid = "org-1",
            EmailAddress = "u@example.com",
            DisplayName = "Jo",
            SubscriptionType = "max",
            RateLimitTier = "tier-5x",
            AuthenticatedAt = AuthenticatedAt,
        };

    // ---- Round-trip -----------------------------------------------------------------------

    [Fact]
    public void Save_then_Load_returns_the_same_credential()
    {
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());
        var credential = Credential();

        sut.Save("anthropic-oauth", credential, TestContext.Current.CancellationToken);
        var loaded = sut.Load("anthropic-oauth", TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(credential.AccessToken, loaded!.AccessToken);
        Assert.Equal(credential.RefreshToken, loaded.RefreshToken);
        Assert.Equal(credential.ExpiresAt, loaded.ExpiresAt);
        Assert.Equal(credential.ClientId, loaded.ClientId);
        Assert.Equal(credential.Scopes, loaded.Scopes);
        Assert.Equal(credential.AccountUuid, loaded.AccountUuid);
        Assert.Equal(credential.OrganizationUuid, loaded.OrganizationUuid);
    }

    [Fact]
    public void Load_without_a_stored_credential_is_null_not_a_failure()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());

        Assert.Null(sut.Load("nadie", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Exists_reflects_presence_without_returning_the_secret()
    {
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());

        Assert.False(sut.Exists("ref", TestContext.Current.CancellationToken));
        sut.Save("ref", Credential(), TestContext.Current.CancellationToken);
        Assert.True(sut.Exists("ref", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Delete_removes_both_the_credential_and_its_metadata()
    {
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());
        sut.Save("ref", Credential(), TestContext.Current.CancellationToken);

        sut.Delete("ref", TestContext.Current.CancellationToken);

        Assert.Null(sut.Load("ref", TestContext.Current.CancellationToken));
        Assert.Null(sut.ReadAccountInfo("ref", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Credentials_of_two_secret_refs_do_not_collide()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        sut.Save("uno", Credential(clientId: "cid-a"), TestContext.Current.CancellationToken);
        sut.Save("otro", Credential(clientId: "cid-b"), TestContext.Current.CancellationToken);

        Assert.Equal("cid-a", sut.Load("uno", TestContext.Current.CancellationToken)?.ClientId);
        Assert.Equal("cid-b", sut.Load("otro", TestContext.Current.CancellationToken)?.ClientId);
    }

    // ---- Secreto fuera del claro (INV-016 / ADR-0018) ------------------------------------

    [Fact]
    public void The_credential_lives_under_its_own_key_separate_from_api_key_refs()
    {
        // La confidencialidad la aporta ICredentialStore (FileCredentialStore cifra con
        // DPAPI/AES-GCM, ADR-0018). Lo que garantiza ESTA capa es el aislamiento de claves: un
        // OAuth credential nunca se guarda bajo la secret ref de una API key, así que un provider
        // configurado con AuthKind.ApiKey no puede leerlo por accidente.
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());

        sut.Save("anthropic-oauth", Credential(), TestContext.Current.CancellationToken);
        store.Save("anthropic", "sk-api-key-del-mismo-provider", TestContext.Current.CancellationToken);

        var keys = store.Values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["anthropic", ClaudeOAuthCredentialStore.CredentialKey("anthropic-oauth")],
            keys);
        Assert.StartsWith(ClaudeOAuthCredentialStore.CredentialKeyPrefix,
            ClaudeOAuthCredentialStore.CredentialKey("anthropic-oauth"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_store_path_encrypts_before_touching_disk()
    {
        // Cubre la afirmacion anterior contra la implementacion de produccion, no contra el fake:
        // FileCredentialStore debe cifrar (DPAPI en Windows, AES-GCM fuera), asi que buscar el
        // token en claro en el archivo tiene que fallar.
        var directory = Path.Combine(_directory, "credential-store");
        var store = new OmniCore.Infrastructure.FileCredentialStore(Path.Combine(directory, "credentials"));
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());

        sut.Save("ref", Credential(), TestContext.Current.CancellationToken);
        var loaded = sut.Load("ref", TestContext.Current.CancellationToken);

        Assert.Equal(AccessToken, loaded!.AccessToken);
        var raw = File.ReadAllText(store.PathValue());
        Assert.DoesNotContain(AccessToken, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void The_metadata_file_never_contains_tokens()
    {
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());

        sut.Save("ref", Credential(), TestContext.Current.CancellationToken);

        var json = File.ReadAllText(MetadataPath());
        Assert.DoesNotContain(AccessToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, json, StringComparison.Ordinal);
    }

    [Fact]
    public void No_temporary_files_survive_a_write()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());

        sut.Save("ref", Credential(), TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public void Loading_registers_the_secrets_for_redaction_before_returning_them()
    {
        // El contrato de ICredentialStore (ADR-0018): lo que sale del almacén ya debe ser
        // redactable, si no un log posterior filtraría el token.
        //
        // Se prueba contra un redactor instalado por el test, no contra Current: otro test puede
        // haber instalado el suyo antes y el resultado dependería del orden de ejecución.
        var redactor = new OmniCore.Security.SecretRedactor();
        SecretRedactorRegistry.Install(redactor);
        try
        {
            var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
            sut.Save("ref", Credential(), TestContext.Current.CancellationToken);

            var loaded = sut.Load("ref", TestContext.Current.CancellationToken);

            Assert.NotNull(loaded);
            // El contrato es que el valor ya esta registrado al salir del almacen: un texto que lo
            // contenga sale redactado, sin que nadie tenga que volver a registrarlo.
            var sentence = redactor.Redact("antes " + AccessToken + " y " + RefreshToken + " después");
            Assert.DoesNotContain(AccessToken, sentence, StringComparison.Ordinal);
            Assert.DoesNotContain(RefreshToken, sentence, StringComparison.Ordinal);
            Assert.Contains("antes ", sentence, StringComparison.Ordinal);
            Assert.Contains("después", sentence, StringComparison.Ordinal);
            // Un token pasado suelto tambien desaparece: es el caso de un log que imprime solo el
            // campo credencial.
            Assert.DoesNotContain(loaded!.AccessToken, redactor.Redact(loaded.AccessToken), StringComparison.Ordinal);
        }
        finally
        {
            // Se devuelve el redactor compartido: Install no apila, y dejar aqui el del test
            // pondria a los demas pruebas a mirar un objeto que ya no existe.
            SecretRedactorRegistry.Install(OmniCore.Security.SecretRedactor.Shared);
        }
    }

    [Fact]
    public void Saving_registers_each_token_separately_not_only_the_whole_series()
    {
        // ICredentialStore registra el valor opaco que guarda (aqui una serie con ambos tokens).
        // Redact busca ese valor completo como substring, asi que registrar solo la serie dejaba
        // filtrar un token cuando alguien logueaba uno suelto. INV-016 / ADR-0018.
        var redactor = new OmniCore.Security.SecretRedactor();
        SecretRedactorRegistry.Install(redactor);
        try
        {
            var store = new InMemoryCredentialStore();
            var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());
            sut.Save("ref", Credential(), TestContext.Current.CancellationToken);

            // El access token suelto tiene que desaparecer del texto, sin que nadie haya llamado
            // todavia a Load.
            Assert.DoesNotContain(AccessToken, redactor.Redact("log: " + AccessToken), StringComparison.Ordinal);
            Assert.DoesNotContain(RefreshToken, redactor.Redact("log: " + RefreshToken), StringComparison.Ordinal);
            // Y la serie almacenada tampoco revela nada al ser redactada tal cual.
            var series = store.Values.Values.First();
            Assert.DoesNotContain(AccessToken, redactor.Redact(series), StringComparison.Ordinal);
        }
        finally
        {
            SecretRedactorRegistry.Install(OmniCore.Security.SecretRedactor.Shared);
        }
    }

    // ---- Metadata no secreta --------------------------------------------------------------

    [Fact]
    public void Account_info_is_readable_without_touching_the_credential_store()
    {
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());
        sut.Save("ref", Credential(), TestContext.Current.CancellationToken);

        // El credential sigue almacenado y aun así la metadata se lee sin tocarlo: es lo que
        // consume /doctor.
        Assert.NotEmpty(store.Values);
        var info = sut.ReadAccountInfo("ref", TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal("max", info!.SubscriptionType);
        Assert.Equal("tier-5x", info.RateLimitTier);
        Assert.Equal("u@example.com", info.EmailAddress);
        Assert.True(info.HasInferenceScope);
        Assert.Equal(AuthenticatedAt, info.AuthenticatedAt);
        Assert.Equal(ExpiresAt, info.ExpiresAt);
    }

    [Fact]
    public void Missing_account_info_is_null_not_an_exception()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());

        Assert.Null(sut.ReadAccountInfo("nadie", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Corrupt_metadata_degrades_to_empty_instead_of_breaking_the_read()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        sut.Save("ref", Credential(), TestContext.Current.CancellationToken);
        File.WriteAllText(MetadataPath(), "{esto no es json");

        Assert.Null(sut.ReadAccountInfo("ref", TestContext.Current.CancellationToken));
        // La credencial sigue intacta: metadata y secretos tienen destinos separados a proposito.
        Assert.NotNull(sut.Load("ref", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Saving_twice_overwrites_the_row_without_duplicating_it()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        sut.Save("ref", Credential(clientId: "cid-1"), TestContext.Current.CancellationToken);
        sut.Save("ref", Credential(clientId: "cid-2"), TestContext.Current.CancellationToken);

        Assert.Equal("cid-2", sut.Load("ref", TestContext.Current.CancellationToken)?.ClientId);
        var json = File.ReadAllText(MetadataPath());
        Assert.Equal(1, CountOccurrences(json, "\"ref\""));
    }

    [Fact]
    public void Saving_one_ref_keeps_the_rows_of_the_others()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        sut.Save("uno", Credential(), TestContext.Current.CancellationToken);
        sut.Save("otro", Credential(clientId: "cid-b"), TestContext.Current.CancellationToken);

        Assert.NotNull(sut.ReadAccountInfo("uno", TestContext.Current.CancellationToken));
        Assert.NotNull(sut.ReadAccountInfo("otro", TestContext.Current.CancellationToken));

        sut.Delete("otro", TestContext.Current.CancellationToken);

        Assert.NotNull(sut.ReadAccountInfo("uno", TestContext.Current.CancellationToken));
        Assert.Null(sut.ReadAccountInfo("otro", TestContext.Current.CancellationToken));
    }

    // ---- Detección multi-proceso (plan §5) -------------------------------------------------

    [Fact]
    public void Metadata_written_at_advances_after_a_save()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        var before = sut.MetadataWrittenAt(TestContext.Current.CancellationToken);

        sut.Save("ref", Credential(), TestContext.Current.CancellationToken);
        var after = sut.MetadataWrittenAt(TestContext.Current.CancellationToken);

        Assert.Null(before);
        Assert.NotNull(after);
    }

    [Fact]
    public void Metadata_written_at_grows_when_another_process_writes_the_file()
    {
        // El coordinador de refresh decide si recargar comparando esta marca (refreshTokenDeadSet.ts
        // invalida el dead-set cuando el mtime avanza por una escritura externa).
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        sut.Save("ref", Credential(), TestContext.Current.CancellationToken);
        var first = sut.MetadataWrittenAt(TestContext.Current.CancellationToken);

        File.SetLastWriteTimeUtc(MetadataPath(), DateTime.SpecifyKind(first!.Value.UtcDateTime, DateTimeKind.Utc).AddMinutes(1));
        var second = sut.MetadataWrittenAt(TestContext.Current.CancellationToken);

        Assert.NotNull(second);
        Assert.True(second! > first);
    }

    [Fact]
    public void Metadata_written_at_is_null_when_the_file_does_not_exist_yet()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());

        Assert.Null(sut.MetadataWrittenAt(TestContext.Current.CancellationToken));
    }

    // ---- Serie resistente ------------------------------------------------------------------

    [Fact]
    public void A_payload_from_an_unknown_schema_version_is_ignored_not_misread()
    {
        // Un formato futuro no se interpreta como si fuera el actual: mejor pedir login que
        // usar campos desplazados en silencio.
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());
        store.Save(ClaudeOAuthCredentialStore.CredentialKey("ref"),
            """{"schema_version":99,"access_token":"x","refresh_token":"y","expires_at_unix":1,"client_id":"z"}""",
            TestContext.Current.CancellationToken);

        Assert.Null(sut.Load("ref", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_credential_missing_required_fields_is_rejected()
    {
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());
        store.Save(ClaudeOAuthCredentialStore.CredentialKey("ref"),
            """{"schema_version":1,"access_token":"","refresh_token":"y","expires_at_unix":1,"client_id":"z"}""",
            TestContext.Current.CancellationToken);

        Assert.Null(sut.Load("ref", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Garbage_in_the_credential_store_is_treated_as_absent()
    {
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());
        store.Save(ClaudeOAuthCredentialStore.CredentialKey("ref"), "no-es-json-en-absoluto",
            TestContext.Current.CancellationToken);

        Assert.Null(sut.Load("ref", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Scopes_survive_round_trip_and_drive_the_inference_flag()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        var noInference = new ClaudeOAuthCredential("a-long-enough-access", "a-long-enough-refresh",
            ExpiresAt, ["user:profile"], "cid") { AuthenticatedAt = AuthenticatedAt };

        sut.Save("ref", noInference, TestContext.Current.CancellationToken);
        var loaded = sut.Load("ref", TestContext.Current.CancellationToken);

        Assert.Equal(["user:profile"], loaded?.Scopes);
        Assert.False(loaded!.CanDoInference);
    }

    // ---- Validación de argumentos ----------------------------------------------------------

    [Fact]
    public void Empty_secret_ref_is_rejected_everywhere()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        var ct = TestContext.Current.CancellationToken;

        Assert.Throws<ArgumentException>(() => sut.Save("", Credential(), ct));
        Assert.Throws<ArgumentException>(() => sut.Load(" ", ct));
        Assert.Throws<ArgumentException>(() => sut.Delete("", ct));
        Assert.Throws<ArgumentException>(() => sut.Exists("  ", ct));
        Assert.Throws<ArgumentException>(() => sut.ReadAccountInfo("", ct));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var sut = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        var ct = TestContext.Current.CancellationToken;

        Assert.Throws<ArgumentNullException>(() => sut.Save("ref", null!, ct));
        Assert.Throws<ArgumentNullException>(() => new ClaudeOAuthCredentialStore(null!, MetadataPath()));
    }

    [Fact]
    public void Missing_metadata_path_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), ""));
    }

    // ---- Cancelación -------------------------------------------------------------------------

    [Fact]
    public void Cancellation_stops_the_save_before_it_touches_anything()
    {
        var store = new InMemoryCredentialStore();
        var sut = new ClaudeOAuthCredentialStore(store, MetadataPath());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            sut.Save("ref", Credential(), cts.Token));

        Assert.Equal(0, store.SaveCount);
        Assert.False(File.Exists(MetadataPath()));
    }

    // ---- Presentación sin secretos -------------------------------------------------------------

    [Fact]
    public void Mask_shows_enough_to_tell_credentials_apart_and_nothing_more()
    {
        var masked = ClaudeOAuthMask.Mask("sk-ant-api03-VeryLongSecretValueThatNobodyShouldSee");

        Assert.NotNull(masked);
        Assert.StartsWith("sk-a", masked);
        Assert.EndsWith("dSee", masked);
        Assert.DoesNotContain("VeryLong", masked);
        Assert.True(masked!.Length < 20, "la máscara no puede crecer sobre el secreto");
    }

    [Fact]
    public void Mask_of_a_short_secret_hides_it_completely()
    {
        Assert.Equal("****", ClaudeOAuthMask.Mask("abcd"));
        Assert.Null(ClaudeOAuthMask.Mask(null));
        Assert.Null(ClaudeOAuthMask.Mask(""));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
