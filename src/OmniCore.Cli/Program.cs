using OmniCore.Cli;
using OmniCore.Host;

// A stable UTF-8 contract for terminals, redirected output and human stdin.
// Windows' inherited legacy code page otherwise corrupts localized text in pipes.
Console.InputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// Punto de entrada: la composición de un cliente in-process (ADR-0019 §3).
return await CliApp.RunAsync(args);
