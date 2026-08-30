namespace Coppelia.Models;

internal sealed class HealbotJobProfile
{
    private HealbotJobProfile(
        uint jobId,
        string jobAbbreviation,
        string jobDisplayName,
        string raiseActionName,
        IReadOnlyList<string> dotActionNames,
        IReadOnlyList<string> offensiveActionNames)
    {
        JobId = jobId;
        JobAbbreviation = jobAbbreviation;
        JobDisplayName = jobDisplayName;
        RaiseActionName = raiseActionName;
        DotActionNames = dotActionNames;
        OffensiveActionNames = offensiveActionNames;
    }

    public uint JobId { get; }
    public string JobAbbreviation { get; }
    public string JobDisplayName { get; }
    public string RaiseActionName { get; }
    public IReadOnlyList<string> DotActionNames { get; }
    public IReadOnlyList<string> OffensiveActionNames { get; }

    public static bool TryResolve(uint jobId, out HealbotJobProfile profile)
    {
        if (Profiles.TryGetValue(jobId, out profile!))
            return true;

        profile = null!;
        return false;
    }

    private static readonly IReadOnlyDictionary<uint, HealbotJobProfile> Profiles = new Dictionary<uint, HealbotJobProfile>
    {
        [24] = new(
            24,
            "WHM",
            "White Mage",
            "Raise",
            new[] { "Aero", "Aero II", "Dia" },
            new[]
            {
                "Stone",
                "Stone II",
                "Stone III",
                "Stone IV",
                "Glare",
                "Glare III",
                "Aero",
                "Aero II",
                "Dia",
                "Holy",
                "Holy III",
                "Afflatus Misery",
                "Assize",
                "Fluid Aura",
            }),
        [28] = new(
            28,
            "SCH",
            "Scholar",
            "Resurrection",
            new[] { "Bio", "Bio II", "Biolysis" },
            new[]
            {
                "Ruin",
                "Ruin II",
                "Broil",
                "Broil II",
                "Broil III",
                "Broil IV",
                "Bio",
                "Bio II",
                "Biolysis",
                "Art of War",
                "Art of War II",
                "Energy Drain",
            }),
        [33] = new(
            33,
            "AST",
            "Astrologian",
            "Ascend",
            new[] { "Combust", "Combust II", "Combust III" },
            new[]
            {
                "Malefic",
                "Malefic II",
                "Malefic III",
                "Malefic IV",
                "Fall Malefic",
                "Combust",
                "Combust II",
                "Combust III",
                "Gravity",
                "Gravity II",
                "Lord of Crowns",
                "Macrocosmos",
            }),
        [40] = new(
            40,
            "SGE",
            "Sage",
            "Resurrection",
            new[] { "Eukrasian Dosis", "Eukrasian Dosis II", "Eukrasian Dosis III" },
            new[]
            {
                "Dosis",
                "Dosis II",
                "Dosis III",
                "Eukrasian Dosis",
                "Eukrasian Dosis II",
                "Eukrasian Dosis III",
                "Dyskrasia",
                "Dyskrasia II",
                "Toxikon",
                "Toxikon II",
                "Phlegma",
                "Phlegma II",
                "Phlegma III",
                "Pneuma",
                "Psyche",
            }),
    };
}
