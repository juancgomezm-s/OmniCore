namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Bounded validator for the JSON Schema subset used by registered tools.</summary>
internal static class ToolSchemaValidator
{
    public const string InvalidArgumentsCode = ToolErrorCode.InvalidArgumentsCode;
    private const int MaximumSchemaCharacters = 65_536;
    private const int MaximumSchemaDepth = 16;

    public static string? Validate(string? argumentsJson, InputSchema schema,
        bool allowAdditionalPropertiesByDefault = false)
    {
        argumentsJson = string.IsNullOrEmpty(argumentsJson) ? "{}" : argumentsJson;
        if (!TryParse(argumentsJson, out var arguments, out var argumentError))
            return "los argumentos no son JSON válido: " + argumentError;
        using (arguments)
        {
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                return "los argumentos deben ser un objeto JSON";
            if (schema.ToString().Length > MaximumSchemaCharacters)
                return "el schema de la tool excede el límite de tamaño";
            if (!TryParse(schema.ToString(), out var schemaDocument, out var schemaError))
                return "el schema de la tool no es JSON válido: " + schemaError;
            using (schemaDocument)
            {
                if (schemaDocument.RootElement.ValueKind != JsonValueKind.Object)
                    return "el schema de la tool debe ser un objeto JSON";
                var schemaTreeError = ValidateSchemaTree(schemaDocument.RootElement, "$", 0);
                if (schemaTreeError is not null) return "el schema de la tool es inválido: " + schemaTreeError;
                return ValidateValue(arguments.RootElement, schemaDocument.RootElement, "$", 0,
                    allowAdditionalPropertiesByDefault);
            }
        }
    }

    private static string? ValidateValue(JsonElement value, JsonElement schema, string path, int depth,
        bool allowAdditionalPropertiesByDefault)
    {
        if (depth > MaximumSchemaDepth) return path + ": schema anidado demasiado profundamente";
        if (schema.ValueKind == JsonValueKind.True) return null;
        if (schema.ValueKind == JsonValueKind.False) return path + ": valor rechazado por el schema";
        if (schema.ValueKind != JsonValueKind.Object) return path + ": definición de schema inválida";
        var shapeError = ValidateSchemaShape(schema, path);
        if (shapeError is not null) return shapeError;

        var types = ReadStringOrStringArray(schema, "type", out var typeError);
        if (typeError is not null) return path + ": " + typeError;
        if (types is not null && !types.Any(type => TypeMatches(type, value)))
            return path + ": se esperaba " + string.Join("|", types);

        if (schema.TryGetProperty("enum", out var enumValues))
        {
            if (!enumValues.EnumerateArray().Any(item => JsonValueEquals(item, value)))
                return path + ": valor fuera del enum permitido";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var length = value.GetString()!.Length;
            if (TryInt(schema, "minLength", out var minLength) && length < minLength)
                return path + ": longitud menor que minLength";
            if (TryInt(schema, "maxLength", out var maxLength) && length > maxLength)
                return path + ": longitud mayor que maxLength";
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (schema.TryGetProperty("minimum", out var minimum)
                && CompareNumbers(value, minimum) is { } minComparison && minComparison < 0)
                return path + ": valor menor que minimum";
            if (schema.TryGetProperty("maximum", out var maximum)
                && CompareNumbers(value, maximum) is { } maxComparison && maxComparison > 0)
                return path + ": valor mayor que maximum";
            if ((schema.TryGetProperty("minimum", out minimum) && CompareNumbers(value, minimum) is null)
                || (schema.TryGetProperty("maximum", out maximum) && CompareNumbers(value, maximum) is null))
                return path + ": número fuera del rango comprobable";
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            if (TryInt(schema, "minItems", out var minItems) && count < minItems)
                return path + ": menos elementos que minItems";
            if (TryInt(schema, "maxItems", out var maxItems) && count > maxItems)
                return path + ": más elementos que maxItems";
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var error = ValidateValue(item, itemSchema, path + "[" + index + "]", depth + 1,
                        allowAdditionalPropertiesByDefault);
                    if (error is not null) return error;
                    index++;
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Object)
            return ValidateObject(value, schema, path, depth, allowAdditionalPropertiesByDefault);
        return null;
    }

    private static string? ValidateObject(JsonElement value, JsonElement schema, string path, int depth,
        bool allowAdditionalPropertiesByDefault)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) return path + ": propiedad duplicada '" + property.Name + "'";

        if (schema.TryGetProperty("required", out var required))
            foreach (var field in required.EnumerateArray())
            {
                var name = field.GetString()!;
                if (!value.TryGetProperty(name, out _)) return path + ": campo requerido faltante: '" + name + "'";
            }

        var hasProperties = schema.TryGetProperty("properties", out var properties);
        var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
        var rejectAdditional = hasAdditional && additional.ValueKind == JsonValueKind.False
            || !hasAdditional && hasProperties && !allowAdditionalPropertiesByDefault;
        foreach (var property in value.EnumerateObject())
        {
            if (!hasProperties || properties.ValueKind != JsonValueKind.Object
                || !properties.TryGetProperty(property.Name, out var childSchema))
            {
                if (rejectAdditional) return path + ": campo desconocido: '" + property.Name + "'";
                if (hasAdditional && additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    var additionalError = ValidateValue(property.Value, additional,
                        path + "." + property.Name, depth + 1, allowAdditionalPropertiesByDefault);
                    if (additionalError is not null) return additionalError;
                }
                continue;
            }
            var error = ValidateValue(property.Value, childSchema, path + "." + property.Name, depth + 1,
                allowAdditionalPropertiesByDefault);
            if (error is not null) return error;
        }
        return null;
    }

    private static string? ValidateSchemaShape(JsonElement schema, string path)
    {
        if (schema.TryGetProperty("required", out var required)
            && (required.ValueKind != JsonValueKind.Array
                || required.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)))
            return path + ": required debe ser un array de strings";
        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind != JsonValueKind.Object)
            return path + ": properties debe ser un objeto";
        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            return path + ": additionalProperties debe ser un schema o booleano";
        if (schema.TryGetProperty("enum", out var enumValues)
            && (enumValues.ValueKind != JsonValueKind.Array || enumValues.GetArrayLength() == 0))
            return path + ": enum debe ser un array no vacío";
        _ = ReadStringOrStringArray(schema, "type", out var typeError);
        if (typeError is not null) return path + ": " + typeError;
        foreach (var key in new[] { "minLength", "maxLength", "minItems", "maxItems" })
            if (schema.TryGetProperty(key, out var integer)
                && (integer.ValueKind != JsonValueKind.Number || !integer.TryGetInt32(out var n) || n < 0))
                return path + ": " + key + " debe ser un entero no negativo";
        foreach (var key in new[] { "minimum", "maximum" })
            if (schema.TryGetProperty(key, out var number) && number.ValueKind != JsonValueKind.Number)
                return path + ": " + key + " debe ser numérico";
        if (schema.TryGetProperty("items", out var items)
            && items.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            return path + ": items debe ser un schema";
        if (schema.TryGetProperty("additionalProperties", out var additionalProperties)
            && additionalProperties.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            return path + ": additionalProperties debe ser un schema o booleano";
        return null;
    }

    private static string? ValidateSchemaTree(JsonElement schema, string path, int depth)
    {
        if (depth > MaximumSchemaDepth) return path + ": schema anidado demasiado profundamente";
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return null;
        if (schema.ValueKind != JsonValueKind.Object) return path + ": definición de schema inválida";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in schema.EnumerateObject())
            if (!names.Add(property.Name)) return path + ": keyword duplicado '" + property.Name + "'";
        var shapeError = ValidateSchemaShape(schema, path);
        if (shapeError is not null) return shapeError;
        var types = ReadStringOrStringArray(schema, "type", out _);
        if (types is not null && types.Any(type => type is not ("object" or "array" or "string" or "integer"
            or "number" or "boolean" or "null")))
            return path + ": type contiene un nombre desconocido";
        if (schema.TryGetProperty("minimum", out var minimum)
            && schema.TryGetProperty("maximum", out var maximum)
            && CompareNumbers(minimum, maximum) is { } comparison && comparison > 0)
            return path + ": minimum no puede ser mayor que maximum";
        if (schema.TryGetProperty("minLength", out var minLength)
            && schema.TryGetProperty("maxLength", out var maxLength)
            && minLength.GetInt32() > maxLength.GetInt32())
            return path + ": minLength no puede ser mayor que maxLength";
        if (schema.TryGetProperty("minItems", out var minItems)
            && schema.TryGetProperty("maxItems", out var maxItems)
            && minItems.GetInt32() > maxItems.GetInt32())
            return path + ": minItems no puede ser mayor que maxItems";

        if (schema.TryGetProperty("properties", out var properties))
        {
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                    return path + ": propiedad de schema duplicada '" + property.Name + "'";
                var error = ValidateSchemaTree(property.Value, path + ".properties." + property.Name, depth + 1);
                if (error is not null) return error;
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            var error = ValidateSchemaTree(items, path + ".items", depth + 1);
            if (error is not null) return error;
        }
        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.Object)
        {
            var error = ValidateSchemaTree(additional, path + ".additionalProperties", depth + 1);
            if (error is not null) return error;
        }
        return null;
    }

    private static IReadOnlyList<string>? ReadStringOrStringArray(JsonElement schema, string key, out string? error)
    {
        error = null;
        if (!schema.TryGetProperty(key, out var type)) return null;
        if (type.ValueKind == JsonValueKind.String) return [type.GetString()!];
        if (type.ValueKind == JsonValueKind.Array && type.GetArrayLength() > 0
            && type.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
            return type.EnumerateArray().Select(item => item.GetString()!).ToArray();
        error = key + " debe ser un nombre de tipo o un array de nombres";
        return null;
    }

    private static bool TypeMatches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static bool TryInt(JsonElement schema, string key, out int value) =>
        schema.TryGetProperty(key, out var element) && element.TryGetInt32(out value) || SetZero(out value);

    private static int? CompareNumbers(JsonElement left, JsonElement right)
    {
        if (right.ValueKind != JsonValueKind.Number) return null;
        if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
            return leftDecimal.CompareTo(rightDecimal);
        if (left.TryGetDouble(out var leftDouble) && double.IsFinite(leftDouble)
            && right.TryGetDouble(out var rightDouble) && double.IsFinite(rightDouble))
            return leftDouble.CompareTo(rightDouble);
        return null;
    }

    private static bool SetZero<T>(out T value)
    {
        value = default!;
        return false;
    }

    private static bool JsonValueEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var leftProperties = left.EnumerateObject().ToArray();
                var rightProperties = right.EnumerateObject().ToArray();
                if (leftProperties.Length != rightProperties.Length) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in leftProperties)
                {
                    if (!seen.Add(property.Name)) return false;
                    var matches = rightProperties.Where(candidate => candidate.Name == property.Name).ToArray();
                    if (matches.Length != 1 || !JsonValueEquals(property.Value, matches[0].Value)) return false;
                }
                return true;
            }
            case JsonValueKind.Array:
            {
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length
                    && leftItems.Zip(rightItems).All(pair => JsonValueEquals(pair.First, pair.Second));
            }
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return CompareNumbers(left, right) is 0;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return false;
        }
    }

    private static bool TryParse(string json, out JsonDocument document, out string error)
    {
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumSchemaDepth + 8 });
            error = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            document = null!;
            error = exception.Message;
            return false;
        }
    }
}
