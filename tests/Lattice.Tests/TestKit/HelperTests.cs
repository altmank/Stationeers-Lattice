using System;
using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.TestKit.Allocations;
using Lattice.TestKit.Golden;
using Lattice.TestKit.PlacementCheck;
using Lattice.TestKit.Catalogue;

namespace Lattice.Tests.TestKit;

/// <summary>Golden files, the allocation helper, and the placement check's call record.</summary>
public sealed class HelperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lattice-golden-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_golden_file_is_written_first_then_compared()
    {
        var path = Path.Combine(_dir, "a.txt");

        Assert.IsType<GoldenOutcome.Written>(GoldenFile.Check(path, "one\ntwo\n"));
        Assert.IsType<GoldenOutcome.Matches>(GoldenFile.Check(path, "one\r\ntwo\r\n"));
        var differs = Assert.IsType<GoldenOutcome.Differs>(GoldenFile.Check(path, "one\nthree\n"));
        Assert.Equal(2, differs.Line);
        Assert.Equal("two", differs.Expected);
        Assert.Equal("three", differs.Actual);
    }

    [Fact]
    public void World_reads_allocate_nothing()
    {
        var w = Worlds.Mixed().World;
        var cell = new Cell(0, 0, 5);
        var empty = new Cell(99, 99, 99);
        var id = new ReferenceId(2);
        w.NetworkOf(id);

        Allocations.AssertNone(() => w.At(cell), "At");
        Allocations.AssertNone(() => w.At(empty), "At on an empty cell");
        Allocations.AssertNone(() => w.Structure(id), "Structure");
        Allocations.AssertNone(() => w.NetworkOf(id), "NetworkOf");
        Allocations.AssertNone(() =>
        {
            foreach (var s in w.At(cell))
                _ = s;
        }, "foreach over At");
    }

    [Fact]
    public void An_allocating_call_is_measured()
    {
        var sink = new List<object>();
        Assert.True(Allocations.Of(() => sink.Add(new object()), repeat: 10) > 0);
    }

    [Fact]
    public void The_placement_check_records_every_call_in_order()
    {
        var check = new FakePlacementCheck();
        var a = new Placement(PrefabHash.Piece("StructureFrameIron"), new Cell(10, 10, 10), Turn.None);
        var b = a with { At = new Cell(30, 10, 10) };

        check.Check(a);
        check.Check(b);
        check.Check(a);

        Assert.Equal(new[] { a, b, a }, check.Calls);
    }

    [Fact]
    public void The_first_refusing_rule_answers_with_the_games_message()
    {
        var check = new FakePlacementCheck()
            .Refuse(_ => true, ReasonCodes.GameRefused, "first")
            .Refuse(_ => true, ReasonCodes.NeedsSupport, "second");

        Assert.True(check.Check(new Placement(default, default, Turn.None)).TryGetError(out var why));
        Assert.Equal(ReasonCodes.GameRefused, why.Code);
        Assert.Equal(Option.Some("first"), why.GameMessage);
    }
}
