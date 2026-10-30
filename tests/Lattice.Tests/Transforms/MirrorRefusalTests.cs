using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Commands;
using Lattice.Core.Transforms;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Board;
using Lattice.Tests.Commands;
using Lattice.Tests.Selectors;
using static Lattice.Tests.Transforms.TransformBench;

namespace Lattice.Tests.Transforms;

/// <summary>
/// A transform that cannot take every ghost it picks is refused whole, never applied in part: one refusal per kind
/// of problem, each listing exactly its ghosts, ascending, in <see cref="Refusal.Ghosts"/>, with its facts as before.
/// </summary>
public sealed class MirrorRefusalTests
{
    private static readonly Transformation Mirror = new Transformation.Mirror(new MirrorPlane(Axis.X, 20));

    private static ValueList<Refusal> Refusals(Outcome outcome) =>
        outcome is Outcome.Refused refused ? refused.Why : throw new System.InvalidOperationException($"applied: {outcome}");

    /// <summary>Three cables that mirror and two machines that have no mirror image, the machines placed between the cables.</summary>
    private static (TransformBench Bench, GhostId[] Mirrorable, GhostId[] Chiral) FiveGhosts()
    {
        var b = new TransformBench();
        var first = b.Build("StructureCableStraight", new Cell(10, 0, 0));
        var chiralA = b.Build("ModChiralMachine", new Cell(15, 0, 0));
        var second = b.Build("StructureCableStraight", new Cell(20, 0, 0));
        var chiralB = b.Build("ModChiralMachine", new Cell(25, 0, 0));
        var third = b.Build("StructureCableStraight", new Cell(30, 0, 0));
        return (b, [first, second, third], [chiralA, chiralB]);
    }

    [Fact]
    public void A_mirror_of_five_ghosts_two_without_a_mirror_image_is_refused_naming_exactly_those_two_and_changes_nothing()
    {
        var (b, _, chiral) = FiveGhosts();
        var before = b.Placed();
        var at = b.T.Board.Revision;

        var refusal = Assert.Single(Refusals(b.Transform(All, Mirror, Copying)));

        Assert.Equal(ReasonCodes.NoMirrorImage, refusal.Why.Code);
        Assert.Equal(chiral.OrderBy(static g => g.Value), refusal.Ghosts);
        Assert.Equal(string.Join(",", chiral.Select(static g => g.Value).OrderBy(static v => v)), refusal.Why.Facts["ghosts"]);
        Assert.Equal(b.T.Catalogue.Named("ModChiralMachine").Key.Hash.ToString(), refusal.Why.Facts["pieces"]);
        Assert.Equal(before, b.Placed());
        Assert.Equal(at, b.T.Board.Revision);
    }

    [Fact]
    public void Resubmitted_without_the_refused_ghosts_the_mirror_takes_the_other_three()
    {
        var (b, mirrorable, chiral) = FiveGhosts();
        var refused = Assert.Single(Refusals(b.Transform(All, Mirror, Copying)));
        var rest = new Selector.Ids(ValueList.From(mirrorable.Where(id => !refused.Ghosts.Contains(id))));

        var copied = TestBoard.Applied(b.Transform(rest, Mirror, Copying));

        Assert.Equal(3, copied.Added.Count);
        Assert.DoesNotContain(copied.Added, g => chiral.Contains(g.Id));
    }

    [Fact]
    public void A_piece_with_no_mirror_and_a_piece_the_catalogue_lacks_give_two_refusals_each_with_its_own_ghosts()
    {
        var c = new TestCommands(null, [.. TestSelect.Pieces(ModdedText)]);
        var known = new FakeCatalogue(c.T.Catalogue.Pieces.Where(static p => p.Name != "ModHandLeft").ToList(), c.T.Catalogue.BrokenToolNames, itemLines: c.T.Catalogue.ItemLines);
        Assert.True(c.Expanders.RegisterSliced(new TransformExpander(new Placements(known, c.T.Grid, new CatalogueAdditions(known))).Descriptor).IsOk);
        var outcomes = new List<Outcome>();
        c.T.Events.Answered += (_, outcome) => outcomes.Add(outcome);
        var chiral = Assert.Single(c.T.Placed(c.T.Draft("ModChiralMachine", new Cell(15, 0, 0))).Added).Id;
        var lost = new[]
        {
            Assert.Single(c.T.Placed(c.T.Draft("ModHandLeft", new Cell(25, 0, 0))).Added).Id,
            Assert.Single(c.T.Placed(c.T.Draft("ModHandLeft", new Cell(35, 0, 0))).Added).Id,
        };

        c.Service.Submit(c.NextId(), new Command.Transform(All, Mirror, new TransformMode.Move()), TestBoard.Alice);
        for (var frame = 0; frame < 100 && outcomes.Count == 0; frame++)
            c.Frame();

        var refusals = Refusals(Assert.Single(outcomes));
        Assert.Equal(2, refusals.Count);
        var unknown = Assert.Single(refusals, static r => r.Why.Code == ReasonCodes.UnknownPiece);
        var noMirror = Assert.Single(refusals, static r => r.Why.Code == ReasonCodes.NoMirrorImage);
        Assert.Equal(lost.OrderBy(static g => g.Value), unknown.Ghosts);
        Assert.Equal(new[] { chiral }, noMirror.Ghosts);
    }

    [Fact]
    public void A_piece_with_a_registered_mirror_partner_is_not_listed()
    {
        var b = new TransformBench();
        using var partners = b.Partners("ModHandLeft", "ModHandRight");
        b.Build("ModHandLeft", new Cell(10, 0, 0));
        var chiral = b.Build("ModChiralMachine", new Cell(20, 0, 0));

        var refusal = Assert.Single(Refusals(b.Transform(All, Mirror, Copying)));

        Assert.Equal(new[] { chiral }, refusal.Ghosts);
    }

    [Fact]
    public void A_ghost_that_targets_a_structure_is_listed_under_its_own_refusal()
    {
        var b = new TransformBench();
        var chiral = b.Build("ModChiralMachine", new Cell(20, 0, 0));
        var standing = new StructureRef(new ReferenceId(500), b.T.Catalogue.Named("StructureCableStraight").Key, new Cell(10, 0, 0));
        var removal = Assert.Single(b.T.Placed(TestBoard.Draft(new Intent.Remove(standing))).Added).Id;

        var refusals = Refusals(b.Transform(new Selector.Ids(ValueList.From(new[] { chiral, removal })), Mirror, Copying));

        Assert.Equal(2, refusals.Count);
        Assert.Equal(new[] { removal }, Assert.Single(refusals, static r => r.Why.Code == ReasonCodes.InvalidValue).Ghosts);
        Assert.Equal(new[] { chiral }, Assert.Single(refusals, static r => r.Why.Code == ReasonCodes.NoMirrorImage).Ghosts);
    }

    [Fact]
    public void A_template_names_its_drafts_in_facts_and_lists_no_ghosts()
    {
        var b = new TransformBench();
        var lost = new GhostDraft(new Intent.Build(new Placement(PrefabHash.Piece("NoSuchPiece"), new Cell(0, 0, 0), Turn.None), Option<Finish>.None),
            Option<PlanId>.None, TestBoard.TestMaker, new Visibility.Everyone(), Option<Label>.None, Option<Tint>.None,
            ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty);
        var template = new Template(TestKitIds.Must(TemplateName.Create("Lost")), ValueList.Of(lost));

        var refusal = Assert.Single(Refusals(b.Run(new Command.PlaceTemplate(template, new Cell(100, 0, 100), Turn.None, NewPlan("From a template")))));

        Assert.Equal("0", refusal.Why.Facts["drafts"]);
        Assert.Empty(refusal.Ghosts);
    }

    [Fact]
    public void Inside_a_batch_the_refused_steps_ghosts_are_listed_with_the_step_fact()
    {
        var (b, _, chiral) = FiveGhosts();
        var batch = new Command.Batch(ValueList.From(new Command[]
        {
            new Command.Place(ValueList.Of(b.T.Draft("StructureCableStraight", new Cell(50, 0, 0)))),
            new Command.Transform(All, Mirror, Copying),
        }));

        var refusal = Assert.Single(Refusals(b.Run(batch)));

        Assert.Equal("1", refusal.Why.Facts["step"]);
        Assert.Equal(chiral.OrderBy(static g => g.Value), refusal.Ghosts);
    }

    [Fact]
    public void The_previews_refusals_equal_the_submits()
    {
        var (b, _, _) = FiveGhosts();
        var transform = new Command.Transform(All, Mirror, Copying);
        Answer<Result<Command, ValueList<Refusal>>> expansion;
        for (var frame = 0; (expansion = b.C.Service.Expansion(transform, TestBoard.Alice)) is not Answer<Result<Command, ValueList<Refusal>>>.Ready && frame < 100; frame++)
            b.C.Frame();

        var previewed = Assert.IsType<Answer<Result<Command, ValueList<Refusal>>>.Ready>(expansion).Value;
        Assert.True(previewed.TryGetError(out var refusals));
        Assert.Equal(refusals, Refusals(b.Run(transform)));
    }

    [Fact]
    public void A_mods_own_expander_refuses_with_one_reason_and_no_ghosts()
    {
        var c = new TestCommands();
        var refused = Reason.Of(ReasonCodes.InvalidValue, RuleId.Contract);
        Assert.True(c.Expanders.Register(new CommandExpander<Command.PlanRun>(
            TestKitIds.Must(ModuleName.Create("mod-runs")), _ => Result<Command, Reason>.Error(refused))).IsOk);
        c.T.Placed(c.T.Draft("StructureCableStraight", new Cell(10, 0, 0)));

        var outcome = c.Submit(new Command.PlanRun(new RunRequest(default, new Cell(0, 0, 0), ValueList<Cell>.Empty, new Cell(5, 0, 0), Option<PlanId>.None, TestBoard.TestMaker)));

        var refusal = Assert.Single(Refusals(outcome));
        Assert.Equal(refused, refusal.Why);
        Assert.Empty(refusal.Ghosts);
    }
}
