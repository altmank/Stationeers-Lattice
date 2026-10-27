using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Openings;
using Lattice.Core.Ports;
using Lattice.Core.Routes;
using Lattice.Core.Support;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Runs;

namespace Lattice.Tests.Routes;

/// <summary>Opening kinds set by hand, as the catalogue will read them by type.</summary>
internal sealed class TestOpeningFacts : IOpeningFacts
{
    private readonly Dictionary<PrefabKey, OpeningKind> _kinds = [];

    public void Set(PrefabKey piece, OpeningKind kind) => _kinds[piece] = kind;

    public Option<OpeningKind> Of(PrefabKey piece) =>
        _kinds.TryGetValue(piece, out var kind) ? Option.Some(kind) : Option<OpeningKind>.None;
}

/// <summary>Support facts set by hand: which pieces allow mounting (frames).</summary>
internal sealed class TestFrames : ISupportFacts
{
    private readonly HashSet<PrefabKey> _frames = [];

    public void Add(PrefabKey piece) => _frames.Add(piece);

    public Option<SupportFacts> Of(PrefabKey piece) =>
        _frames.Contains(piece) ? Option.Some(SupportFacts.Nothing with { AllowsMounting = true }) : Option<SupportFacts>.None;
}

/// <summary>
/// The run bench plus what routes need: a modded hatch (a door no base game has, large grid, on a face, with a power
/// port), a modded scaffold (a frame of a kit no base game has), opening and frame facts set by hand, and a route
/// planner over them. Large cells centre on odd metres: <see cref="Large"/> gives a large cell's centre, and the
/// small cell <c>At(2, 2, 2)</c> is the centre of large cell (0, 0, 0).
/// </summary>
internal sealed class RouteBench
{
    public const string RouteText = """
        lattice-catalogue 3
        piece ModHatch kit=ItemModHatchKit option=0 grid=large
          step material=ItemModHatchKit*1 exit=ItemWrench air=yes gravity=yes
          end 10 0 0 z+ power
          cell 0 0 0 face-x+

        piece ModScaffold kit=ItemModScaffoldKit option=0 grid=large
          step material=ItemModScaffoldKit*1 exit=ItemWrench air=no
          cell 0 0 0 centre
        """;

    /// <param name="limits">The search's limits; the defaults when none.</param>
    /// <param name="extraPieces">More pieces in the catalogue file format, without its header.</param>
    public RouteBench(RouteLimits? limits = null, string? extraPieces = null)
    {
        Runs = new RunBench(extraPieces is null ? RouteText : RouteText + System.Environment.NewLine + System.Environment.NewLine + extraPieces);
        Openings.Set(Catalogue.Named("ModHatch").Key, OpeningKind.Door);
        Openings.Set(Catalogue.Named("StructureCompositeWindowIron").Key, OpeningKind.Window);
        Frames.Add(Catalogue.Named("StructureFrame").Key);
        Frames.Add(Catalogue.Named("ModScaffold").Key);
        Limits = limits ?? RouteLimits.Default;
    }

    public RunBench Runs { get; }

    public TestOpeningFacts Openings { get; } = new();

    public TestFrames Frames { get; } = new();

    public RouteLimits Limits { get; }

    public Lattice.TestKit.Catalogue.FakeCatalogue Catalogue => Runs.Catalogue;

    public RoutePlanner Planner => new(Catalogue, Runs.Grid, Runs.Check, Openings, Frames, OpeningBands.Default, Limits);

    /// <summary>The centre of large cell (i, j, k), as a cell key in decimetres.</summary>
    public static Cell Large(int i, int j, int k) => new(110 + 20 * i, 110 + 20 * j, 110 + 20 * k);

    public static RouteToggles Off => new(false, false, false, false, false);

    public RouteRequest Route(KitKey kit, Cell from, Cell to, RouteToggles? toggles = null) =>
        new(kit, from, to, toggles ?? Off, Option<PlanId>.None, TestBoard.TestMaker);

    public Result<RoutePlan, Reason> Try(RouteRequest request) => Planner.Plan(request, Runs.Boards.View, Runs.Query);

    /// <summary>The route; a route's own run never carries rules, so its report never has a break.</summary>
    public RoutePlan Plan(RouteRequest request)
    {
        var plan = TestKitIds.Must(Try(request));
        if (plan.AsRun.Rules.IsSome || plan.Run.FirstBreak.IsSome)
            throw new System.InvalidOperationException("a route's run has a rule break");
        return plan;
    }

    public Reason Refusal(RouteRequest request) =>
        Try(request).TryGetError(out var why) ? why : throw new System.InvalidOperationException("routed");

    public Turn Sending(Direction from, Direction to) => Runs.Sending(from, to);
}
