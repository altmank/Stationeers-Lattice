using Lattice.Api;

namespace Lattice.TestKit;

/// <summary>Why a fixture file could not be read: which file, which line (0 when not line based), what is wrong.</summary>
internal sealed record FileError(string File, int Line, string Problem)
{
    public override string ToString() => Line > 0 ? $"{File}:{Line}: {Problem}" : $"{File}: {Problem}";
}

/// <summary>The ids the test kit's own reasons carry.</summary>
internal static class TestKitIds
{
    public static readonly ModId Mod = Must(ModId.Create("testkit"));

    public static readonly RuleId Catalogue = Must(RuleId.Create(Mod, "catalogue"));

    public static readonly RuleId PlacementCheck = Must(RuleId.Create(Mod, "placement-check"));

    public static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new System.InvalidOperationException(result.ToString());

    public static T MustRead<T>(Result<T, FileError> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new System.InvalidOperationException(result.ToString());
}
