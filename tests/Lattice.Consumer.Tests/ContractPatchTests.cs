using System;
using System.Runtime.CompilerServices;
using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A consumer checks the running patch before it calls a member a patch added, reading it in a method of its own so a
/// Lattice too old to have it reads as an older patch.
/// </summary>
public sealed class ContractPatchTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadPatch() => ApiVersion.CurrentPatch;

    private static int RunningPatch()
    {
        try
        {
            return ReadPatch();
        }
        catch (MissingMethodException)
        {
            return 3;
        }
    }

    private static bool Has(int major, int minor, int patch) =>
        ApiVersion.Current.Major == major && (ApiVersion.Current.Minor > minor || (ApiVersion.Current.Minor == minor && RunningPatch() >= patch));

    [Fact]
    public void A_consumer_reads_the_patch_it_was_built_against_and_the_running_one()
    {
        Assert.True(ApiVersion.ContractPatch >= 4);
        Assert.Equal(ApiVersion.ContractPatch, RunningPatch());
        Assert.True(Has(0, 22, 2), "PieceSnap.OnFaces is there");
        Assert.False(Has(0, 22, ApiVersion.ContractPatch + 1));
    }
}
