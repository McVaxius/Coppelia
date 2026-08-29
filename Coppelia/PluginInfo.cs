namespace Coppelia;

internal static class PluginInfo
{
    public const string DisplayName = "Coppelia";
    public const string InternalName = "Coppelia";
    public const string Command = "/healbot";
    public const string AliasCommand = "/copellia";
    public const string WatchCommand = "watch";
    public const string Summary = "Guided watched-target healing, healer-first JOAT support, and Fren-assisted powerleveling.";
    public const string Description = "Coppelia provides guided setup for three mutually exclusive automation modes. HealBot runs configurable WHM, SCH, AST, or SGE healing, raises, buffs, and pre-buffs for up to 20 watched friendly targets. Jacqueline of All Trades (JOAT) gives that healing absolute priority, then uses the equipped healer's highest available single-target filler spell only when the healing decision is idle and an eligible damaged enemy is already targeting FrenRider's configured visible Fren or the local healer. PowerlevelBot retains its BRD/MCH instant-action policy. Includes dependency/readiness checks, optional saved targets, and optional Rotation Solver Reborn isolation for HealBot and JOAT. Use /healbot to open.";
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
