using Lattice.Api;

namespace Lattice.Core.Ports;

/// <summary>
/// Every structure the world holds, read once when a world is running, after the change signals are subscribed, so the
/// world mirror misses nothing registered between the two. Main thread; linear in the world's structures. Nothing calls
/// it again for that world: after the scan the mirror follows <see cref="IWorldChanges"/> only.
/// </summary>
internal interface IWorldScan
{
    ValueList<StructureInfo> Structures();
}
