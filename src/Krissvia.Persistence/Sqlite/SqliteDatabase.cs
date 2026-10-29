using Dapper;
using Microsoft.Data.Sqlite;

namespace Krissvia.Persistence.Sqlite;

/// <summary>
/// Accès SQLite. Une connexion par opération (Microsoft.Data.Sqlite gère le pool) : aucun verrou
/// applicatif, donc aucun bail à ne pas imbriquer comme dans DomoMud.
/// </summary>
public sealed class SqliteDatabase(string connectionString)
{
    private const int VersionSchema = 7;

    /// <remarks>
    /// <c>synchronous = FULL</c>, pas NORMAL, et sans réglage pour le changer : NORMAL n'est sûr en WAL que si chaque
    /// <c>fsync</c> atteint vraiment le support. Le SSD du Pi est derrière un pont USB-SATA qui annonce un cache
    /// d'écriture sans FUA ; ces ponts acquittent des vidages qu'ils ne font pas, et la corruption de la base de DomoMud
    /// sur ce Pi (02/10/2026) portait la signature d'écritures perdues ou réordonnées. Au rythme d'écriture de Krissvia
    /// (des lots toutes les 30 s), FULL ne coûte rien de mesurable. Le réglage vaut par connexion : il est posé à chaque
    /// ouverture.
    /// </remarks>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connexion = new SqliteConnection(connectionString);
        await connexion.OpenAsync(ct);
        await connexion.ExecuteAsync("PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;");
        return connexion;
    }

    /// <summary>Crée ou met à jour le schéma (suivi par PRAGMA user_version). Idempotent.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connexion = await OpenAsync(ct);
        await connexion.ExecuteAsync("PRAGMA journal_mode = WAL;");

        var version = await connexion.ExecuteScalarAsync<long>("PRAGMA user_version;");
        if (version >= VersionSchema) return;

        await using var transaction = await connexion.BeginTransactionAsync(ct);
        // Une étape par version : une base existante n'applique que ce qui lui manque.
        if (version < 1) await connexion.ExecuteAsync(SchemaV1, transaction: transaction);
        if (version < 2) await connexion.ExecuteAsync(SchemaV2, transaction: transaction);
        if (version < 3) await connexion.ExecuteAsync(SchemaV3, transaction: transaction);
        if (version < 4) await connexion.ExecuteAsync(SchemaV4, transaction: transaction);
        if (version < 5) await connexion.ExecuteAsync(SchemaV5, transaction: transaction);
        if (version < 6) await connexion.ExecuteAsync(SchemaV6, transaction: transaction);
        if (version < 7) await connexion.ExecuteAsync(SchemaV7, transaction: transaction);
        await connexion.ExecuteAsync($"PRAGMA user_version = {VersionSchema};", transaction: transaction);
        await transaction.CommitAsync(ct);
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS devices (
            device_id      TEXT PRIMARY KEY,
            integration_id TEXT NOT NULL,
            unique_id      TEXT NOT NULL,
            display_name   TEXT NOT NULL,
            manufacturer   TEXT,
            model          TEXT,
            room           TEXT,
            extra_json     TEXT,
            UNIQUE (integration_id, unique_id)
        );

        CREATE TABLE IF NOT EXISTS entities (
            entity_id      TEXT PRIMARY KEY,
            device_id      TEXT NOT NULL REFERENCES devices(device_id),
            integration_id TEXT NOT NULL,
            unique_id      TEXT NOT NULL,
            kind           TEXT NOT NULL,
            display_name   TEXT NOT NULL,
            device_class   TEXT,
            unit           TEXT,
            state_class    TEXT,
            commands_json  TEXT NOT NULL DEFAULT '[]',
            extra_json     TEXT,
            usage          TEXT,
            enabled        INTEGER NOT NULL DEFAULT 1,
            missing        INTEGER NOT NULL DEFAULT 0,
            UNIQUE (integration_id, unique_id)
        );

        CREATE TABLE IF NOT EXISTS entity_aliases (
            alias     TEXT PRIMARY KEY,
            entity_id TEXT NOT NULL REFERENCES entities(entity_id)
        );
        """;

    /// <summary>v2 : dernier état de chaque entité (magasin d'états). Pas de clé étrangère : un état peut précéder
    /// l'enregistrement de son entité, et une entité absente du registre ne doit pas faire échouer une écriture.</summary>
    private const string SchemaV2 = """
        CREATE TABLE IF NOT EXISTS entity_states (
            entity_id         TEXT PRIMARY KEY,
            value_json        TEXT NOT NULL,
            measured_at_ticks INTEGER NOT NULL
        );
        """;

    /// <summary>v3 : documents de configuration des modules (thermostats…), un JSON par élément.</summary>
    private const string SchemaV3 = """
        CREATE TABLE IF NOT EXISTS config_documents (
            collection TEXT NOT NULL,
            id         TEXT NOT NULL,
            json       TEXT NOT NULL,
            PRIMARY KEY (collection, id)
        );
        """;

    /// <summary>v4 : puissance déclarée par l'utilisateur pour chaque entité (W), facultative.</summary>
    // Widget choisi pour une entité (champ de l'utilisateur, WIDGETS.md §6).
    private const string SchemaV5 = """
        ALTER TABLE entities ADD COLUMN widget TEXT;
        """;

    /// <summary>
    /// v6 : historique. Points bruts (au plus un par minute et par série, gardés 30 jours) et heures consolidées
    /// (gardées sans limite). WITHOUT ROWID : la clé primaire est l'ordre de lecture, une courbe se lit d'un trait.
    /// </summary>
    private const string SchemaV6 = """
        CREATE TABLE IF NOT EXISTS history_series (
            id  INTEGER PRIMARY KEY,
            key TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS history_points (
            series_id INTEGER NOT NULL,
            ts        INTEGER NOT NULL,
            value     REAL,
            PRIMARY KEY (series_id, ts)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS history_points_ts ON history_points (ts);

        CREATE TABLE IF NOT EXISTS history_hourly (
            series_id INTEGER NOT NULL,
            hour      INTEGER NOT NULL,
            min       REAL,
            max       REAL,
            avg       REAL,
            last      REAL,
            coverage  REAL NOT NULL,
            PRIMARY KEY (series_id, hour)
        ) WITHOUT ROWID;
        """;

    /// <summary>
    /// v7 : le dernier nom proposé par le plugin, que le nom affiché suit tant qu'il lui est égal. Laissé vide pour
    /// l'existant : rien ne distingue un nom choisi par l'utilisateur d'un nom reçu du plugin.
    /// </summary>
    private const string SchemaV7 = """
        ALTER TABLE devices ADD COLUMN proposed_name TEXT;
        ALTER TABLE entities ADD COLUMN proposed_name TEXT;
        """;

    private const string SchemaV4 = """
        ALTER TABLE entities ADD COLUMN nominal_power REAL;
        """;
}
