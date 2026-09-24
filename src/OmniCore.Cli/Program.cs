using OmniCore.Cli;
using OmniCore.Host;

return await OmniHost.Create(args).RunAsync(CliClient.RunAsync);
