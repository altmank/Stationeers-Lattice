using System;
using Lattice.Api;

namespace Lattice.Core.Saving;

/// <summary>
/// Which save a starting world was loaded from. The game's load (<c>LoadHelper.LoadGame(path, stationName)</c>) sets
/// <c>CurrentWorldSave</c> and <c>CurrentStationName</c> on success and clears both when it fails; a new world started
/// from the menu or as a tutorial calls <c>XmlSaveLoad.ClearAll</c> first, and one started by a console command sets
/// <c>CurrentStationName</c> only after it runs. So a path noted for a load that then failed meets a game with no current
/// save (or no station name) when the next world starts, and is not taken for that world.
/// </summary>
internal static class LoadedSaveRule
{
    /// <summary>
    /// The noted save path when the game still holds the load it was noted for: a current world save, and a station
    /// name equal to the one the load was called with. Otherwise none.
    /// </summary>
    public static Option<string> Accept(string? loadedPath, string? nameNoted, bool gameHasCurrentSave, string? gameStationName) =>
        loadedPath != null && nameNoted != null && gameHasCurrentSave && string.Equals(nameNoted, gameStationName, StringComparison.Ordinal)
            ? Option.Some(loadedPath)
            : Option<string>.None;
}
