using OmniCore.Cli;
using OmniCore.Host;

// A stable UTF-8 contract for terminals, redirected output and human stdin.
// Windows' inherited legacy code page otherwise corrupts localized text in pipes.
Console.InputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// Punto de entrada: la composición de un cliente in-process (ADR-0019 §3).
const string trustedVariable = "OMNICORE_TRUSTED_ACTION_BOOTSTRAP";
var trustedValue = Environment.GetEnvironmentVariable(trustedVariable);
Environment.SetEnvironmentVariable(trustedVariable, null);
byte[]? trustedBootstrap = null;
if (args.Length >= 2 && args[0] == "host" && args[1] == "--stdio" && trustedValue is not null)
{
    try
    {
        var decoded = Convert.FromHexString(trustedValue);
        if (decoded.Length == 32) trustedBootstrap = decoded;
    }
    catch (FormatException) { }
}
try { return await CliApp.RunAsync(args, trustedBootstrap); }
finally
{
    if (trustedBootstrap is not null)
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(trustedBootstrap);
}
