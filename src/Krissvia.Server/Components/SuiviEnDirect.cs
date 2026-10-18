using Krissvia.Core.Live;

namespace Krissvia.Server.Components;

/// <summary>
/// Rafraîchit un écran à chaque changement d'état ou du registre (le flux de <c>/api/live</c>), et au moins une fois
/// par <c>periode</c> : une sonde qui se tait ne publie rien, c'est le temps qui la rend « hors délai ».
/// </summary>
internal sealed class SuiviEnDirect : IDisposable
{
    private readonly CancellationTokenSource _arret = new();
    private readonly LiveSubscription _abonnement;

    public SuiviEnDirect(LiveFeed flux, Func<bool, Task> rafraichir, TimeSpan periode)
    {
        _abonnement = flux.Subscribe();
        _ = BoucleAsync(rafraichir, periode);
    }

    /// <param name="rafraichir">Reçoit vrai si le registre a changé (entité ajoutée, renommée, pièce modifiée…).</param>
    private async Task BoucleAsync(Func<bool, Task> rafraichir, TimeSpan periode)
    {
        while (!_arret.IsCancellationRequested)
        {
            var registre = false;
            using (var delai = CancellationTokenSource.CreateLinkedTokenSource(_arret.Token))
            {
                delai.CancelAfter(periode);
                try
                {
                    registre = (await _abonnement.ReadAsync(delai.Token)).RegistryChanged;
                }
                catch (OperationCanceledException) when (!_arret.IsCancellationRequested)
                {
                    // Rien n'a bougé pendant la période : on rafraîchit quand même, pour la fraîcheur des mesures.
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            try
            {
                await rafraichir(registre);
            }
            catch (ObjectDisposedException)
            {
                return;   // circuit fermé entre-temps
            }
        }
    }

    public void Dispose()
    {
        _arret.Cancel();
        _abonnement.Dispose();
        _arret.Dispose();
    }
}
