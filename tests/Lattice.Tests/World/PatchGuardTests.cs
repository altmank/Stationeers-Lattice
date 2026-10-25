using System;
using System.Collections.Generic;
using Lattice.Core.World;

namespace Lattice.Tests.World;

/// <summary>
/// A patched game call always completes: the patch bodies run through <see cref="PatchGuard"/>, as the world bridge's
/// do. The stand-in game setter below has a prefix and a postfix like the build-state patch; its postfix reports to a
/// relay that throws.
/// </summary>
public sealed class PatchGuardTests
{
    private static readonly List<string> Faults = new();
    private static readonly Action<string, Exception> OnFault = static (patch, e) => Faults.Add($"{patch}: {e.Message}");
    private static Action<int>? _report;

    private int _index;

    public PatchGuardTests()
    {
        Faults.Clear();
        _report = null;
    }

    [Fact]
    public void A_postfix_whose_relay_throws_leaves_the_game_call_complete()
    {
        _report = _ => throw new InvalidOperationException("relay bug");

        SetIndex(3);

        Assert.Equal(3, _index);
        Assert.Equal(new[] { "After: relay bug" }, Faults);
    }

    [Fact]
    public void A_prefix_that_throws_makes_its_postfix_skip_and_the_call_complete()
    {
        var reported = 0;
        _report = _ => reported++;

        SetIndex(5, prefixThrows: true);

        Assert.Equal(5, _index);
        Assert.Equal(0, reported);
        Assert.Equal(new[] { "Before: prefix bug" }, Faults);
    }

    [Fact]
    public void A_body_that_does_not_throw_runs_and_reports_no_fault()
    {
        var reported = new List<int>();
        _report = reported.Add;

        SetIndex(2);

        Assert.Equal(new[] { 2 }, reported);
        Assert.Empty(Faults);
    }

    /// <summary>The game's setter as Harmony runs it: prefix, original body, postfix.</summary>
    private void SetIndex(int value, bool prefixThrows = false)
    {
        var state = PatchGuard.Run("Before", (Self: this, Throws: prefixThrows), static p =>
            p.Throws ? throw new InvalidOperationException("prefix bug") : p.Self._index, int.MinValue, OnFault);
        _index = value;
        if (state != int.MinValue)
            PatchGuard.Run("After", this, state, static (self, before) =>
            {
                if (self._index != before)
                    _report?.Invoke(self._index);
            }, OnFault);
    }
}
