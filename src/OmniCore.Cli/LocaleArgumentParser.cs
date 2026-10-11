namespace OmniCore.Cli;

/// <summary>Extracts the CLI-wide locale option without rewriting positional arguments.</summary>
internal static class LocaleArgumentParser
{
    internal sealed record Result(string[] Arguments, string? ExplicitLocale, IReadOnlyList<string> Diagnostics)
    {
        public bool HasMissingLocaleValue => Diagnostics.Contains("missing", StringComparer.Ordinal);
    }

    public static Result Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var forwarded = new List<string>(args.Length);
        var diagnostics = new List<string>();
        string? locale = null;
        var optionsEnded = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (optionsEnded)
            {
                forwarded.Add(argument);
                continue;
            }

            if (argument == "--")
            {
                optionsEnded = true;
                forwarded.Add(argument);
                continue;
            }

            if (argument == "--locale")
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    diagnostics.Add("missing");
                    forwarded.Add(argument);
                    continue;
                }

                locale = args[++index];
                continue;
            }

            if (argument.StartsWith("--locale=", StringComparison.Ordinal))
            {
                locale = argument[9..];
                continue;
            }

            forwarded.Add(argument);
        }

        return new(forwarded.ToArray(), locale, diagnostics);
    }
}
