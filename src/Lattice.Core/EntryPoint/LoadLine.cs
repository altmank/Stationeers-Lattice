using System.Globalization;
using Lattice.Api;

namespace Lattice.Core.EntryPoint;

/// <summary>
/// The one line Lattice logs when it starts: its version, whether saving and multiplayer are on, and the versions of
/// the two libraries it runs on. <paramref name="SavingPatches"/> is none when the saving hooks didn't go in.
/// </summary>
internal sealed record LoadLine(string Version, Option<int> SavingPatches, bool SyncOn, string Booster, string Harmony)
{
    /// <summary>The line's text; a blank version reads "unknown".</summary>
    public string Text =>
        $"Lattice {Known(Version)} loaded: {Saving}, multiplayer sync {(SyncOn ? "on" : "off")}, " +
        $"LaunchPadBooster {Known(Booster)}, Harmony {Known(Harmony)}.";

    private string Saving =>
        SavingPatches.Match(
            patches => $"saving hooks in ({patches.ToString(CultureInfo.InvariantCulture)} patches)",
            () => "saving hooks not in");

    private static string Known(string version) => string.IsNullOrWhiteSpace(version) ? "unknown" : version;
}
