namespace Lattice.Tests;

/// <summary>A fact that needs generated saves or logs: see <see cref="FixtureFactAttribute"/> for when it skips or fails.</summary>
public sealed class SaveFactAttribute(params string[] saves) : FixtureFactAttribute(MissingSave(saves));
