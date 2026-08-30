namespace Coppelia;

internal static class PluginInfo
{
    public const string DisplayName = "HealBot";
    public const string InternalName = "Coppelia";
    public const string Command = "/healbot";
    public const string ShortAliasCommand = "/hb";
    public const string LegacyAliasCommand = "/copellia";
    public const string WatchCommand = "watch";
    public const string Summary = "Guided healing, healer-first JOAT, Fren-assisted powerleveling, and paired Newb travel.";
    public const string Description = "HealBot provides four mutually exclusive automation modes. HealBot heals watched friendly targets; Jacqueline of All Trades (JOAT) heals first and attacks only while healing is idle; PowerlevelBot retains its BRD/MCH instant-action policy; and Newb securely pairs one client to a HealBot over direct authenticated TCP for healing and travel. LAN traffic is authenticated but not encrypted, so names and coordinates remain visible on the network. Use /healbot, /hb, or /copellia to open.";
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
