using Lumen.Engine;

var app = EngineHost.Build(EngineOptions.FromArgs(args));
await app.RunAsync().ConfigureAwait(false);
