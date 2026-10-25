using Assets.Scripts.Objects;
using Lattice.Api;
using Lattice.Core.World;

namespace Lattice.Game.World;

/// <summary>
/// The world bridge of one running world: the world view, the placement check and the change signals. Start it when
/// the world is running (<c>GameState.Running</c>), before anything scans the world: signals flow from the moment it
/// starts, so a scan made after it misses nothing, and a structure seen by both is the same id.
/// Structures registered while a save loads are not signalled; they are what the scan finds. End it when the world
/// ends: every subscription ends with it. Main thread only.
/// </summary>
internal sealed class WorldBridge
{
    private WorldBridge(WorldChangeRelay changes) => Changes = changes;

    /// <summary>The world as it stands.</summary>
    public GameWorldView View { get; } = new();

    /// <summary>The game's placement check.</summary>
    public GamePlacementCheck Placement { get; } = new();

    /// <summary>The world's change signals, held until Lattice's frame raises them (<see cref="WorldChangeRelay.Flush"/>).</summary>
    public WorldChangeRelay Changes { get; }

    /// <summary>
    /// Starts the bridge for the running world: installs the patches if they are not in yet (refused, with nothing
    /// patched, when the game lacks a member the module uses) and routes them to this world.
    /// </summary>
    public static Result<WorldBridge, Reason> Start()
    {
        if (!WorldPatches.Install().TryGetError(out var refused))
        {
            var changes = new WorldChangeRelay(Stands);
            WorldPatches.Current = changes;
            return Result<WorldBridge, Reason>.Ok(new WorldBridge(changes));
        }

        return Result<WorldBridge, Reason>.Error(refused);
    }

    /// <summary>Ends the bridge: no more signals, every subscription dropped. The patches stay, idle.</summary>
    public void End()
    {
        if (WorldPatches.Current == Changes)
            WorldPatches.Current = null;
        Changes.End();
    }

    private static bool Stands(ReferenceId id)
    {
        var structure = Referencable.Find<Structure>(id.Value);
        return structure != null && !structure.BeingDestroyed;
    }
}
