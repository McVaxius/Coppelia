namespace Coppelia;

internal static class PluginInfo
{
    public const string DisplayName = "Coppelia";
    public const string InternalName = "Coppelia";
    public const string Command = "/healbot";
    public const string AliasCommand = "/copellia";
    public const string WatchCommand = "watch";
    public const string Summary = "Multi-target healer automation seam built around RSR for watched targets.";
    public const string SupportUrl = "https://ko-fi.com/mcvaxius";
    public const string DiscordUrl = "https://discord.gg/VsXqydsvpu";
    public const string DiscordFeedbackNote = "Scroll down to \"The Dumpster Fire\" channel to discuss issues / suggestions for specific plugins.";

    public static readonly string[] RequiredPlugins =
    {
        "Rotation Solver Reborn (RSR)",
        "FrenRider",
        "vnavmesh",
        "BossMod Reborn (BMR) or VBM",
    };

    public static readonly string[] SupportedJobs =
    {
        "WHM",
        "SCH",
        "AST",
        "SGE",
    };
}
