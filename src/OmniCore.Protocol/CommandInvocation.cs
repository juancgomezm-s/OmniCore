namespace OmniCore.Protocol;

/// <summary>Invocation estructurada de un command (ADR-0024). El Engine nunca recibe el texto /crudo.</summary>
public sealed record CommandInvocation(string Name, IReadOnlyList<string> Arguments, string InvocationOrigin);

/// <summary>Resultado de expandir un PromptCommand en input ordinario del runtime.</summary>
public sealed record PromptExpanded(string Text, string Origin);

/// <summary>Outcome tipado cuando una interacción requiere una decisión humana que no está disponible.</summary>
public sealed record InputRequiredOutcome(string InteractionId, string Kind);

/// <summary>DTO wire de CommandInvocation, compatible con net8.</summary>
public static class CommandInvocationJson
{
    public static string Encode(CommandInvocation invocation)
    {
        var args = "[" + string.Join(",", invocation.Arguments.Select(JsonString)) + "]";
        return "{" + JsonObj.Field("cmd", "command.invoke") + ","
            + JsonObj.Field("name", invocation.Name) + ","
            + JsonObj.FieldRaw("arguments", args) + ","
            + JsonObj.Field("origin", invocation.InvocationOrigin) + "}";
    }

    private static string JsonString(string value) => "\"" + JsonObj.Escape(value) + "\"";
}
