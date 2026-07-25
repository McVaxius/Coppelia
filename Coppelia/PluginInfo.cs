namespace Coppelia;

internal static class PluginInfo
{
    public const string DisplayName = "Coppelia";
    public const string InternalName = "Coppelia";
    public const string Command = "/healbot";
    public const string AliasCommand = "/copellia";
    public const string WatchCommand = "watch";
    public const string Summary = "Guided healing for up to 20 watched targets, plus Fren-assisted powerleveling.";
    public const string Description = "Coppelia provides guided setup for two mutually exclusive automation modes. HealBot runs configurable WHM, SCH, AST, or SGE healing, raises, buffs, and pre-buffs for up to 20 watched friendly targets, including targets outside the party. PowerlevelBot uses a currently equipped BRD or MCH to attack damaged enemies already engaging FrenRider's configured Fren or the local player. Includes dependency/readiness checks, optional saved targets, and optional Rotation Solver Reborn isolation for HealBot. Use /healbot to open.";
    public const string SupportUrl = "https://ko-fi.com/mcvaxius";
    public const string DiscordUrl = "https://discord.gg/VsXqydsvpu";
    public const string DiscordFeedbackNote = "Scroll down to \"The Dumpster Fire\" channel to discuss issues / suggestions for specific plugins.";

    public static readonly string[] RequiredPlugins =
    {
        "FrenRider",
        "vnavmesh",
        "BossMod Reborn (BMR) or VBM",
    };

    public static readonly string[] RecommendedPlugins =
    {
        "Rotation Solver Reborn (RSR) - optional isolation/restore helper",
    };

    public static readonly string[] SupportedJobs =
    {
        "WHM",
        "SCH",
        "AST",
        "SGE",
    };
}
