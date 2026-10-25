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

    /// <summary>
    /// A full collection, finalizers included, before a timed loop: the heap the setup left behind is not collected
    /// inside the loop, so a pause there is the measured code's own.
    /// </summary>
    public static void Collect()
    {
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();
    }
}

/// <summary>
/// Tests that assert wall-clock time run in this collection, one at a time and after the parallel tests, so no other
/// test shares their CPU or triggers a collection that pauses them.
/// </summary>
[CollectionDefinition(nameof(Timing), DisableParallelization = true)]
public sealed class Timing;
