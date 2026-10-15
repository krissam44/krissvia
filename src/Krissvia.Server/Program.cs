using Krissvia.Core.Commands;
using Krissvia.Core.Registry;
using Krissvia.Core.States;
using Krissvia.Persistence.Sqlite;
using Krissvia.PluginHost;
using Krissvia.Server;

var builder = WebApplication.CreateBuilder(args);

// Réglages propres à la machine (jetons des plugins) : jamais versionnés.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

var cheminBase = Path.GetFullPath(builder.Configuration["Database:Path"] ?? "krissvia.db", builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(cheminBase)!);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new SqliteDatabase($"Data Source={cheminBase}"));
builder.Services.AddSingleton<IEntityRepository, SqliteEntityRepository>();
builder.Services.AddSingleton<IEntityRegistry, EntityRegistry>();
builder.Services.AddSingleton<IEntityStateRepository, SqliteEntityStateRepository>();
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<EntityStateStore>>();
    return new EntityStateStore(
        sp.GetRequiredService<IEntityStateRepository>(),
        sp.GetRequiredService<TimeProvider>(),
        ex => logger.LogError(ex, "Un abonné du magasin d'états a échoué."));
});
builder.Services.AddSingleton<IEntityStateReader>(sp => sp.GetRequiredService<EntityStateStore>());
builder.Services.AddSingleton<IEntityStateWriter>(sp => sp.GetRequiredService<EntityStateStore>());

builder.Services.AddSingleton(builder.Configuration.GetSection("PluginHost").Get<PluginHostOptions>() ?? new PluginHostOptions());
builder.Services.AddSingleton<PluginHub>();
builder.Services.AddSingleton<IEntityCommander, PluginCommander>();

// Schéma, puis restauration des états : les hosted services démarrent avant que Kestrel accepte un plugin.
builder.Services.AddHostedService<StateStoreLifetime>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => "Krissvia");

// Point d'entrée des plugins : voir docs/TRANSPORT.md.
app.Map("/plugins", async (HttpContext contexte, PluginHub hub) =>
{
    if (!contexte.WebSockets.IsWebSocketRequest)
    {
        contexte.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await contexte.WebSockets.AcceptWebSocketAsync();
    await hub.RunAsync(new WebSocketChannel(socket), contexte.RequestAborted);
});

if (app.Environment.IsDevelopment())
{
    // Diagnostic provisoire, en attendant l'API publique (contrat 3) : ce que le cœur sait en ce moment.
    app.MapGet("/debug/states", async (IEntityRegistry registre, EntityStateStore etats, PluginHub hub) => new
    {
        plugins = hub.ConnectedPlugins,
        entities = (await registre.ListAsync()).Select(e => new
        {
            e.EntityId,
            e.IntegrationId,
            e.Missing,
            State = etats.Get(e.EntityId) is { } s ? new { s.Value, s.MeasuredAt, s.Available } : null,
        }),
    });
}

app.Run();
