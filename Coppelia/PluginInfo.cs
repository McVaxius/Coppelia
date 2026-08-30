namespace Coppelia;

internal static class PluginInfo
{
    public const string DisplayName = "HealBot";
    public const string InternalName = "Coppelia";
    public const string Command = "/healbot";
    public const string ShortAliasCommand = "/hb";
    public const string LegacyAliasCommand = "/copellia";
    public const string WatchCommand = "watch";
    public const string Summary = "Stand-alone healing/JOAT/powerleveling and authenticated Helper/Newb pairing.";
    public const string Description = "HealBot has four operating roles: Off, Stand-alone, Helper, and Newb. Stand-alone runs HealBot, healer-first JOAT, or BRD/MCH PowerlevelBot without networking. Helper listens for one authenticated Newb and activates JOAT for remote healing and chase; Newb connects asynchronously and performs no local healing or attacking. Pairing traffic is authenticated but not encrypted, so names and coordinates remain visible on the network. Use /healbot, /hb, or /copellia to open.";
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
