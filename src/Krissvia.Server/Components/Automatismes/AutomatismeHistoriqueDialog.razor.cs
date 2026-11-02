using System.Globalization;

namespace Krissvia.Server.Components.Automatismes;

/// <summary>Les exécutions d'un automatisme relues dans le journal durable (<see cref="AutomationHistory"/>).</summary>
public partial class AutomatismeHistoriqueDialog
{
    private static readonly int[] Periodes = [7, 30, 90];
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    [CascadingParameter] private IMudDialogInstance Dialogue { get; set; } = default!;
    [Parameter, EditorRequired] public string AutomationId { get; set; } = "";
    [Parameter] public string Nom { get; set; } = "";

    [Inject] private AutomationHistory Historique { get; set; } = default!;
    [Inject] private TimeProvider Horloge { get; set; } = default!;

    private int _jours = 7;
    private bool _chargement = true;
    private IReadOnlyList<AutomationRun> _executions = [];

    public static Task OuvrirAsync(IDialogService dialogues, string id, string nom) =>
        dialogues.ShowAsync<AutomatismeHistoriqueDialog>(null,
            new DialogParameters<AutomatismeHistoriqueDialog> { { d => d.AutomationId, id }, { d => d.Nom, nom } },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true });

    protected override Task OnInitializedAsync() => ChargerAsync();

    private Task ChoisirAsync(int jours)
    {
        _jours = jours;
        return ChargerAsync();
    }

    private async Task ChargerAsync()
    {
        _chargement = true;
        _executions = await Historique.ReadAsync(AutomationId, TimeSpan.FromDays(_jours));
        _chargement = false;
    }

    /// <summary>« 31 exécutions · 30 réussies · 1 en échec » : de quoi voir d'un coup d'œil qu'un soir a manqué.</summary>
    private string Resume
    {
        get
        {
            var parties = new List<string> { $"{_executions.Count} exécution{(_executions.Count > 1 ? "s" : "")}" };
            var reussies = _executions.Count(r => r.Outcome == RunOutcome.Done);
            var nonRemplies = _executions.Count(r => r.Outcome == RunOutcome.ConditionsNotMet);
            var echecs = _executions.Count - reussies - nonRemplies;
            if (reussies > 0) parties.Add($"{reussies} réussie{(reussies > 1 ? "s" : "")}");
            if (nonRemplies > 0) parties.Add($"{nonRemplies} sans suite (conditions)");
            if (echecs > 0) parties.Add($"{echecs} en échec");
            return string.Join(" · ", parties);
        }
    }

    private DateTime Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Horloge.LocalTimeZone).DateTime;
}
