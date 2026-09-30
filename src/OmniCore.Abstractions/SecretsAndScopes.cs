namespace OmniCore.Abstractions;

using OmniCore.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// SecretProvider: única vía de lectura de secretos (ADR-0018 §1, ADR-0011 §3).
/// Devuelve un <c>Secret</c> que no se serializa y cuyo ToString es ***.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Lee un secreto por referencia (p. ej. "openrouter"); null si no existe.</summary>
    Secret GetSecret(string secretRef, CancellationToken cancellationToken);
}

/// <summary>Credencial seguro: serialización siempre redactada, ToString = *** (ADR-0018).</summary>
[JsonConverter(typeof(SecretJsonConverter))]
public sealed class Secret
{
    private readonly string _value;

    private Secret(string value) => _value = value;

    public static Secret Of(string value) => new(value);

    /// <summary>Acceso al valor solo dentro del proceso que lo autoriza.</summary>
    public string Value() => _value;

    public override string ToString() => "***";
}

/// <summary>Impide que System.Text.Json exponga el valor de un secreto (ADR-0018 §1).</summary>
public sealed class SecretJsonConverter : JsonConverter<Secret>
{
    public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("Secret no puede deserializarse.");

    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) =>
        writer.WriteStringValue("***");
}

/// <summary>Almacén de credenciales por plataforma (Windows Credential Manager / DPAPI; ADR-0011 §3).</summary>
public interface ICredentialStore
{
    void Save(string key, string value, CancellationToken cancellationToken);

    string? Load(string key, CancellationToken cancellationToken);

    void Delete(string key, CancellationToken cancellationToken);
}

/// <summary>Formas de autenticación de un provider.</summary>
public enum AuthKind
{
    None,
    ApiKey,
    Bearer,
    OAuth,
}

/// <summary>Cómo se autentica un provider (ADR-0011 §3).</summary>
public sealed class AuthConfig
{
    public AuthKind Kind { get; }

    public string? SecretRef { get; }

    public AuthConfig(AuthKind kind, string? secretRef)
    {
        Kind = kind;
        SecretRef = secretRef;
    }

    public static AuthConfig None() => new(AuthKind.None, null);

    public static AuthConfig ApiKey(string secretRef) => new(AuthKind.ApiKey, secretRef);
}

/// <summary>
/// Resuelve la configuración por scope (ADR-0022 §25): el más específico gana. La
/// implementación de referencia vive en Domain (ScopeResolver); Abstractions re-exporta
/// el tipo para los consumidores que solo ven Abstractions (INV-011).
/// </summary>
public interface IScopeResolver<T> : OmniCore.Domain.IScopeResolver<T>
{
}