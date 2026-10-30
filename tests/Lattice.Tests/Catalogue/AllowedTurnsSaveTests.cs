using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Matching;
using Lattice.TestKit;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.Saves;
using Lattice.TestKit.World;
using Xunit.Abstractions;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// The allowed turns against a base a player built with the game's builder (Vulcan2_4): every built structure stands
/// in a turn its piece allows, or its piece has no direction, so the turn it stands in looks like an allowed one.
/// </summary>
public sealed class AllowedTurnsSaveTests(ITestOutputHelper output)
{
    [GameCatalogueFact]
    public void Every_structure_in_a_real_base_stands_in_an_allowed_turn_or_has_no_direction()
    {
        if (!Fixtures.Save("Vulcan2_4.save").TryGet(out var path))
            return;
        var catalogue = GameCatalogue.Load();
        var save = TestKitIds.MustRead(SaveLoader.Load(path, catalogue));
        var free = new TurnFreePieces(catalogue, new CubeGrid(), new Lattice.Core.Board.Shapes(catalogue, new CubeGrid()));
        var limited = 0;
        var outside = new List<string>();
        foreach (var structure in new Lattice.Tests.Validation.FakeScan(save.World).Structures())
        {
            if (!catalogue.Piece(structure.Ref.Piece).TryGet(out var piece) || piece.AllowedTurns.IsAll)
                continue;
            limited++;
            if (!piece.AllowedTurns.Contains(structure.Turn) && !free.AllowedTwin(piece, structure.Turn).IsSome)
                outside.Add($"{catalogue.NameOf(piece.Key)} turn {structure.Turn.Index} allowed {piece.AllowedTurns} at {structure.Ref.At}");
        }

        foreach (var line in outside.GroupBy(l => l.Split(' ')[0]).Select(g => $"x{g.Count()}: {g.First()}"))
            output.WriteLine(line);
        output.WriteLine($"{limited} structures of limited pieces, {outside.Count} outside");
        Assert.True(limited > 0);
        Assert.Empty(outside);
    }
}
