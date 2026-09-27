using Lumen.Engine;

if (args.Length > 0 && args[0] == "connections")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    return await ConnectionsCommand.RunAsync(args, Console.In, Console.Out).ConfigureAwait(false);
}

var app = EngineHost.Build(EngineOptions.FromArgs(args));
await app.RunAsync().ConfigureAwait(false);
return 0;
