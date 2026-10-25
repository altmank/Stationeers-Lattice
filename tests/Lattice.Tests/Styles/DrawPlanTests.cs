using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.TestKit.Allocations;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// The batch plan: batches keyed by piece, signature, 32 m chunk and start; instances in
/// pages of 1023; a toggle moves no instance; things whose look starts together share a batch; reading the plan
/// allocates nothing; a seat that does not draw keeps no plan but still picks and explains.
/// </summary>
public sealed class DrawPlanTests
{
    private static List<Batch> Batches(StyleBench bench)
    {
        var batches = new List<Batch>();
        for (var i = 0; i < bench.Styles.Plan.BatchCount; i++)
            if (bench.Styles.Plan.BatchAt(i).Count > 0)
                batches.Add(bench.Styles.Plan.BatchAt(i));
        return batches;
    }

    [Fact]
    public void Things_share_a_batch_by_piece_signature_and_chunk()
    {
        var bench = new StyleBench();
        bench.Place(
            bench.Draft("StructureFrame", new Cell(10, 10, 10)),
            bench.Draft("StructureFrame", new Cell(30, 10, 10)),
            bench.Draft("StructureFrame", new Cell(10 + 320, 10, 10)),
            bench.Draft("StructureCableStraight", new Cell(5, 5, 5)));

        var batches = Batches(bench);

        Assert.Equal(3, batches.Count);
        Assert.Contains(batches, static b => b.Count == 2 && b.Chunk == new ChunkKey(0, 0, 0));
        Assert.Contains(batches, static b => b.Count == 1 && b.Chunk == new ChunkKey(1, 0, 0));
    }

    // Render's request (63): a placement and an existing part drawn with one look still draw from different copies.
    [Fact]
    public void A_build_beside_a_remove_with_the_same_look_draws_as_two_batches_one_per_part()
    {
        var bench = new StyleBench();
        var standing = new StructureRef(new ReferenceId(7), bench.T.Catalogue.Named("StructureFrame").Key, new Cell(30, 10, 10));
        var resolves = bench.Styles.Resolves;
        var changes = bench.Place(
            bench.Draft("StructureFrame", new Cell(10, 10, 10)),
            Lattice.Tests.Board.TestBoard.Draft(new Intent.Remove(standing)));
        var build = changes.Added[0];
        var remove = changes.Added[1];

        var batches = Batches(bench);

        Assert.Equal(2, batches.Count);
        Assert.Equal(batches[0].Key.Signature, batches[1].Key.Signature);
        Assert.Contains(batches, static b => b.Part == PartKind.Placement && b.Count == 1);
        Assert.Contains(batches, static b => b.Part == PartKind.Existing && b.Count == 1);
        Assert.Equal(1, bench.Styles.Resolves - resolves);
        Assert.True(bench.Styles.Draws(build.Id) && bench.Styles.Draws(remove.Id));
    }

    [Fact]
    public void A_batch_holds_its_instances_in_pages_of_1023_and_removal_keeps_them_packed()
    {
        var bench = new StyleBench();
        var drafts = new GhostDraft[2_500];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft("StructureCableStraight", new Cell(5 * (i % 50), 5, 5 * (i / 50)));
        var placed = bench.Place(drafts).Added;
        var batch = Assert.Single(Batches(bench));

        Assert.Equal(3, batch.Pages);
        Assert.Equal(1023, batch.Page(0).Length);
        Assert.Equal(2_500 - 2 * 1023, batch.Page(2).Length);

        bench.T.Apply(new Command.Remove(Lattice.Tests.Board.TestBoard.Ids(placed[3].Id, placed[1500].Id)));
        bench.Settle();
        Assert.Equal(2_498, batch.Count);
        for (var i = 0; i < batch.Count; i++)
            Assert.Equal(i, batch[i].Thing.BatchSlot);
    }

    [Fact]
    public void A_toggle_moves_no_instance_and_batches_read_the_new_look()
    {
        var bench = new StyleBench();
        var layer = bench.Add(LayerOf("edges", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Edge(LineStyle.Solid, LibraryPalette.Fail))));
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)), bench.Draft("StructureFrame", new Cell(30, 10, 10)));
        var batch = Assert.Single(Batches(bench));
        var version = batch.Version;
        Assert.True(batch.Look.Edge.A > 0);

        layer.Disable();

        Assert.Equal(version, batch.Version);
        Assert.Equal(0, batch.Look.Edge.A);
    }

    [Fact]
    public void Things_whose_look_starts_together_share_a_batch_and_a_later_start_gets_its_own()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("selected", StyleBand.Selection, 0, Rule(new StyleMatch.InSet(LibrarySets.Selected), new Style
        {
            Glow = Option.Some<GlowLook>(new GlowLook.Lit(new Paint.Token(LibraryPalette.Select), Fraction.One)),
            Ease = Option.Some(Must(Duration.Create(0.2f))),
        })));
        var drafts = new GhostDraft[6];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft("StructureFrame", new Cell(10 + 20 * i, 10, 10));
        var ghosts = bench.Place(drafts).Added;
        var selected = Must(bench.Styles.Set(LibrarySets.Selected));

        selected.Replace(ValueList.From(new[] { ghosts[0].Id, ghosts[1].Id, ghosts[2].Id }));
        bench.Frame();
        selected.Add(ValueList.Of(ghosts[3].Id));

        var animated = Batches(bench).FindAll(static b => b.Start != 0);
        Assert.Equal(2, animated.Count);
        Assert.Contains(animated, static b => b.Count == 3);
        Assert.Contains(animated, static b => b.Count == 1);
    }

    [Fact]
    public void Reading_the_plan_allocates_nothing()
    {
        var bench = new StyleBench();
        var drafts = new GhostDraft[300];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft("StructureFrame", new Cell(10 + 20 * (i % 30), 10, 10 + 20 * (i / 30)));
        bench.Place(drafts);
        var plan = bench.Styles.Plan;

        Allocations.AssertNone(() =>
        {
            var seen = 0L;
            for (var b = 0; b < plan.BatchCount; b++)
            {
                var batch = plan.BatchAt(b);
                seen += batch.Look.Fill.A + batch.Version;
                for (var p = 0; p < batch.Pages; p++)
                    seen += batch.Page(p).Length;
            }

            for (var l = 0; l < plan.LabelCount; l++)
                seen += plan.LabelAt(l).Label.Priority;
            _ = plan.Clip;
            Assert.True(seen >= 0);
        }, "reading the draw plan");
    }

    [Fact]
    public void A_seat_that_does_not_draw_keeps_no_plan_but_picks_and_explains()
    {
        var bench = new StyleBench(drawing: false);
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        Assert.Equal(0, bench.Styles.Plan.BatchCount);
        Assert.True(bench.Styles.Pick(Must(PickRay.Create(new WorldPoint(1f, 10f, 1f), new WorldVector(0f, -1f, 0f), 20f)), PickOptions.Default).TryGet(out _));
        Assert.True(bench.Styles.Explain(ghost.Id).TryGet(out _));
    }
}
