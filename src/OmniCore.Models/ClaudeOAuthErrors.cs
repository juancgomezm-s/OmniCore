namespace OmniCore.Models;

/// <summary>
/// Base de los fallos del flujo OAuth. Los errores de dominio son tipados, no <see cref="Exception"/>
/// genérica (spec §71). Ninguno lleva tokens, codes ni cuerpos crudos: pueden contener material
/// sensible y los secretos no llegan a logs ni artefactos (INV-016, ADR-0018).
/// </summary>
public abstract class ClaudeOAuthException : Exception
{
    protected ClaudeOAuthException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>El exchange devolvió 401: el authorization code no sirve (usado, caducado o falso).</summary>
public sealed class ClaudeOAuthExchangeInvalidCodeException : ClaudeOAuthException
{
    public ClaudeOAuthExchangeInvalidCodeException()
        : base("El authorization code fue rechazado por Anthropic.")
    {
    }
}

/// <summary>El exchange falló por algo que no es un rechazo de credencial.</summary>
public sealed class ClaudeOAuthExchangeHttpException : ClaudeOAuthException
{
    public ClaudeOAuthExchangeHttpException(int statusCode, string? serverErrorType)
        : base($"El exchange de OAuth falló con HTTP {statusCode}"
            + (serverErrorType is null ? "." : $" ({serverErrorType})."))
    {
        StatusCode = statusCode;
        ServerErrorType = serverErrorType;
    }

    public int StatusCode { get; }

    /// <summary>Error-type del servidor, solo si tiene forma de token RFC 6749.</summary>
    public string? ServerErrorType { get; }
}

/// <summary>
/// El refresh token está muerto: Anthropic devolvió <c>invalid_grant</c>. Distinto de un fallo
/// transitorio — aquí reintentar no sirve y hace falta volver a autenticarse.
/// </summary>
public sealed class ClaudeOAuthInvalidGrantException : ClaudeOAuthException
{
    public ClaudeOAuthInvalidGrantException()
        : base("Anthropic rechazó el refresh token. Hay que volver a iniciar sesión.")
    {
    }
}

/// <summary>El refresh falló por algo que no es un rechazo de credencial.</summary>
public sealed class ClaudeOAuthRefreshHttpException : ClaudeOAuthException
{
    public ClaudeOAuthRefreshHttpException(int statusCode, string? serverErrorType)
        : base($"El refresco de OAuth falló con HTTP {statusCode}"
            + (serverErrorType is null ? "." : $" ({serverErrorType})."))
    {
        StatusCode = statusCode;
        ServerErrorType = serverErrorType;
    }

    public int StatusCode { get; }

    public string? ServerErrorType { get; }
}

/// <summary>No hubo respuesta del endpoint (red, DNS o timeout del RPC).</summary>
public sealed class ClaudeOAuthNetworkException : ClaudeOAuthException
{
    public ClaudeOAuthNetworkException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// El endpoint respondió bien pero el contenido no era lo que el protocolo dice. Se separa del
/// fallo de red porque indica cambio de contrato en el backend, no indisponibilidad (plan §11.1).
/// </summary>
public sealed class ClaudeOAuthProtocolException : ClaudeOAuthException
{
    public ClaudeOAuthProtocolException(string message, Exception? inner, int statusCode)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
