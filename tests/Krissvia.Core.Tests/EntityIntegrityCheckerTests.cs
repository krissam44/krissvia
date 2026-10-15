using Krissvia.Contracts;
using Krissvia.Core.Registry;

namespace Krissvia.Core.Tests;

public class EntityIntegrityCheckerTests
{
    private sealed class SourceFixe(params EntityReference[] references) : IEntityReferenceSource
    {
        public Task<IReadOnlyList<EntityReference>> GetReferencesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EntityReference>>(references);
    }

    private static DeviceDescriptor Prise(string uniqueId, string nom) => new()
    {
        UniqueId = uniqueId,
        Name = nom,
        Entities = [new EntityDescriptor { UniqueId = uniqueId + ":switch", Kind = EntityKind.Switch }],
    };

    [Fact]
    public async Task Les_references_cassees_sont_signalees_avec_leur_cause()
    {
        var registre = new EntityRegistry(new InMemoryEntityRepository());
        await registre.RegisterAsync("test", [Prise("a", "Prise salon"), Prise("b", "Prise cuisine"), Prise("c", "Prise cave")]);
        await registre.RegisterAsync("test", [Prise("a", "Prise salon"), Prise("c", "Prise cave")]); // « Prise cuisine » disparaît
        await registre.SetEnabledAsync("switch.prise_cave", false);

        var source = new SourceFixe(
            new EntityReference("thermostat", "salon", "sortie", "switch.prise_salon"),
            new EntityReference("thermostat", "cuisine", "sortie", "switch.prise_cuisine"),
            new EntityReference("thermostat", "cave", "sortie", "switch.prise_cave"),
            new EntityReference("delestage", "principal", "coupure", "switch.fantome"));

        var problemes = await new EntityIntegrityChecker(registre, [source]).CheckAsync();

        Assert.Equal(3, problemes.Count);
        Assert.Contains(problemes, p => p.Reference.EntityId == "switch.prise_cuisine" && p.Problem == ReferenceProblem.Missing);
        Assert.Contains(problemes, p => p.Reference.EntityId == "switch.prise_cave" && p.Problem == ReferenceProblem.Disabled);
        Assert.Contains(problemes, p => p.Reference.EntityId == "switch.fantome" && p.Problem == ReferenceProblem.Unknown);
    }

    [Fact]
    public async Task Sans_reference_cassee_aucun_probleme()
    {
        var registre = new EntityRegistry(new InMemoryEntityRepository());
        await registre.RegisterAsync("test", [Prise("a", "Prise salon")]);
        var source = new SourceFixe(new EntityReference("thermostat", "salon", "sortie", "switch.prise_salon"));

        var problemes = await new EntityIntegrityChecker(registre, [source]).CheckAsync();

        Assert.Empty(problemes);
    }
}
