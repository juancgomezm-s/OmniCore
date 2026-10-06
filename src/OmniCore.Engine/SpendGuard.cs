namespace OmniCore.Engine;

using OmniCore.Domain;

/// <summary>
/// Guardia de gasto de un Run/Task (ADR-0037 §7): vigila turnos, tool-calls, tokens y costo
/// USD contra el TaskBudget. Puro y determinista; el engine pregunta antes de avanzar un paso.
/// Cuando se supera un tope, levanta una excepción tipada que el engine convierte en la
/// interacción BudgetExceeded (spec §34, ADR-0034). Los topes por sesión/día (5/20 USD) los
/// impone la capa de presupuesto del host sumando los recursos del guard.
/// </summary>
public sealed class SpendGuard
{
    private readonly TaskBudget _budget;

    private int _turns;

    private int _toolCalls;

    private long _tokens;

    private decimal _costUsd;

    public SpendGuard(TaskBudget budget)
    {
        _budget = budget is null ? new TaskBudget(null, null, null, null) : budget;
    }

    /// <summary>Avanza un turno más; lanza si excede el tope de turnos.</summary>
    public void AdvanceTurn(long turnTokens)
    {
        var turns = checked(_turns + 1);
        var tokens = checked(_tokens + Math.Max(0, turnTokens));
        _turns = turns;
        _tokens = tokens;
        if (_budget.MaxTurns is not null && _turns > _budget.MaxTurns!)
        {
            throw new BudgetExceededException("límite de turnos: " + _budget.MaxTurns);
        }

        if (_budget.MaxTokens is not null && _tokens > _budget.MaxTokens!)
        {
            throw new BudgetExceededException("límite de tokens: " + _budget.MaxTokens);
        }
    }

    /// <summary>Registra una tool call; lanza si excede el tope de tool-calls.</summary>
    public void RecordToolCall()
    {
        _toolCalls = checked(_toolCalls + 1);
        if (_budget.MaxToolCalls is not null && _toolCalls > _budget.MaxToolCalls!)
        {
            throw new BudgetExceededException("límite de tool calls: " + _budget.MaxToolCalls);
        }
    }

    /// <summary>
    /// Acumula costo estimado (USD) de un paso; lanza si supera MaxCostUsd (ADR-0037 §7).
    /// Es la integración del tope monetario que la guardia aislada no tenía (P1-11).
    /// </summary>
    public void AddCostUsd(decimal usd)
    {
        if (usd < 0) throw new ArgumentOutOfRangeException(nameof(usd), "Cost cannot be negative.");
        _costUsd += usd;
        if (_budget.MaxCostUsd is not null && _costUsd > _budget.MaxCostUsd!)
        {
            throw new BudgetExceededException(
                "límite de costo: $" + _budget.MaxCostUsd + " (gastado $" + _costUsd + ")");
        }
    }

    public int Turns() => _turns;

    public int ToolCalls() => _toolCalls;

    public long Tokens() => _tokens;

    public decimal CostUsd() => _costUsd;
}

/// <summary>El gasto del Run/Task superó un tope del presupuesto (ADR-0037 §7).</summary>
public sealed class BudgetExceededException : InvalidOperationException
{
    public string Detail { get; }

    public BudgetContinuationOffer? Continuation { get; }

    public BudgetExceededException(string detail, BudgetContinuationOffer? continuation = null)
    {
        Detail = detail;
        Continuation = continuation;
    }
}
