namespace Coppelia.Models;

internal sealed record PowerlevelActionDefinition(string ActionName, PowerlevelJob Job, int Priority);

internal static class PowerlevelActionCatalog
{
    private static readonly IReadOnlyDictionary<PowerlevelJob, IReadOnlyList<PowerlevelActionDefinition>> DefinitionsByJob =
        new Dictionary<PowerlevelJob, IReadOnlyList<PowerlevelActionDefinition>>
        {
            [PowerlevelJob.BRD] =
            [
                new("Empyreal Arrow", PowerlevelJob.BRD, 10),
                new("Sidewinder", PowerlevelJob.BRD, 20),
                new("Heartbreak Shot", PowerlevelJob.BRD, 30),
                new("Bloodletter", PowerlevelJob.BRD, 31),
                new("Refulgent Arrow", PowerlevelJob.BRD, 40),
                new("Straight Shot", PowerlevelJob.BRD, 41),
                new("Iron Jaws", PowerlevelJob.BRD, 50),
                new("Caustic Bite", PowerlevelJob.BRD, 60),
                new("Venomous Bite", PowerlevelJob.BRD, 61),
                new("Stormbite", PowerlevelJob.BRD, 70),
                new("Windbite", PowerlevelJob.BRD, 71),
                new("Burst Shot", PowerlevelJob.BRD, 80),
                new("Heavy Shot", PowerlevelJob.BRD, 81),
            ],
            [PowerlevelJob.MCH] =
            [
                new("Drill", PowerlevelJob.MCH, 10),
                new("Air Anchor", PowerlevelJob.MCH, 20),
                new("Hot Shot", PowerlevelJob.MCH, 21),
                new("Gauss Round", PowerlevelJob.MCH, 30),
                new("Blazing Shot", PowerlevelJob.MCH, 40),
                new("Heat Blast", PowerlevelJob.MCH, 41),
                new("Heated Clean Shot", PowerlevelJob.MCH, 50),
                new("Clean Shot", PowerlevelJob.MCH, 51),
                new("Heated Slug Shot", PowerlevelJob.MCH, 60),
                new("Slug Shot", PowerlevelJob.MCH, 61),
                new("Heated Split Shot", PowerlevelJob.MCH, 70),
                new("Split Shot", PowerlevelJob.MCH, 71),
            ],
        };

    public static IReadOnlyList<PowerlevelActionDefinition> GetDefinitions(PowerlevelJob job)
        => DefinitionsByJob.TryGetValue(job, out var definitions)
            ? definitions
            : [];
}
