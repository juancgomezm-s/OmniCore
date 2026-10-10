using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Models;

/// <summary>
/// Contexto source-gen para el wire de OAuth. Los analizadores AOT están activos (ADR-0038 §5),
/// así que nada se serializa por reflexión: cada tipo del wire va declarado aquí.
///
/// El request se manda como Dictionary&lt;string, object&gt; porque mezcla strings e enteros
/// (<c>expires_in</c>) y porque omite campos según la opción elegida — un DTO con huecos
/// obligaría a emitir nulls que el servidor no espera.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClaudeOAuthTokenResponse))]
[JsonSerializable(typeof(ClaudeOAuthAccountDto))]
[JsonSerializable(typeof(ClaudeOAuthOrganizationDto))]
[JsonSerializable(typeof(ClaudeOAuthWireErrorResponse))]
[JsonSerializable(typeof(ClaudeOAuthProfileDto))]
[JsonSerializable(typeof(ClaudeOAuthProfileAccountDto))]
[JsonSerializable(typeof(ClaudeOAuthProfileOrganizationDto))]
[JsonSerializable(typeof(ClaudeOAuthTokenRequest))]
internal partial class ClaudeOAuthJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Cuerpo de error del token endpoint. RFC 6749 §5.1 permite <c>error</c> como string o como
/// objeto con <c>type</c>; Anthropic usa ambas formas según el camino y client.ts
/// (isInvalidGrantError) mira las dos. Como System.Text.Json no admite dos propiedades con el
/// mismo nombre JSON, se resuelve con un converter que lee la rama <c>error</c> una sola vez.
/// Sin JsonElement suelto ni reflexión: los analizadores AOT están activos (ADR-0038 §5).
/// </summary>
[JsonConverter(typeof(ClaudeOAuthWireErrorResponse.Converter))]
internal sealed class ClaudeOAuthWireErrorResponse
{
    public string? ErrorType { get; set; }

    public string? ErrorDescription { get; set; }

    internal sealed class Converter : JsonConverter<ClaudeOAuthWireErrorResponse>
    {
        public override ClaudeOAuthWireErrorResponse? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException();
            }

            var result = new ClaudeOAuthWireErrorResponse();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return result;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException();
                }

                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "error":
                        result.ErrorType = ReadErrorValue(ref reader);
                        break;
                    case "error_description":
                        result.ErrorDescription = reader.TokenType == JsonTokenType.String
                            ? reader.GetString()
                            : null;
                        reader.Skip();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            throw new JsonException();
        }

        /// <summary>
        /// Lee la rama <c>error</c>: string plano u objeto con <c>type</c>. Un valor de otra forma
        /// se ignora en vez de tumbar el parseo — seguimos queriendo el status y el resto.
        /// </summary>
        private static string? ReadErrorValue(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();

                case JsonTokenType.StartObject:
                    string? type = null;
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndObject)
                        {
                            return type;
                        }

                        if (reader.TokenType != JsonTokenType.PropertyName)
                        {
                            throw new JsonException();
                        }

                        var inner = reader.GetString();
                        reader.Read();
                        if (inner == "type" && reader.TokenType == JsonTokenType.String)
                        {
                            type = reader.GetString();
                        }

                        reader.Skip();
                    }

                    throw new JsonException();

                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, ClaudeOAuthWireErrorResponse value, JsonSerializerOptions options) =>
            throw new NotSupportedException("El cuerpo de error solo se lee, nunca se escribe.");
    }
}

/// <summary>
/// Cuerpo de request del token endpoint. Es un DTO con campos concretos en vez de un
/// Dictionary&lt;string, object&gt; porque bajo AOT un valor boxed (el <c>expires_in</c> entero)
/// no tiene metadata de serialización disponible y el source-gen se cae en tiempo de ejecución.
/// Los campos que no aplican al grant en curso quedan null y se omiten
/// (<see cref="JsonIgnoreCondition.WhenWritingNull"/>), que es lo que espera el servidor.
/// </summary>
internal sealed class ClaudeOAuthTokenRequest
{
    [JsonPropertyName("grant_type")]
    public string GrantType { get; set; } = "";

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("redirect_uri")]
    public string? RedirectUri { get; set; }

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("code_verifier")]
    public string? CodeVerifier { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("expires_in")]
    public long? ExpiresInSeconds { get; set; }
}
