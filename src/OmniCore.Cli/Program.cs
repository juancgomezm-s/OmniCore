using OmniCore.Cli;
using OmniCore.Host;

// Punto de entrada: la composición de un cliente in-process (ADR-0019 §3).
return await CliApp.RunAsync(args);