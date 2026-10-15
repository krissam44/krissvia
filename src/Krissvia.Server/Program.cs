using System.Text.Json;
using Krissvia.Climate;
using Krissvia.Climate.Thermostats;
using Krissvia.Contracts;
using Krissvia.Core.Configuration;
using Krissvia.Core.Commands;
using Krissvia.Core.Events;
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

// Bus d'événements : tout ce qui change dans le cœur y passe (états, registre, plugins, ordres).
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<EventBus>>();
    return new EventBus((abonne, ex) => logger.LogError(ex, "L'abonné {Abonne} du bus d'événements a échoué.", abonne));
});
builder.Services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());
builder.Services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<EventBus>());

builder.Services.AddSingleton(new SqliteDatabase($"Data Source={cheminBase}"));
builder.Services.AddSingleton<IEntityRepository, SqliteEntityRepository>();
builder.Services.AddSingleton<IEntityRegistry, EntityRegistry>();
builder.Services.AddSingleton<IEntityStateRepository, SqliteEntityStateRepository>();
builder.Services.AddSingleton<EntityStateStore>();
builder.Services.AddSingleton<IEntityStateReader>(sp => sp.GetRequiredService<EntityStateStore>());
builder.Services.AddSingleton<IEntityStateWriter>(sp => sp.GetRequiredService<EntityStateStore>());

builder.Services.AddSingleton(builder.Configuration.GetSection("PluginHost").Get<PluginHostOptions>() ?? new PluginHostOptions());
builder.Services.AddSingleton<PluginHub>();

// Bus de commandes : passage unique de tous les ordres ; le PluginHost n'en est que le transport.
builder.Services.AddSingleton<ICommandDispatcher, PluginCommander>();
builder.Services.AddSingleton<CommandBus>();
builder.Services.AddSingleton<ICommandBus>(sp => sp.GetRequiredService<CommandBus>());
builder.Services.AddSingleton<IEntityCommander>(sp => sp.GetRequiredService<CommandBus>());

// Thermostats : configuration en base, moteur, et l'inhibiteur que partageront délestage et protections.
builder.Services.AddSingleton<IConfigurationStore, SqliteConfigurationStore>();
builder.Services.AddSingleton(sp => new ThermostatInhibitor(sp.GetRequiredService<ICommandBus>()));
builder.Services.AddSingleton<ThermostatEngine>();
builder.Services.AddSingleton<ThermostatService>();

// Ordre de démarrage : schéma et états d'abord (les hosted services démarrent avant que Kestrel accepte un
// plugin), puis la configuration des thermostats, qui lit la base.
builder.Services.AddHostedService<StateStoreLifetime>();
builder.Services.AddHostedService<CommandJournal>();
builder.Services.AddHostedService<ThermostatLoop>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => "Krissvia");

// API publique (premier morceau du contrat 3) : protégée par la clé d'API.
var api = app.MapGroup("/api").AddEndpointFilter<ApiKeyFilter>();
api.MapThermostatApi();

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
    app.MapGet("/debug/states", async (IEntityRegistry registre, EntityStateStore etats, PluginHub hub, EventBus bus) => new
    {
        plugins = hub.ConnectedPlugins,
        bus = bus.Diagnostics().Select(d => new { d.Subscriber, d.Pending }),
        entities = (await registre.ListAsync()).Select(e => new
        {
            e.EntityId,
            e.IntegrationId,
            e.Missing,
            e.Commands,
            State = etats.Get(e.EntityId) is { } s ? new { s.Value, s.MeasuredAt, s.Available } : null,
        }),
    });

    app.MapGet("/debug/commands", (ICommandBus commandes) => commandes.RecentCommands.Select(c => new
    {
        c.Command.EntityId,
        c.Command.Command,
        c.Command.Origin,
        Priority = c.Command.Priority.ToString(),
        Outcome = c.Outcome.ToString(),
        Verification = c.Verification.ToString(),
        c.Detail,
        c.SentAt,
        DurationMs = (int)c.Duration.TotalMilliseconds,
    }));

    // Envoi manuel d'un ordre, vérifié : {"entity_id": "switch.…", "command": "turn_off", "args": {…}}.
    app.MapPost("/debug/commands", async (OrdreManuel ordre, ICommandBus commandes, CancellationToken ct) =>
    {
        var resultat = await commandes.SendVerifiedAsync(
            new EntityCommand(ordre.EntityId, ordre.Command) { Args = ordre.Args, Origin = "debug" }, ct: ct);
        return new { Outcome = resultat.Outcome.ToString(), Verification = resultat.Verification.ToString() };
    });
}

app.Run();

/// <summary>Corps de POST /debug/commands.</summary>
internal sealed record OrdreManuel(
    [property: System.Text.Json.Serialization.JsonPropertyName("entity_id")] string EntityId,
    string Command,
    JsonElement? Args);
