using Assets.Scripts;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Game.World;

/// <summary>
/// <see cref="IGameClock"/> over <c>GameManager.GameTime</c>: Unity's <c>Time.time</c>, copied by
/// <c>GameManager.Update</c> only while the world is not paused and set at <c>GameManager.StartGame</c> (decompile
/// <c>Assets.Scripts/GameManager.cs</c>). It counts seconds since the
/// game process started, stands still while paused and, on unpause, catches up with the time the pause took.
/// Main thread; reading it allocates nothing.
/// </summary>
internal sealed class GameClock : IGameClock
{
    /// <inheritdoc />
    public GameTime Now => new(GameManager.GameTime);
}
