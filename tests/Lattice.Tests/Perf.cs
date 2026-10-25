namespace Lattice.Tests;

/// <summary>
/// The trait of tests that assert wall-clock time (frame budgets, slice steps, rules on real-clock threads). Load on
/// the machine fails them without a fault in the code, so build.ps1 leaves them out; run them on demand with
/// <c>./build.ps1 -Perf</c> (or <c>dotnet test --filter Category=Perf</c>) on an otherwise idle machine.
/// </summary>
internal static class Perf
{
    public const string Trait = "Category";

    public const string Value = "Perf";
}
