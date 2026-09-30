namespace OmniCore.Security;

using System.Collections.Concurrent;
using System.Net;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Redactor process-wide de valores resueltos y patrones de autenticación (ADR-0018 §2).
/// No registra valores inferiores a 8 caracteres para evitar ocultar palabras triviales.
/// </summary>
public sealed class SecretRedactor : ISecretRedactor
{
    public const int MinimumSecretLength = 8;
    public const string Marker = "[REDACTED]";

    private readonly ConcurrentDictionary<string, byte> _known = new(StringComparer.Ordinal);
    private readonly PiiRedactor _patterns = new();

    /// <summary>La instancia usada por todos los sinks del proceso.</summary>
    public static SecretRedactor Shared { get; } = CreateShared();

    public void RegisterSecret(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinimumSecretLength) return;

        Add(value);
        Add(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)));
        Add(Uri.EscapeDataString(value));
        Add(WebUtility.UrlEncode(value));
    }

    public string Redact(string input) => RedactKnownValues(_patterns.RedactPatternsOnly(input));

    /// <summary>Redacta solo valores conocidos; se usa como hook de PiiRedactor para el journal.</summary>
    public string RedactKnownValues(string input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var result = input;
        // Reemplazar primero valores largos para impedir que un secreto corto registrado después
        // fragmente y deje visible parte de un secreto más largo.
        foreach (var value in _known.Keys.OrderByDescending(static value => value.Length))
            result = result.Replace(value, Marker, StringComparison.Ordinal);
        return result;
    }

    private void Add(string value)
    {
        if (!string.IsNullOrEmpty(value)) _known.TryAdd(value, 0);
    }

    private static SecretRedactor CreateShared()
    {
        var redactor = new SecretRedactor();
        SecretRedactorRegistry.Install(redactor);
        PiiRedactor.SetAdditionalRedactor(redactor.RedactKnownValues);
        return redactor;
    }
}
