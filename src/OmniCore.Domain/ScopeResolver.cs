namespace OmniCore.Domain;

/// <summary>
/// Resuelve la configuración por scope (ADR-0022 §25): el más específico gana, salvo claves
/// locked. M2 implementa la estrategia de configuración.
/// </summary>
public interface IScopeResolver<T>
{
    /// <summary>Devuelve el valor efectivo para un scope dado.</summary>
    T Resolve(ScopeLevel scope, string key, T fallback);
}

/// <summary>
/// ScopeResolver: implementación de IScopeResolver (ADR-0022 §25). Para una clave dada,
/// consulta cada capa de especificidad — BuiltIn (menos específico) → … → Lane (más
/// específico) — y el valor más específico que exista gana.
/// </summary>
public sealed class ScopeResolver<T> : IScopeResolver<T>
{
    private readonly Func<ScopeLevel, string, T?> _lookup;

    private readonly List<ScopeLevel> _chain;

    public ScopeResolver(Func<ScopeLevel, string, T?> lookup)
    {
        _lookup = lookup;
        _chain = new List<ScopeLevel>();
        _chain.Add(ScopeLevel.BuiltIn);
        _chain.Add(ScopeLevel.User);
        _chain.Add(ScopeLevel.Project);
        _chain.Add(ScopeLevel.Workspace);
        _chain.Add(ScopeLevel.Session);
        _chain.Add(ScopeLevel.Run);
        _chain.Add(ScopeLevel.Task);
        _chain.Add(ScopeLevel.Lane);
    }

    /// <summary>
    /// Resuelve el valor efectivo: el más específico que tenga la clave. `fallback` se usa
    /// si ninguna capa la define.
    /// </summary>
    public T Resolve(ScopeLevel scope, string key, T fallback)
    {
        // Sube desde el scope consultado hasta BuiltIn; el primer valor presente gana.
        for (var i = _chain.IndexOf(scope); i >= 0; i--)
        {
            var candidate = _lookup(_chain[i], key);
            if (candidate is not null)
            {
                return candidate!;
            }
        }

        return fallback;
    }

    /// <summary>Scope que provee el valor efectivo (para la traza de override; ADR-0022 §25).</summary>
    public ScopeLevel SourceOf(string key)
    {
        for (var i = _chain.Count - 1; i >= 0; i--)
        {
            var candidate = _lookup(_chain[i], key);
            if (candidate is not null)
            {
                return _chain[i];
            }
        }

        return ScopeLevel.BuiltIn;
    }
}