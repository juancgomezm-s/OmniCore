// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Xunit;
using OmniCore.Models;

namespace OmniCore.Tests;

public class RateLimitQuotaParserTests
{
    private static readonly DateTimeOffset FixedNow = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static IEnumerable<KeyValuePair<string, IEnumerable<string>>> Headers(params (string Key, string Value)[] pairs)
        => pairs.Select(p => new KeyValuePair<string, IEnumerable<string>>(p.Key, new[] { p.Value }));

    private static IEnumerable<KeyValuePair<string, IEnumerable<string>>> HeadersMulti(params (string Key, string[] Values)[] pairs)
        => pairs.Select(p => new KeyValuePair<string, IEnumerable<string>>(p.Key, (IEnumerable<string>)p.Values));

    // ===== Anthropic windows =====

    [Fact]
    public void Parse_AnthropicRequestsWindow_FullHeaders_ParsesAllFields()
    {
        var headers = Headers(
            ("anthropic-ratelimit-requests-limit", "1000"),
            ("anthropic-ratelimit-requests-remaining", "850"),
            ("anthropic-ratelimit-requests-reset", "2026-09-30T13:00:00Z"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.Requests, window.Kind);
        Assert.Equal(1000L, window.Limit);
        Assert.Equal(850L, window.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 13, 0, 0, TimeSpan.Zero), window.ResetsAt);
    }

    [Fact]
    public void Parse_AnthropicTokensWindow_FullHeaders_ParsesAllFields()
    {
        var headers = Headers(
            ("anthropic-ratelimit-tokens-limit", "50000"),
            ("anthropic-ratelimit-tokens-remaining", "42000"),
            ("anthropic-ratelimit-tokens-reset", "2026-09-30T13:00:00Z"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.Tokens, window.Kind);
        Assert.Equal(50000L, window.Limit);
        Assert.Equal(42000L, window.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 13, 0, 0, TimeSpan.Zero), window.ResetsAt);
    }

    [Fact]
    public void Parse_AnthropicInputTokensWindow_FullHeaders_ParsesAllFields()
    {
        var headers = Headers(
            ("anthropic-ratelimit-input-tokens-limit", "30000"),
            ("anthropic-ratelimit-input-tokens-remaining", "25000"),
            ("anthropic-ratelimit-input-tokens-reset", "2026-09-30T13:00:00Z"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.InputTokens, window.Kind);
        Assert.Equal(30000L, window.Limit);
        Assert.Equal(25000L, window.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 13, 0, 0, TimeSpan.Zero), window.ResetsAt);
    }

    [Fact]
    public void Parse_AnthropicOutputTokensWindow_FullHeaders_ParsesAllFields()
    {
        var headers = Headers(
            ("anthropic-ratelimit-output-tokens-limit", "20000"),
            ("anthropic-ratelimit-output-tokens-remaining", "17000"),
            ("anthropic-ratelimit-output-tokens-reset", "2026-09-30T13:00:00Z"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.OutputTokens, window.Kind);
        Assert.Equal(20000L, window.Limit);
        Assert.Equal(17000L, window.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 13, 0, 0, TimeSpan.Zero), window.ResetsAt);
    }

    // ===== OpenAI windows =====

    [Fact]
    public void Parse_OpenAIRequestsWindow_FullHeaders_ParsesAllFields()
    {
        var headers = Headers(
            ("x-ratelimit-limit-requests", "500"),
            ("x-ratelimit-remaining-requests", "420"),
            ("x-ratelimit-reset-requests", "6m0s"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.Requests, window.Kind);
        Assert.Equal(500L, window.Limit);
        Assert.Equal(420L, window.Remaining);
        Assert.Equal(FixedNow.AddMinutes(6), window.ResetsAt);
    }

    [Fact]
    public void Parse_OpenAITokensWindow_FullHeaders_ParsesAllFields()
    {
        var headers = Headers(
            ("x-ratelimit-limit-tokens", "200000"),
            ("x-ratelimit-remaining-tokens", "150000"),
            ("x-ratelimit-reset-tokens", "2h3m"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.Tokens, window.Kind);
        Assert.Equal(200000L, window.Limit);
        Assert.Equal(150000L, window.Remaining);
        Assert.Equal(FixedNow.AddHours(2).AddMinutes(3), window.ResetsAt);
    }

    // ===== Duration formats (OpenAI) =====

    [Fact]
    public void Parse_OpenAIDuration_Milliseconds_ParsesCorrectly()
    {
        var headers = Headers(
            ("x-ratelimit-limit-requests", "100"),
            ("x-ratelimit-remaining-requests", "50"),
            ("x-ratelimit-reset-requests", "500ms"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(FixedNow.AddMilliseconds(500), window.ResetsAt);
    }

    [Fact]
    public void Parse_OpenAIDuration_Seconds_ParsesCorrectly()
    {
        var headers = Headers(
            ("x-ratelimit-limit-requests", "100"),
            ("x-ratelimit-remaining-requests", "50"),
            ("x-ratelimit-reset-requests", "30s"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(FixedNow.AddSeconds(30), window.ResetsAt);
    }

    [Fact]
    public void Parse_OpenAIDuration_Minutes_ParsesCorrectly()
    {
        var headers = Headers(
            ("x-ratelimit-limit-requests", "100"),
            ("x-ratelimit-remaining-requests", "50"),
            ("x-ratelimit-reset-requests", "5m"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(FixedNow.AddMinutes(5), window.ResetsAt);
    }

    [Fact]
    public void Parse_OpenAIDuration_Hours_ParsesCorrectly()
    {
        var headers = Headers(
            ("x-ratelimit-limit-requests", "100"),
            ("x-ratelimit-remaining-requests", "50"),
            ("x-ratelimit-reset-requests", "2h"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(FixedNow.AddHours(2), window.ResetsAt);
    }

    [Fact]
    public void Parse_OpenAIDuration_Combined_ParsesCorrectly()
    {
        var headers = Headers(
            ("x-ratelimit-limit-requests", "100"),
            ("x-ratelimit-remaining-requests", "50"),
            ("x-ratelimit-reset-requests", "1h30m45s100ms"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        var expected = FixedNow.AddHours(1).AddMinutes(30).AddSeconds(45).AddMilliseconds(100);
        Assert.Equal(expected, window.ResetsAt);
    }

    // ===== Case insensitivity =====

    [Fact]
    public void Parse_CaseInsensitiveHeaders_ParsesCorrectly()
    {
        var headers = Headers(
            ("ANTHROPIC-RATELIMIT-REQUESTS-LIMIT", "200"),
            ("AnThRoPiC-RaTeLiMiT-Requests-Remaining", "150"),
            ("anthropic-ratelimit-requests-reset", "2026-09-30T13:00:00Z"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.Requests, window.Kind);
        Assert.Equal(200L, window.Limit);
        Assert.Equal(150L, window.Remaining);
    }

    [Fact]
    public void Parse_OpenAICaseInsensitiveHeaders_ParsesCorrectly()
    {
        var headers = Headers(
            ("X-RATELIMIT-LIMIT-REQUESTS", "300"),
            ("x-ratelimit-remaining-requests", "200"),
            ("X-RateLimit-Reset-Requests", "10m"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.Requests, window.Kind);
        Assert.Equal(300L, window.Limit);
        Assert.Equal(200L, window.Remaining);
        Assert.Equal(FixedNow.AddMinutes(10), window.ResetsAt);
    }

    // ===== Garbage values ignored =====

    [Fact]
    public void Parse_GarbageLimitValue_IgnoredButWindowCreatedFromRemaining()
    {
        var headers = Headers(
            ("anthropic-ratelimit-requests-limit", "not-a-number"),
            ("anthropic-ratelimit-requests-remaining", "75"),
            ("anthropic-ratelimit-requests-reset", "2026-09-30T13:00:00Z"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Null(window.Limit);
        Assert.Equal(75L, window.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 13, 0, 0, TimeSpan.Zero), window.ResetsAt);
    }

    [Fact]
    public void Parse_GarbageRemainingValue_IgnoredButWindowCreatedFromLimit()
    {
        var headers = Headers(
            ("anthropic-ratelimit-requests-limit", "500"),
            ("anthropic-ratelimit-requests-remaining", "invalid"),
            ("anthropic-ratelimit-requests-reset", "2026-09-30T13:00:00Z"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(500L, window.Limit);
        Assert.Null(window.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 13, 0, 0, TimeSpan.Zero), window.ResetsAt);
    }

    [Fact]
    public void Parse_GarbageResetValue_IgnoredButWindowCreated()
    {
        var headers = Headers(
            ("anthropic-ratelimit-requests-limit", "500"),
            ("anthropic-ratelimit-requests-remaining", "400"),
            ("anthropic-ratelimit-requests-reset", "not-a-date"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(500L, window.Limit);
        Assert.Equal(400L, window.Remaining);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void Parse_OpenAIGarbageDuration_IgnoredButWindowCreated()
    {
        var headers = Headers(
            ("x-ratelimit-limit-requests", "500"),
            ("x-ratelimit-remaining-requests", "400"),
            ("x-ratelimit-reset-requests", "not-a-duration"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(500L, window.Limit);
        Assert.Equal(400L, window.Remaining);
        Assert.Null(window.ResetsAt);
    }

    // ===== Missing headers =====

    [Fact]
    public void Parse_EmptyHeaders_ReturnsEmptyList()
    {
        var headers = Headers();

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_OnlyRemainingHeader_CreatesWindowWithNullLimit()
    {
        var headers = Headers(
            ("anthropic-ratelimit-requests-remaining", "123"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.Requests, window.Kind);
        Assert.Null(window.Limit);
        Assert.Equal(123L, window.Remaining);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void Parse_OnlyLimitHeader_CreatesWindowWithNullRemaining()
    {
        var headers = Headers(
            ("x-ratelimit-limit-tokens", "10000"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(RateLimitWindowKind.Tokens, window.Kind);
        Assert.Equal(10000L, window.Limit);
        Assert.Null(window.Remaining);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void Parse_MultipleWindowsFromBothProviders_AllParsed()
    {
        var headers = Headers(
            ("anthropic-ratelimit-requests-limit", "1000"),
            ("anthropic-ratelimit-requests-remaining", "800"),
            ("anthropic-ratelimit-requests-reset", "2026-09-30T13:00:00Z"),
            ("x-ratelimit-limit-tokens", "50000"),
            ("x-ratelimit-remaining-tokens", "40000"),
            ("x-ratelimit-reset-tokens", "30m"));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        Assert.Equal(2, result.Count);

        var anthropic = result.First(w => w.Kind == RateLimitWindowKind.Requests);
        Assert.Equal(1000L, anthropic.Limit);
        Assert.Equal(800L, anthropic.Remaining);

        var openai = result.First(w => w.Kind == RateLimitWindowKind.Tokens);
        Assert.Equal(50000L, openai.Limit);
        Assert.Equal(40000L, openai.Remaining);
        Assert.Equal(FixedNow.AddMinutes(30), openai.ResetsAt);
    }

    [Fact]
    public void Parse_HeadersWithMultipleValues_UsesFirstValue()
    {
        var headers = HeadersMulti(
            ("anthropic-ratelimit-requests-limit", new[] { "100", "200" }),
            ("anthropic-ratelimit-requests-remaining", new[] { "50", "60" }));

        var result = RateLimitQuotaParser.Parse(headers, FixedNow);

        var window = Assert.Single(result);
        Assert.Equal(100L, window.Limit);
        Assert.Equal(50L, window.Remaining);
    }
}