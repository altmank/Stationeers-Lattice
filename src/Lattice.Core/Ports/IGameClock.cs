using Lattice.Api;

namespace Lattice.Core.Ports;

/// <summary>
/// The world's game time: <c>GameManager.GameTime</c>, which stands still while the game is paused
/// (<c>GameManager.Update</c> stops updating it). Main thread; reading it allocates nothing.
/// </summary>
internal interface IGameClock
{
    /// <summary>Now, in game seconds since the world started; never goes back within a world.</summary>
    GameTime Now { get; }
}
