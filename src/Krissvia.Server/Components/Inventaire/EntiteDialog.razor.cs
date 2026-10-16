using Krissvia.Contracts;
using Krissvia.Core.Commands;
using Krissvia.Core.Registry;

namespace Krissvia.Server.Components.Inventaire;

/// <summary>
/// Champs de l'utilisateur d'une entité (nom, puissance, activation) et pièce de son appareil. Seuls les champs
/// modifiés sont écrits : chacun est une écriture du registre.
/// </summary>
public partial class EntiteDialog
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public RegisteredEntity Entite { get; set; } = default!;
    [Parameter] public RegisteredDevice? Appareil { get; set; }
    [Parameter] public IReadOnlyList<RegisteredEntity> AutresEntites { get; set; } = [];

    [Inject] private IEntityRegistry Registre { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    /// <summary>Entité publiée par un module du cœur (thermostat) : son nom vient de la configuration du module.</summary>
    private bool DuCoeur => CoreIntegrations.IsCore(Entite.IntegrationId);

    private string _nom = "";
    private string? _piece;
    private double _puissance;
    private bool _active;
    private string? _erreur;

    protected override void OnInitialized()
    {
        _nom = Entite.DisplayName;
        _piece = Appareil?.Room;
        _puissance = Entite.NominalPower ?? 0;
        _active = Entite.Enabled;
    }

    private async Task EnregistrerAsync()
    {
        if (string.IsNullOrWhiteSpace(_nom))
        {
            _erreur = "Le nom ne peut pas être vide.";
            return;
        }

        if (!DuCoeur && _nom.Trim() != Entite.DisplayName) await Registre.RenameAsync(Entite.EntityId, _nom);
        if (Appareil is not null && (_piece?.Trim() ?? "") != (Appareil.Room ?? "")) await Registre.SetRoomAsync(Appareil.DeviceId, _piece);
        if (Entite.Kind == EntityKind.Switch && _puissance != (Entite.NominalPower ?? 0))
            await Registre.SetNominalPowerAsync(Entite.EntityId, _puissance > 0 ? _puissance : null);
        if (_active != Entite.Enabled) await Registre.SetEnabledAsync(Entite.EntityId, _active);

        Snackbar.Add($"« {_nom.Trim()} » enregistrée.", Severity.Success);
        MudDialog.Close(DialogResult.Ok(true));
    }
}
