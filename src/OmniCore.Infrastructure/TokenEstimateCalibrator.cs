using System;
using System.Collections.Generic;

namespace OmniCore.Infrastructure;

/// <summary>
/// Calibra la razon caracteres/token por tokenizador usando observaciones empiricas (chars exactos vs tokens exactos).
/// Segun ADR-0042 §1: razon calibrada por familia + margen de seguridad del 10 %.
/// Logica pura, sin I/O, thread-safe.
/// </summary>
public sealed class TokenEstimateCalibrator
{
    private readonly double _defaultCharsPerToken;
    private readonly int _minimumSamples;
    private readonly double _minRatio;
    private readonly double _maxRatio;

    private readonly object _lock = new();
    private readonly Dictionary<string, TokenizerStats> _stats = new(StringComparer.Ordinal);

    /// <summary>
    /// Crea un calibrador con parametros configurables.
    /// </summary>
    /// <param name="defaultCharsPerToken">Razon por defecto (chars/token) antes de tener suficientes muestras. Por defecto 4.0.</param>
    /// <param name="minimumSamples">Minimo de muestras validas por tokenizador antes de usar la razon calibrada. Por defecto 20.</param>
    /// <param name="minRatio">Limite inferior de la razon calibrada (chars/token). Por defecto 1.5.</param>
    /// <param name="maxRatio">Limite superior de la razon calibrada (chars/token). Por defecto 8.0.</param>
    public TokenEstimateCalibrator(
        double defaultCharsPerToken = 4.0,
        int minimumSamples = 20,
        double minRatio = 1.5,
        double maxRatio = 8.0)
    {
        if (defaultCharsPerToken <= 0 || double.IsNaN(defaultCharsPerToken))
            throw new ArgumentOutOfRangeException(nameof(defaultCharsPerToken));
        if (minimumSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        if (minRatio <= 0 || double.IsNaN(minRatio))
            throw new ArgumentOutOfRangeException(nameof(minRatio));
        if (maxRatio <= 0 || double.IsNaN(maxRatio))
            throw new ArgumentOutOfRangeException(nameof(maxRatio));
        if (minRatio > maxRatio)
            throw new ArgumentException("minRatio must be <= maxRatio");

        _defaultCharsPerToken = defaultCharsPerToken;
        _minimumSamples = minimumSamples;
        _minRatio = minRatio;
        _maxRatio = maxRatio;
    }

    /// <summary>
    /// Registra una observacion: caracteres y tokens exactos para un tokenizador.
    /// Ignora muestras con characters &lt;= 0 o exactTokens &lt;= 0.
    /// </summary>
    public void AddSample(string tokenizerId, int characters, int exactTokens)
    {
        if (string.IsNullOrEmpty(tokenizerId))
            throw new ArgumentException("tokenizerId cannot be null or empty", nameof(tokenizerId));
        if (characters <= 0 || exactTokens <= 0)
            return; // Ignorar muestras invalidas silenciosamente

        lock (_lock)
        {
            if (!_stats.TryGetValue(tokenizerId, out var s))
            {
                s = new TokenizerStats();
                _stats[tokenizerId] = s;
            }
            s.TotalCharacters += characters;
            s.TotalExactTokens += exactTokens;
            s.SampleCount++;
        }
    }

    /// <summary>
    /// Devuelve la razon caracteres/token para el tokenizador.
    /// Antes de <c>minimumSamples</c> muestras validas, devuelve <c>defaultCharsPerToken</c>.
    /// Despues, devuelve (total chars / total exact tokens) clampado a [minRatio, maxRatio].
    /// </summary>
    public double CharsPerToken(string tokenizerId)
    {
        if (string.IsNullOrEmpty(tokenizerId))
            throw new ArgumentException("tokenizerId cannot be null or empty", nameof(tokenizerId));

        lock (_lock)
        {
            if (!_stats.TryGetValue(tokenizerId, out var s) || s.SampleCount < _minimumSamples)
            {
                return _defaultCharsPerToken;
            }

            var ratio = (double)s.TotalCharacters / s.TotalExactTokens;
            if (ratio < _minRatio) return _minRatio;
            if (ratio > _maxRatio) return _maxRatio;
            return ratio;
        }
    }

    /// <summary>
    /// Numero de muestras validas registradas para el tokenizador.
    /// </summary>
    public int SampleCount(string tokenizerId)
    {
        if (string.IsNullOrEmpty(tokenizerId))
            throw new ArgumentException("tokenizerId cannot be null or empty", nameof(tokenizerId));

        lock (_lock)
        {
            return _stats.TryGetValue(tokenizerId, out var s) ? s.SampleCount : 0;
        }
    }

    private sealed class TokenizerStats
    {
        public long TotalCharacters;
        public long TotalExactTokens;
        public int SampleCount;
    }
}