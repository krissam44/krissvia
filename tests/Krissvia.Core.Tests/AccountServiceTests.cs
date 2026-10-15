using Krissvia.Core.Accounts;
using Krissvia.Core.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Krissvia.Core.Tests;

public class AccountServiceTests
{
    private const string MotDePasse = "correct-horse-battery";

    private sealed class MagasinEnMemoire : IConfigurationStore
    {
        public Dictionary<(string, string), string> Documents { get; } = [];

        public Task<IReadOnlyList<ConfigurationDocument>> ListAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigurationDocument>>(
                Documents.Where(d => d.Key.Item1 == collection).Select(d => new ConfigurationDocument(d.Key.Item2, d.Value)).ToList());

        public Task SaveAsync(string collection, ConfigurationDocument document, CancellationToken ct = default)
        {
            Documents[(collection, document.Id)] = document.Json;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct = default) =>
            Task.FromResult(Documents.Remove((collection, id)));
    }

    private readonly MagasinEnMemoire _magasin = new();
    private readonly FakeTimeProvider _horloge = new(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));

    private async Task<AccountService> NeufAsync()
    {
        var service = new AccountService(_magasin, _horloge);
        await service.LoadAsync();
        return service;
    }

    private async Task<AccountService> AvecAdminAsync()
    {
        var service = await NeufAsync();
        Assert.Null(await service.CreateFirstAsync(service.SetupToken, "admin", MotDePasse));
        return service;
    }

    [Fact]
    public async Task Sans_compte_un_jeton_d_installation_est_tire_et_il_disparait_au_premier_compte()
    {
        var service = await NeufAsync();
        Assert.False(service.HasAccounts);
        Assert.NotNull(service.SetupToken);

        Assert.Null(await service.CreateFirstAsync(service.SetupToken, "admin", MotDePasse));

        Assert.Null(service.SetupToken);
        Assert.Equal(["admin"], service.Users);
    }

    [Fact]
    public async Task Le_premier_compte_exige_le_bon_jeton()
    {
        var service = await NeufAsync();

        Assert.NotNull(await service.CreateFirstAsync("0000", "admin", MotDePasse));
        Assert.NotNull(await service.CreateFirstAsync(null, "admin", MotDePasse));
        Assert.False(service.HasAccounts);
    }

    [Fact]
    public async Task Le_jeton_ne_sert_qu_une_fois()
    {
        var service = await NeufAsync();
        var jeton = service.SetupToken;
        Assert.Null(await service.CreateFirstAsync(jeton, "admin", MotDePasse));

        Assert.NotNull(await service.CreateFirstAsync(jeton, "intrus", MotDePasse));
        Assert.Equal(["admin"], service.Users);
    }

    [Fact]
    public async Task Les_comptes_survivent_au_redemarrage_et_aucun_jeton_n_est_alors_tire()
    {
        await AvecAdminAsync();

        var relu = await NeufAsync();

        Assert.Null(relu.SetupToken);
        Assert.Equal(LoginOutcome.Ok, (await relu.VerifyAsync("admin", MotDePasse)).Outcome);
    }

    [Fact]
    public async Task Le_mot_de_passe_n_est_jamais_enregistre_en_clair()
    {
        await AvecAdminAsync();

        var json = Assert.Single(_magasin.Documents).Value;
        Assert.DoesNotContain(MotDePasse, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("admin", "mauvais-mot-de-passe")]
    [InlineData("inconnu", MotDePasse)]
    [InlineData("Admin", MotDePasse)]
    public async Task Une_connexion_fausse_est_refusee(string utilisateur, string motDePasse)
    {
        var service = await AvecAdminAsync();

        Assert.Equal(LoginOutcome.Invalid, (await service.VerifyAsync(utilisateur, motDePasse)).Outcome);
    }

    [Fact]
    public async Task Cinq_echecs_verrouillent_une_minute_meme_le_bon_mot_de_passe()
    {
        var service = await AvecAdminAsync();
        for (var i = 1; i < AccountService.MaxFailures; i++)
            Assert.Equal(LoginOutcome.Invalid, (await service.VerifyAsync("admin", "faux")).Outcome);

        var cinquieme = await service.VerifyAsync("admin", "faux");
        Assert.Equal(LoginOutcome.Locked, cinquieme.Outcome);
        Assert.Equal(60, cinquieme.LockedSeconds);

        _horloge.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(LoginOutcome.Locked, (await service.VerifyAsync("admin", MotDePasse)).Outcome);

        _horloge.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(LoginOutcome.Ok, (await service.VerifyAsync("admin", MotDePasse)).Outcome);
    }

    [Fact]
    public async Task Le_compteur_d_echecs_est_global_et_ne_depend_pas_de_l_identifiant()
    {
        var service = await AvecAdminAsync();
        for (var i = 0; i < AccountService.MaxFailures; i++) await service.VerifyAsync($"invente{i}", "faux");

        Assert.Equal(LoginOutcome.Locked, (await service.VerifyAsync("admin", MotDePasse)).Outcome);
    }

    [Fact]
    public async Task Apres_le_verrou_le_premier_echec_ne_reverrouille_pas()
    {
        var service = await AvecAdminAsync();
        for (var i = 0; i < AccountService.MaxFailures; i++) await service.VerifyAsync("admin", "faux");
        _horloge.Advance(AccountService.LockDuration);

        Assert.Equal(LoginOutcome.Invalid, (await service.VerifyAsync("admin", "faux")).Outcome);
    }

    [Fact]
    public async Task Une_connexion_reussie_remet_le_compteur_a_zero()
    {
        var service = await AvecAdminAsync();
        for (var i = 1; i < AccountService.MaxFailures; i++) await service.VerifyAsync("admin", "faux");
        Assert.Equal(LoginOutcome.Ok, (await service.VerifyAsync("admin", MotDePasse)).Outcome);

        Assert.Equal(LoginOutcome.Invalid, (await service.VerifyAsync("admin", "faux")).Outcome);
    }

    [Theory]
    [InlineData("a", MotDePasse)]
    [InlineData("Admin", MotDePasse)]
    [InlineData("avec espace", MotDePasse)]
    [InlineData("marie", "court")]
    [InlineData("marie", "123456789")]
    public async Task Identifiant_et_mot_de_passe_sont_controles(string utilisateur, string motDePasse)
    {
        var service = await AvecAdminAsync();

        Assert.NotNull(await service.CreateAsync(utilisateur, motDePasse));
        Assert.Equal(["admin"], service.Users);
    }

    [Fact]
    public async Task Un_compte_existant_n_est_pas_ecrase()
    {
        var service = await AvecAdminAsync();

        Assert.NotNull(await service.CreateAsync("admin", "un-autre-mot-de-passe"));
        Assert.Equal(LoginOutcome.Ok, (await service.VerifyAsync("admin", MotDePasse)).Outcome);
    }

    [Fact]
    public async Task Changer_son_mot_de_passe_exige_l_actuel()
    {
        var service = await AvecAdminAsync();

        Assert.NotNull(await service.ChangePasswordAsync("admin", "faux", "nouveau-mot-de-passe"));
        Assert.Null(await service.ChangePasswordAsync("admin", MotDePasse, "nouveau-mot-de-passe"));

        Assert.Equal(LoginOutcome.Invalid, (await service.VerifyAsync("admin", MotDePasse)).Outcome);
        Assert.Equal(LoginOutcome.Ok, (await service.VerifyAsync("admin", "nouveau-mot-de-passe")).Outcome);
    }

    [Fact]
    public async Task On_ne_supprime_pas_son_propre_compte()
    {
        var service = await AvecAdminAsync();
        Assert.Null(await service.CreateAsync("marie", MotDePasse));

        Assert.NotNull(await service.DeleteAsync("admin", "admin"));
        Assert.Equal(["admin", "marie"], service.Users);
    }

    [Fact]
    public async Task On_ne_supprime_pas_le_dernier_compte()
    {
        var service = await AvecAdminAsync();
        Assert.Null(await service.CreateAsync("marie", MotDePasse));
        Assert.Null(await service.DeleteAsync("marie", "admin"));

        Assert.NotNull(await service.DeleteAsync("admin", "quelqu-un"));
        Assert.Equal(["admin"], service.Users);
        Assert.Single(_magasin.Documents);
    }
}
