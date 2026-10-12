using Wodra.Multiplayer.Server;

var urls = Environment.GetEnvironmentVariable("WODRA_MULTIPLAYER_URLS") ?? "http://0.0.0.0:8787";
var app = MultiplayerServer.Create(args, urls);
await app.RunAsync();
