using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Catalogue;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Transforms;
using Xunit.Abstractions;

namespace Lattice.Tests.Written;

/// <summary>
/// <see cref="IPlans.WriteTemplate"/> and <see cref="IPlans.ReadTemplate"/>: the template form as the contract promises it.
/// Written without plan fields; read whole or refused, never in part; the same on every seat.
/// </summary>
public sealed class TemplateTextTests(ITestOutputHelper output)
{
    private static readonly ModId Mod = TestKitIds.Mod;

    private static IPlans Plans() => new PlansView(new TestBoard().Board, TestBoard.Alice);

    private static T Must<T>(Result<T, Reason> result) where T : notnull => TestKitIds.Must(result);

    private static GhostDraft Draft(Intent intent, Visibility? visibility = null) => new(
        intent, Option<PlanId>.None, TestBoard.TestMaker, visibility ?? new Visibility.Everyone(),
        Option<Label>.None, Option<Tint>.None, ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty);

    /// <summary>Builds with a turn and a finish, a label, a tint, groups, an attachment and team visibility.</summary>
    private static Template Rich()
    {
        var cable = PrefabHash.Piece("StructureCableStraight");
        var light = PrefabHash.Piece("StructureAreaPowerControl");
        var finish = Must(Finish.Create(Option.Some(Must(PaintColour.Create(3))), Option.Some(Must(ThingName.Create("Hall light"))), Option.Some(Must(Setting.Create(0.5)))));
        var looked = Draft(new Intent.Build(new Placement(light, new Cell(10, 0, 10), Must(Turn.Create(5))), Option.Some(finish)),
            new Visibility.Team(Must(TeamName.Create("Builders")))) with
        {
            Label = Option.Some(Must(Label.Create("North light"))),
            Tint = Option.Some(new Tint(10, 200, 30, 255)),
            Groups = ValueSet.From(new[] { Must(GroupName.Create("Lights")), Must(GroupName.Create("Hall")) }),
            Attached = ValueMap.Of(Must(AttachmentKey.Create(Mod, "note")), Must(Attachment.Create("two words, 100% #1"))),
        };
        return new Template(Must(TemplateName.Create("Hall kit")), ValueList.From(new[]
        {
            Draft(new Intent.Build(new Placement(cable, new Cell(0, 0, 0), Turn.None), Option<Finish>.None)),
            Draft(new Intent.Build(new Placement(cable, new Cell(5, 0, 0), Must(Turn.Create(1))), Option<Finish>.None)),
            looked,
        }));
    }

    /// <summary>A template written by contract 0.10's writer: every later build reads it as <see cref="Rich"/>.</summary>
    private const string Golden = """
        lattice-template 1 name=Hall%20kit
        build=605357050@0,0,0 made=testkit/board-test
        build=605357050@5,0,0/1 made=testkit/board-test
        build=1999523701@10,0,10/5 paint=3 name=Hall%20light setting=0.5 made=testkit/board-test vis=team:Builders label=North%20light tint=0ac81eff group=Hall group=Lights data.testkit/note=two%20words,%20100%25%20#1

        """;

    [Fact]
    public void A_template_reads_back_equal_to_what_was_written()
    {
        var plans = Plans();
        var template = Rich();

        Assert.Equal(template, Must(plans.ReadTemplate(plans.WriteTemplate(template))));
    }

    [Fact]
    public void Text_written_by_0_10_reads_as_the_same_template()
    {
        var golden = Golden.Replace("\r\n", "\n");

        Assert.Equal(Rich(), Must(Plans().ReadTemplate(golden)));
        Assert.Equal(golden, Plans().WriteTemplate(Rich()));
    }

    [Fact]
    public void A_plans_template_round_trips_exactly()
    {
        var b = new TransformBench();
        var plan = Must(b.Plans.Find(b.T.Board.Index.Plans.Keys.First()).Match(static p => Result<Plan, Reason>.Ok(p), static () => Result<Plan, Reason>.Error(Reason.Of(ReasonCodes.NotFound, RuleId.Contract)))).Id;
        b.Build("StructureCableStraight", new Cell(10, 0, 0), b.Sending(Direction.ZPlus, Direction.XPlus), Option.Some(plan));
        b.Build("ModChiralMachine", new Cell(15, 0, 0), default, Option.Some(plan));
        var template = Must(b.Plans.AsTemplate(plan, Must(TemplateName.Create("Copied"))));

        Assert.Equal(template, Must(b.Plans.ReadTemplate(b.Plans.WriteTemplate(template))));
    }

    [Fact]
    public void A_drafts_plan_is_not_written_and_reads_back_as_none()
    {
        var plans = Plans();
        var withPlan = new Template(Must(TemplateName.Create("Planned")), ValueList.Of(Rich().Ghosts[0] with { Plan = Option.Some(new PlanId(7)) }));

        var text = plans.WriteTemplate(withPlan);

        Assert.DoesNotContain("plan=", text);
        Assert.True(Must(plans.ReadTemplate(text)).Ghosts[0].Plan.IsNone);
    }

    public static TheoryData<string, string, string> Refusals() => new()
    {
        { "empty", "", "invalid-value" },
        { "a plan file", "lattice-plan 1\n", "invalid-value" },
        { "schema 0", "lattice-template 0 name=Hall\n", "invalid-value" },
        { "no name", "lattice-template 1\n", "invalid-value" },
        { "a bad name", "lattice-template 1 name=\n", "invalid-value" },
        { "a newer schema", "lattice-template 2 name=Hall\n", "unknown-kind" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Text_that_is_not_a_template_this_version_reads_is_refused(string what, string text, string code)
    {
        var read = Plans().ReadTemplate(text);

        Assert.True(read.TryGetError(out var why), what);
        Assert.Equal(code, why.Code.Value);
    }

    [Fact]
    public void A_draft_with_an_unknown_intent_or_an_unknown_field_refuses_the_template_naming_the_drafts()
    {
        var plans = Plans();
        var lines = plans.WriteTemplate(Rich()).Split('\n');
        lines[1] = "fly=0,0,0 made=testkit/board-test";
        lines[3] += " sparkle=yes";

        var why = Prefabs.Error(plans.ReadTemplate(string.Join("\n", lines)));

        Assert.Equal(ReasonCodes.UnknownKind, why.Code);
        Assert.Equal("0,2", why.Facts["drafts"]);
    }

    [Fact]
    public void A_cut_last_line_is_refused_with_its_line()
    {
        var text = Plans().WriteTemplate(Rich());

        var why = Prefabs.Error(Plans().ReadTemplate(text.Substring(0, text.Length - 5)));

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("4", why.Facts["line"]);
    }

    [Fact]
    public void A_damaged_line_is_refused_with_its_line()
    {
        var lines = Plans().WriteTemplate(Rich()).Split('\n');
        lines[2] = lines[2].Replace("made=testkit/board-test", "made=Not A Maker");

        var why = Prefabs.Error(Plans().ReadTemplate(string.Join("\n", lines)));

        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Equal("3", why.Facts["line"]);
    }

    [Fact]
    public void Unknown_header_fields_are_dropped()
    {
        var text = Plans().WriteTemplate(Rich());
        var newer = text.Insert(text.IndexOf('\n'), " colour-scheme=dusk");

        Assert.Equal(Rich(), Must(Plans().ReadTemplate(newer)));
    }

    [Fact]
    public void A_piece_this_world_lacks_reads_and_placing_it_is_refused_naming_the_draft()
    {
        var b = new TransformBench();
        var lost = new Template(Must(TemplateName.Create("Lost")), ValueList.Of(Draft(new Intent.Build(new Placement(PrefabHash.Piece("NoSuchPiece"), new Cell(0, 0, 0), Turn.None), Option<Finish>.None))));
        var read = Must(b.Plans.ReadTemplate(b.Plans.WriteTemplate(lost)));

        var refusal = TestBoard.Refused(b.Run(new Command.PlaceTemplate(read, new Cell(100, 0, 100), Turn.None, TransformBench.NewPlan("Stamp"))));

        Assert.Equal(lost, read);
        Assert.Equal("0", refusal.Why.Facts["drafts"]);
    }

    [Fact]
    public void A_placed_template_is_asked_with_the_plan_it_opens()
    {
        var b = new TransformBench();
        var asked = new List<AccessRequest>();
        Assert.True(b.T.Policies.Install(new AccessPolicy(static (_, _) => true, static (_, _) => true, request =>
        {
            asked.Add(request);
            return Result<Unit, Reason>.Ok(Unit.Value);
        })).IsOk);

        var set = TestBoard.Applied(b.Run(new Command.PlaceTemplate(Rich(), new Cell(100, 0, 100), Turn.None, TransformBench.NewPlan("Stamp"))));

        Assert.NotEmpty(asked);
        Assert.All(asked, request => Assert.Equal(Option.Some(set.PlansOpened[0]), request.PlanRecord));
    }

    [Fact]
    public void A_host_and_a_client_with_no_modules_read_and_write_the_same_text()
    {
        using var host = Lattice.Testing.TestWorld.Start(Lattice.Testing.TestScenario.Empty).Match(static w => w, static why => throw new InvalidOperationException(why.ToString()));
        var bare = Plans();

        var written = host.System.Plans.WriteTemplate(Rich());

        Assert.Equal(written, bare.WriteTemplate(Rich()));
        Assert.Equal(Must(bare.ReadTemplate(written)), Must(host.System.Plans.ReadTemplate(written)));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Ten_thousand_drafts_write_and_read_in_time()
    {
        var plans = Plans();
        var cable = PrefabHash.Piece("StructureCableStraight");
        var drafts = new GhostDraft[10_000];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = Draft(new Intent.Build(new Placement(cable, new Cell(5 * (i % 100), 0, 5 * (i / 100)), Turn.None), Option<Finish>.None));
        var template = new Template(Must(TemplateName.Create("Big")), ValueList.From(drafts));
        plans.ReadTemplate(plans.WriteTemplate(template));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        var text = plans.WriteTemplate(template);
        var written = clock.Elapsed;
        var read = Must(plans.ReadTemplate(text));
        var total = clock.Elapsed;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"write {written.TotalMilliseconds:F1} ms, read {(total - written).TotalMilliseconds:F1} ms, {bytes / 1024} KiB for {text.Length / 1024} KiB of text");
        Assert.Equal(10_000, read.Ghosts.Count);
        Assert.True(total < TimeSpan.FromSeconds(1), $"{total.TotalMilliseconds} ms");
        Assert.True(bytes < 64L * 1024 * 1024, $"{bytes} B");
    }
}
