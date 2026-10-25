using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Transforms;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Board;
using Lattice.Tests.Commands;
using Lattice.Tests.Selectors;

namespace Lattice.Tests.Transforms;

/// <summary>
/// The command service with the transforms and templates expanders registered (sliced), over the hand catalogue plus
/// modded pieces: a machine whose three ports (input x+, output y+, waste z+) have no mirror image, a corner of a
/// network kind no base game has, and a left and right hand of a machine (input x+ or x-) that are each other's mirror
/// image once a mod registers them as partners (<see cref="Additions"/>); and the face pieces of <see cref="FaceSpotTests"/>. Small cells are keys in multiples of 5; large cells centre on keys 10 mod 20.
/// </summary>
internal sealed class TransformBench
{
    public const string ModdedText = """
        piece ModChiralMachine kit=ItemModMachineKit option=0 grid=small
          step material=ItemModMachineKit*1 exit=ItemWrench air=no
          end 0 0 0 x+ gas role=input
          end 0 0 0 y+ power role=output
          end 0 0 0 z+ gas role=waste
          cell 0 0 0 device
          collision 4

        piece ModSteamCorner kit=ItemModSteamPipe option=0 grid=small
          step material=ItemModSteamPipe*1 exit=ItemWrench air=no
          end 0 0 0 x+ mod:Steam:64 reach=350
          end 0 0 0 z+ mod:Steam:64 reach=350
          cell 0 0 0 pipe
          collision 1

        piece ModHandLeft kit=ItemModHandKit option=0 grid=small
          step material=ItemModHandKit*1 exit=ItemWrench air=no
          end 0 0 0 x+ gas role=input
          end 0 0 0 y+ power role=output
          end 0 0 0 z+ gas role=waste
          cell 0 0 0 device
          collision 4

        piece ModHandRight kit=ItemModHandKit option=1 grid=small
          step material=ItemModHandKit*1 exit=ItemWrench air=no
          end 0 0 0 x- gas role=input
          end 0 0 0 y+ power role=output
          end 0 0 0 z+ gas role=waste
          cell 0 0 0 device
          collision 4
        """;

    public TransformBench()
    {
        C = new TestCommands(null, [.. TestSelect.Pieces(ModdedText), .. FaceSpotTests.Pieces()]);
        Additions = new CatalogueAdditions(C.T.Catalogue);
        Placements = new Placements(C.T.Catalogue, C.T.Grid, Additions);
        Assert.True(C.Expanders.RegisterSliced(new TransformExpander(Placements).Descriptor).IsOk);
        Assert.True(C.Expanders.RegisterSliced(new TemplateExpander(Placements).Descriptor).IsOk);
        Assert.True(C.Expanders.RegisterSliced(new TemplateInExpander(Placements, C.T.Board).Descriptor).IsOk);
        C.T.Events.Answered += (_, outcome) => Answers.Add(outcome);
    }

    public TestCommands C { get; }

    public TestBoard T => C.T;

    public Placements Placements { get; }

    /// <summary>The world's catalogue additions; the mirror partners <see cref="Placements"/> reads.</summary>
    public CatalogueAdditions Additions { get; }

    /// <summary>Registers two pieces, by name, as each other's mirror image.</summary>
    public IDisposable Partners(string piece, string mirror) => TestKitIds.Must(Additions.Register(
        new CatalogueAddition.MirrorPartner(T.Catalogue.Named(piece).Key, T.Catalogue.Named(mirror).Key)));

    public Shapes Shapes => new(T.Catalogue, T.Grid);

    public IPlans Plans => new PlansView(T.Board, TestBoard.Alice);

    public List<Outcome> Answers { get; } = new();

    public static PlanDraft NewPlan(string name) =>
        new(TestKitIds.Must(PlanName.Create(name)), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

    public Turn Sending(Direction from, Direction to) => TestBoard.TurnSending(from, to, T.Grid);

    public GhostId Build(string piece, Cell at, Turn turn = default, Option<PlanId> plan = default) =>
        Assert.Single(T.Placed(T.Draft(piece, at, turn, plan)).Added).Id;

    /// <summary>Submits for Alice and runs frames until the host answers.</summary>
    public Outcome Run(Command command, Actor? actor = null)
    {
        var before = Answers.Count;
        var submission = C.Service.Submit(C.NextId(), command, actor ?? TestBoard.Alice);
        if (submission is Submission.Answered answered)
            return answered.Outcome;
        for (var frame = 0; frame < 1000 && Answers.Count == before; frame++)
            C.Frame();
        return Answers.Count > before ? Answers[before] : throw new InvalidOperationException("never answered");
    }

    public Outcome Transform(Selector which, Transformation how, TransformMode? mode = null) =>
        Run(new Command.Transform(which, how, mode ?? new TransformMode.Move()));

    /// <summary>Every placement on the board, as a set.</summary>
    public HashSet<Placement> Placed()
    {
        var placed = new HashSet<Placement>();
        foreach (var entry in T.Board.Index.Ghosts.Values)
            if (entry.Ghost.Intent is Intent.Build build)
                placed.Add(build.Target);
        return placed;
    }

    public static Selector All => new Selector.MadeBy(TestBoard.TestMaker);

    /// <summary>The maker of copies the tests place: not the originals' maker, so a copy shows whose it is.</summary>
    public static readonly Maker Copier = TestKitIds.Must(Maker.Create(TestKitIds.Mod, "copier"));

    public static TransformMode Copying => new TransformMode.Copy(Copier);

    public static Reason Refusal(Outcome outcome) => TestBoard.Refused(outcome).Why;
}
