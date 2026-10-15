using System.Text.Json;
using Krissvia.Core.Commands;
using Krissvia.Core.States;

namespace Krissvia.Core.Events;

/// <summary>Événement du cœur, diffusé par le <see cref="IEventBus"/>.</summary>
public abstract record CoreEvent;

/// <summary>
/// Nouvelle valeur ou changement de disponibilité d'une entité. <see cref="Previous"/> est null quand l'entité
/// n'avait encore aucun état : ce n'est pas un front, un déclencheur « sur changement » ne doit pas partir
/// (sinon chaque redémarrage rallumerait les lumières, règle de DomoMud).
/// </summary>
public sealed record EntityStateChanged(string EntityId, EntityStateSnapshot? Previous, EntityStateSnapshot Current) : CoreEvent
{
    /// <summary>La valeur a changé (et l'état précédent était connu).</summary>
    public bool ValueChanged => Previous is not null && !JsonElement.DeepEquals(Previous.Value, Current.Value);

    public bool AvailabilityChanged => Previous is not null && Previous.Available != Current.Available;
}

/// <summary>Le registre a changé (déclaration d'un plugin, renommage, usage…).</summary>
public sealed record RegistryChanged : CoreEvent;

/// <summary>Un plugin s'est connecté ou sa session s'est terminée.</summary>
public sealed record PluginConnectionChanged(string PluginId, bool Connected) : CoreEvent;

/// <summary>Une commande est passée par le bus de commandes, quelle qu'en soit l'issue.</summary>
public sealed record CommandCompleted(CommandRecord Record) : CoreEvent;
