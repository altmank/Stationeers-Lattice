using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.TestKit.Allocations;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// A watched match (Render's claims): judged per batch on the batch's signature, its
/// leaves joining every generation while it is held, its generation rising only when its compiled form changes.
/// </summary>
public sealed class MatchWatchTests
{
    private static List<Batch> Batches(StyleBench bench)
    {
        var batches = new List<Batch>();
        for (var i = 0; i < bench.Styles.Plan.BatchCount; i++)
            if (bench.Styles.Plan.BatchAt(i).Count > 0)
                batches.Add(bench.Styles.Plan.BatchAt(i));
        return batches;
    }

    private static StyleBench Cables()
    {
        var bench = new StyleBench();
        bench.Looks.Family("StructureCableStraight", PieceFamilies.Cable).Family("StructureFrame", PieceFamilies.Frame);
        bench.Place(
            bench.Draft("StructureCableStraight", new Cell(5, 5, 5)),
            bench.Draft("StructureCableStraight", new Cell(10, 5, 5)),
            bench.Draft("StructureFrame", new Cell(30, 10, 10)));
        return bench;
    }

    [Fact]
    public void A_watch_holds_for_the_batches_whose_things_match_and_for_no_other()
    {
        var bench = Cables();
        using var watch = bench.Styles.Watch(new StyleMatch.Family(PieceFamilies.Cable));
        bench.Settle();

        var batches = Batches(bench);
        Assert.Equal(2, batches.Count);
        foreach (var batch in batches)
            Assert.Equal(batch.Piece == bench.T.Catalogue.Named("StructureCableStraight").Key, watch.Holds(batch));
    }

    [Fact]
    public void A_watch_on_a_modded_family_holds_for_its_unknown_piece()
    {
        var bench = new StyleBench();
        var conduits = FamilyOf("conduits");
        bench.Looks.Family(Modded, conduits);
        bench.Place(bench.Draft(Modded, new Cell(5, 5, 5)), bench.Draft("StructureFrame", new Cell(30, 10, 10)));
        using var watch = bench.Styles.Watch(new StyleMatch.Family(conduits));
        bench.Settle();

        Assert.Contains(Batches(bench), batch => batch.Piece == bench.T.Catalogue.Named(Modded).Key && watch.Holds(batch));
        Assert.DoesNotContain(Batches(bench), batch => batch.Piece != bench.T.Catalogue.Named(Modded).Key && watch.Holds(batch));
    }

    [Fact]
    public void A_watch_on_a_leaf_no_layer_reads_starts_a_new_generation_in_slices_and_holds_once_it_is_done()
    {
        var bench = Cables();
        var frame = Assert.Single(Batches(bench), batch => batch.Piece == bench.T.Catalogue.Named("StructureFrame").Key);
        var claimed = SetId("claimed");
        var ghost = frame[0].Thing.Id;
        Must(bench.Styles.Set(claimed)).Put(ghost);
        bench.Settle();

        using var watch = bench.Styles.Watch(new StyleMatch.InSet(claimed));

        Assert.True(bench.Styles.Busy);
        bench.Settle();
        var now = Assert.Single(Batches(bench), batch => batch.Piece == bench.T.Catalogue.Named("StructureFrame").Key);
        Assert.True(watch.Holds(now));
        Assert.DoesNotContain(Batches(bench), batch => !ReferenceEquals(batch, now) && watch.Holds(batch));
    }

    [Fact]
    public void A_watch_on_a_leaf_a_layer_already_reads_holds_at_once_on_the_batches_drawn_now()
    {
        var bench = Cables();
        bench.Add(LayerOf("cables", StyleBand.Theme, 0, Rule(new StyleMatch.Family(PieceFamilies.Cable), Edge(LineStyle.Solid, LibraryPalette.Next))));
        bench.Settle();

        using var watch = bench.Styles.Watch(new StyleMatch.Family(PieceFamilies.Cable));

        Assert.Contains(Batches(bench), batch => watch.Holds(batch));
    }

    [Fact]
    public void Its_generation_rises_when_its_compiled_form_changes_and_not_from_frame_to_frame()
    {
        var bench = Cables();
        using var watch = bench.Styles.Watch(new StyleMatch.Family(PieceFamilies.Cable));
        bench.Settle();
        var settled = watch.Generation;

        for (var i = 0; i < 20; i++)
            bench.Frame();
        Assert.Equal(settled, watch.Generation);

        bench.Add(LayerOf("frames", StyleBand.Theme, 0, Rule(new StyleMatch.Family(PieceFamilies.Frame), Edge(LineStyle.Solid, LibraryPalette.Next))));
        bench.Settle();
        Assert.True(watch.Generation > settled);
    }

    [Fact]
    public void A_disposed_watch_holds_for_nothing_and_its_leaves_leave_the_next_generation()
    {
        var bench = Cables();
        var claimed = SetId("claimed");
        var watch = bench.Styles.Watch(new StyleMatch.InSet(claimed));
        bench.Settle();
        Assert.Contains(bench.Styles.Newest.Leaves, leaf => leaf.Match is StyleMatch.InSet { Set: var set } && set == claimed);

        watch.Dispose();
        bench.Settle();

        Assert.DoesNotContain(Batches(bench), batch => watch.Holds(batch));
        Assert.DoesNotContain(bench.Styles.Newest.Leaves, leaf => leaf.Match is StyleMatch.InSet { Set: var set } && set == claimed);
    }

    [Fact]
    public void Two_watches_of_one_leaf_keep_it_while_either_is_held()
    {
        var bench = Cables();
        var first = bench.Styles.Watch(new StyleMatch.Family(PieceFamilies.Cable));
        using var second = bench.Styles.Watch(new StyleMatch.Family(PieceFamilies.Cable));
        bench.Settle();

        first.Dispose();
        bench.Settle();

        Assert.Contains(Batches(bench), batch => second.Holds(batch));
    }

    [Fact]
    public void A_watch_on_ended_styles_holds_for_nothing()
    {
        var bench = Cables();
        var batches = Batches(bench);
        bench.Styles.End();

        using var watch = bench.Styles.Watch(new StyleMatch.Everything());

        Assert.DoesNotContain(batches, batch => watch.Holds(batch));
    }

    [Fact]
    public void Holds_allocates_nothing()
    {
        var bench = Cables();
        using var watch = bench.Styles.Watch(new StyleMatch.Any(ValueList.From(new StyleMatch[] { new StyleMatch.Family(PieceFamilies.Cable), new StyleMatch.Not(new StyleMatch.Everything()) })));
        bench.Settle();
        var batches = Batches(bench).ToArray();
        var held = 0;

        Allocations.AssertNone(() =>
        {
            foreach (var batch in batches)
                if (watch.Holds(batch))
                    held++;
        }, "judging every batch");
        Assert.True(held > 0);
    }
}
