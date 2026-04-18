using Dalamud.Game.ClientState.Objects.Types;

namespace Coppelia.Models;

internal sealed class WatchTargetCandidate
{
    public ICharacter Character { get; init; } = null!;
    public WatchTargetSnapshot Snapshot { get; init; } = null!;
    public ResolvedWatchTarget Target { get; init; } = null!;
}
