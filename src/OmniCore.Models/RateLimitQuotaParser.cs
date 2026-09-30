// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniCore.Models;

/// <summary>
/// Kind of rate-limit window as reported by the provider.
/// </summary>
public enum RateLimitWindowKind
{
    /// <summary>Request-count window.</summary>
    Requests,

    /// <summary>Total token window (combined input + output).</summary>
    Tokens,

    /// <summary>Input-token window.</summary>
    InputTokens,

    /// <summary>Output-token window.</summary>
    OutputTokens,
}

/// <summary>
/// A single parsed rate-limit window.
/// </summary>
/// <param name="Kind">The kind of window.</param>
/// <param name="Limit">The limit, if reported.</param>
/// <param name="Remaining">The remaining amount, if reported.</param>
/// <param name="ResetsAt">The reset timestamp, if reported/parsable.</param>
public sealed record RateLimitWindow(
    RateLimitWindowKind Kind,
    long? Limit,
    long? Remaining,
    DateTimeOffset? ResetsAt);

/// <summary>
/// Pure parser for provider rate-limit headers into <see cref="RateLimitWindow"/> instances.
/// No I/O, no external dependencies.
/// </summary>
public static class RateLimitQuotaParser
{
    /// <summary>
    /// Parses rate-limit headers into a list of quota windows.
    /// </summary>
    /// <param name="headers">Headers as key-value pairs (case-insensitive keys, multiple values allowed).</param>
    /// <param name="now">Current time, used to resolve relative reset durations (e.g., OpenAI).</param>
    /// <returns>List of parsed windows. A window appears only if at least Limit or Remaining was parsed.
    /// Unparseable values are treated as null (never guessed). Empty input yields empty list.</returns>
    public static IReadOnlyList<RateLimitWindow> Parse(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        DateTimeOffset now)
    {
        var dict = headers
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(kvp => kvp.Value).FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var windows = new List<RateLimitWindow>();

        // Anthropic headers
        TryAddAnthropicWindow(dict, RateLimitWindowKind.Requests,
            "anthropic-ratelimit-requests-limit",
            "anthropic-ratelimit-requests-remaining",
            "anthropic-ratelimit-requests-reset",
            windows, now);

        TryAddAnthropicWindow(dict, RateLimitWindowKind.Tokens,
            "anthropic-ratelimit-tokens-limit",
            "anthropic-ratelimit-tokens-remaining",
            "anthropic-ratelimit-tokens-reset",
            windows, now);

        TryAddAnthropicWindow(dict, RateLimitWindowKind.InputTokens,
            "anthropic-ratelimit-input-tokens-limit",
            "anthropic-ratelimit-input-tokens-remaining",
            "anthropic-ratelimit-input-tokens-reset",
            windows, now);

        TryAddAnthropicWindow(dict, RateLimitWindowKind.OutputTokens,
            "anthropic-ratelimit-output-tokens-limit",
            "anthropic-ratelimit-output-tokens-remaining",
            "anthropic-ratelimit-output-tokens-reset",
            windows, now);

        // OpenAI headers
        TryAddOpenAIWindow(dict, RateLimitWindowKind.Requests,
            "x-ratelimit-limit-requests",
            "x-ratelimit-remaining-requests",
            "x-ratelimit-reset-requests",
            windows, now);

        TryAddOpenAIWindow(dict, RateLimitWindowKind.Tokens,
            "x-ratelimit-limit-tokens",
            "x-ratelimit-remaining-tokens",
            "x-ratelimit-reset-tokens",
            windows, now);

        return windows;
    }

    private static void TryAddAnthropicWindow(
        IReadOnlyDictionary<string, string> dict,
        RateLimitWindowKind kind,
        string limitKey,
        string remainingKey,
        string resetKey,
        List<RateLimitWindow> windows,
        DateTimeOffset now)
    {
        long? limit = null;
        long? remaining = null;
        DateTimeOffset? resetAt = null;
        var hasLimit = false;
        var hasRemaining = false;

        if (dict.TryGetValue(limitKey, out var limitStr) && long.TryParse(limitStr, out var parsedLimit))
        {
            limit = parsedLimit;
            hasLimit = true;
        }

        if (dict.TryGetValue(remainingKey, out var remainingStr) && long.TryParse(remainingStr, out var parsedRemaining))
        {
            remaining = parsedRemaining;
            hasRemaining = true;
        }

        if (dict.TryGetValue(resetKey, out var resetStr) && DateTimeOffset.TryParse(resetStr, out var parsedResetAt))
        {
            resetAt = parsedResetAt;
        }

        if (hasLimit || hasRemaining)
        {
            windows.Add(new RateLimitWindow(
                kind,
                limit,
                remaining,
                resetAt));
        }
    }

    private static void TryAddOpenAIWindow(
        IReadOnlyDictionary<string, string> dict,
        RateLimitWindowKind kind,
        string limitKey,
        string remainingKey,
        string resetKey,
        List<RateLimitWindow> windows,
        DateTimeOffset now)
    {
        long? limit = null;
        long? remaining = null;
        DateTimeOffset? resetAt = null;
        var hasLimit = false;
        var hasRemaining = false;

        if (dict.TryGetValue(limitKey, out var limitStr) && long.TryParse(limitStr, out var parsedLimit))
        {
            limit = parsedLimit;
            hasLimit = true;
        }

        if (dict.TryGetValue(remainingKey, out var remainingStr) && long.TryParse(remainingStr, out var parsedRemaining))
        {
            remaining = parsedRemaining;
            hasRemaining = true;
        }

        if (dict.TryGetValue(resetKey, out var resetStr))
        {
            resetAt = ParseOpenAIDuration(resetStr, now);
        }

        if (hasLimit || hasRemaining)
        {
            windows.Add(new RateLimitWindow(
                kind,
                limit,
                remaining,
                resetAt));
        }
    }

    private static DateTimeOffset? ParseOpenAIDuration(string value, DateTimeOffset now)
    {
        // OpenAI reset headers are durations like "6m0s", "1s", "120ms", "2h3m"
        // We parse them as a TimeSpan and add to 'now'.
        // Format: optional hours (h), optional minutes (m), optional seconds (s), optional milliseconds (ms)
        // Each component is a number followed by its unit. Components can appear in any order but typically h, m, s, ms.

        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var span = TimeSpan.Zero;
            var i = 0;
            var len = value.Length;

            while (i < len)
            {
                // Parse number
                var start = i;
                while (i < len && char.IsDigit(value[i]))
                    i++;

                if (start == i)
                    return null; // No digits found

                if (!long.TryParse(value.Substring(start, i - start), out var num))
                    return null;

                // Parse unit
                if (i >= len)
                    return null;

                char unit = char.ToLowerInvariant(value[i]);
                i++;

                // Check for "ms"
                if (unit == 'm' && i < len && char.ToLowerInvariant(value[i]) == 's')
                {
                    unit = 'µ'; // Use a distinct marker for milliseconds
                    i++;
                }

                switch (unit)
                {
                    case 'h':
                        span = span.Add(TimeSpan.FromHours(num));
                        break;
                    case 'm':
                        span = span.Add(TimeSpan.FromMinutes(num));
                        break;
                    case 's':
                        span = span.Add(TimeSpan.FromSeconds(num));
                        break;
                    case 'µ':
                        span = span.Add(TimeSpan.FromMilliseconds(num));
                        break;
                    default:
                        return null;
                }
            }

            return now.Add(span);
        }
        catch
        {
            return null;
        }
    }
}