namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;

/// <summary>
/// Valida los argumentos de una tool contra su InputSchema declarado, ANTES de Prepare
/// (ADR-0014 §1, INV-001): una tool nunca se ejecuta con argumentos que no conforman su
/// schema; el rechazo es un resultado tipado que alimenta el repair loop (spec §71).
/// Sin reflexión (AOT): System.Text.Json en modo de solo lectura.
///
/// Semántica — estricta en lo que declara, permisiva en lo que no entiende:
///  - Un schema sin `properties` (p. ej. `{}` en las tools de test y las permisivas) no
///    declara superficie: se acepta cualquier objeto y la tool hace su propia validación
///    semántica (spec §40). Los `required` declarados sí se aplican siempre.
///  - Los campos de `required` deben estar presentes.
///  - Cada campo declarado se comprueba contra su `type` (string/integer/number/boolean/
///    array/object). `integer` exige un número entero que quepa en Int32.
///  - Los campos NO declarados se rechazan salvo `additionalProperties: true` explícito:
///    para una superficie de tools cerrada, JSON Schema es strict-by-default.
///  - Nunca lanza: unos argumentos que no son un objeto JSON válido devuelven error tipado;
///    un schema que no se puede parsear se trata como permisivo (bug de la tool, no del
///    modelo).
/// </summary>
internal static class ToolSchemaValidator
{
    /// <summary>Código de error tipado (spec §71): los argumentos no conforman el InputSchema.</summary>
    public const string InvalidArgumentsCode = "INVALID_ARGUMENTS";

    /// <summary>Valida los argumentos contra el schema. null si son válidos; si no, el detalle del error.</summary>
    public static string? Validate(string? argumentsJson, InputSchema schema)
    {
        if (argumentsJson is null || argumentsJson.Length == 0)
        {
            argumentsJson = "{}";
        }

        using var doc = ParseJson(argumentsJson);
        if (doc is null)
        {
            return "los argumentos no son JSON válido";
        }

        using var schemaDoc = ParseJson(schema.ToString());
        if (schemaDoc is null)
        {
            // Schema no parseable: no es un fallo del modelo. Permisivo (fail-open); la tool
            // sigue siendo responsable de su validación semántica.
            return null;
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "los argumentos deben ser un objeto JSON";
        }

        var schemaRoot = schemaDoc.RootElement;

        // required: ["campo", ...]
        var required = new List<string>();
        if (schemaRoot.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    required.Add(item.GetString()!);
                }
            }
        }

        foreach (var field in required)
        {
            if (!root.TryGetProperty(field, out _))
            {
                return "campo requerido faltante: '" + field + "'";
            }
        }

        // properties: { ... } — sin superficie declarada no hay nada que tipar ni de
        // desconocido que rechazar: schema permisivo.
        if (!schemaRoot.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var allowAdditional = schemaRoot.TryGetProperty("additionalProperties", out var additionalElement)
            && additionalElement.ValueKind == JsonValueKind.True;

        foreach (var property in root.EnumerateObject())
        {
            if (!propertiesElement.TryGetProperty(property.Name, out var propertySchema))
            {
                if (!allowAdditional)
                {
                    return "campo desconocido: '" + property.Name + "'";
                }

                continue;
            }

            var typeError = ValidateType(property.Name, property.Value, propertySchema);
            if (typeError is not null)
            {
                return typeError;
            }
        }

        return null;
    }

    private static JsonDocument? ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Comprueba el `type` declarado de un campo; null si conforms o no hay type.</summary>
    private static string? ValidateType(string key, JsonElement value, JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("type", out var typeElement))
        {
            return null; // sin tipo declarado, no se valida
        }

        // JSON Schema admite varios tipos: ["string","null"] para opcionales que aceptan
        // JSON null (p. ej. expectedVersion de filesystem.write: null = crear).
        if (typeElement.ValueKind == JsonValueKind.Array)
        {
            var accepted = new List<string>();
            foreach (var type in typeElement.EnumerateArray())
            {
                if (type.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                accepted.Add(type.GetString()!);
                if (TypeMatches(type.GetString()!, value))
                {
                    return null;
                }
            }

            return "campo '" + key + "': se esperaba " + string.Join("|", accepted);
        }

        if (typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var expected = typeElement.GetString()!;
        return TypeMatches(expected, value) ? null : "campo '" + key + "': se esperaba " + expected;
    }

    /// <summary>Un valor JSON conforms el tipo declarado ("null" = JSON null explícito).</summary>
    private static bool TypeMatches(string expectedType, JsonElement value) => expectedType switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true, // tipo desconocido (refinements del schema): no se valida
    };
}
