using Krissvia.Contracts;

namespace Krissvia.Core.Commands;

/// <summary>
/// Envoi d'une commande à une entité. Le seul fil que tirent les moteurs vers le monde extérieur :
/// il permet de les tester sans plugin ni broker, et d'observer les ordres réellement émis.
/// </summary>
/// <remarks>
/// Le résultat dit si le plugin a pris l'ordre, pas si l'appareil a obéi : une coupure de sécurité se
/// confirme aussi par l'état résultant de l'entité (voir ARCHITECTURE.md §7). Une implémentation ne
/// masque jamais un échec : elle renvoie <see cref="CommandOutcome.Failed"/> ou
/// <see cref="CommandOutcome.Unreachable"/>.
/// </remarks>
public interface IEntityCommander
{
    Task<CommandOutcome> SendAsync(
        string entityId,
        string command,
        CommandPriority priority = CommandPriority.Normal,
        int? ttlSeconds = null,
        CancellationToken ct = default);
}
