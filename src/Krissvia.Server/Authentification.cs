using System.Security.Claims;
using Krissvia.Core.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Krissvia.Server;

/// <summary>
/// Connexion, déconnexion et création du premier compte. Des formulaires HTML classiques, pas des composants
/// interactifs : le cookie de session se pose dans une réponse HTTP, ce qu'un circuit Blazor ne sait pas faire.
/// </summary>
internal static class Authentification
{
    public static void MapAuthentification(this WebApplication app)
    {
        app.MapPost("/connexion", async (
            [FromForm] string? utilisateur, [FromForm] string? motdepasse, [FromForm] string? retour,
            HttpContext contexte, AccountService comptes) =>
        {
            var resultat = await comptes.VerifyAsync(utilisateur?.Trim() ?? "", motdepasse ?? "");
            var suite = Url(retour);
            return resultat.Outcome switch
            {
                LoginOutcome.Ok => await ConnecterAsync(contexte, utilisateur!.Trim(), suite),
                LoginOutcome.Locked => Results.Redirect($"/connexion?erreur=verrou&secondes={resultat.LockedSeconds}&retour={Uri.EscapeDataString(suite)}"),
                _ => Results.Redirect($"/connexion?erreur=invalide&retour={Uri.EscapeDataString(suite)}"),
            };
        }).AllowAnonymous();

        app.MapPost("/installation", async (
            [FromForm] string? jeton, [FromForm] string? utilisateur, [FromForm] string? motdepasse, [FromForm] string? confirmation,
            HttpContext contexte, AccountService comptes) =>
        {
            string Retour(string erreur) =>
                $"/installation?jeton={Uri.EscapeDataString(jeton ?? "")}&utilisateur={Uri.EscapeDataString(utilisateur ?? "")}&erreur={Uri.EscapeDataString(erreur)}";

            if (motdepasse != confirmation) return Results.Redirect(Retour("les deux mots de passe diffèrent."));
            if (await comptes.CreateFirstAsync(jeton, utilisateur?.Trim() ?? "", motdepasse ?? "") is { } refus)
                return Results.Redirect(Retour(refus));

            return await ConnecterAsync(contexte, utilisateur!.Trim(), "/");
        }).AllowAnonymous();

        app.MapPost("/deconnexion", async (HttpContext contexte) =>
        {
            await contexte.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/connexion");
        });
    }

    private static async Task<IResult> ConnecterAsync(HttpContext contexte, string utilisateur, string suite)
    {
        var identite = new ClaimsIdentity([new Claim(ClaimTypes.Name, utilisateur)], CookieAuthenticationDefaults.AuthenticationScheme);
        await contexte.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identite),
            new AuthenticationProperties { IsPersistent = true });
        return Results.LocalRedirect(suite);
    }

    /// <summary>Seules les adresses locales sont suivies après connexion : pas de redirection vers un autre site.</summary>
    private static string Url(string? retour) =>
        retour is { Length: > 0 } r && r.StartsWith('/') && !r.StartsWith("//", StringComparison.Ordinal) && !r.StartsWith("/\\", StringComparison.Ordinal) ? r : "/";
}

/// <summary>Charge les comptes au démarrage et, s'il n'y en a aucun, écrit le jeton d'installation dans le journal.</summary>
internal sealed partial class AccountsLifetime(AccountService comptes, ILogger<AccountsLifetime> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await comptes.LoadAsync(cancellationToken);
        if (comptes.SetupToken is { } jeton) PremierCompte(logger, jeton);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Aucun compte. Créez le premier sur /installation?jeton={Jeton} (jeton à usage unique, valable jusqu'au prochain démarrage).")]
    private static partial void PremierCompte(ILogger logger, string jeton);
}
