using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit;
using Lattice.Tests.Board;

namespace Lattice.Tests.Sync;

/// <summary>
/// <see cref="IPlans.AsTemplate"/> of an active plan on a client answers from the client's board, which sync keeps equal
/// to the host's: the same template as the host gives for the same board. A finished plan's file is the host's, so a
/// client refuses it module-missing.
/// </summary>
public sealed class ClientTemplateTests
{
    private static readonly TemplateName Name = TestKitIds.Must(TemplateName.Create("Copy"));

    private static (SyncBench Bench, GhostBoard Client, PlanId Plan) HostAndClient()
    {
        var bench = new SyncBench();
        var client = new TestBoard().Board;
        bench.Join(1, 7, client);
        bench.Frame();
        var host = bench.Host.T;
        var finish = TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(4))), Option.Some(TestKitIds.Must(ThingName.Create("North"))), Option<Setting>.None));
        var create = new Command.CreatePlan(new PlanDraft(TestKitIds.Must(PlanName.Create("Hall")), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty))
        {
            Ghosts = ValueList.From(new[]
            {
                host.Draft("StructureFrame", new Cell(30, 10, 50)),
                host.Draft("StructureCableStraight", new Cell(105, 100, 100), TestBoard.TurnSending(Lattice.Core.Ports.Direction.ZPlus, Lattice.Core.Ports.Direction.XPlus, host.Grid)),
                host.Draft("StructureCableStraight", new Cell(110, 100, 100)) with
                {
                    Intent = new Intent.Build(new Placement(host.Catalogue.Named("StructureCableStraight").Key, new Cell(110, 100, 100), Turn.None), Option.Some(finish)),
                    Label = Option.Some(TestKitIds.Must(Label.Create("feed"))),
                    Groups = ValueSet.Of(TestKitIds.Must(GroupName.Create("Power"))),
                },
            }),
        };
        var plan = Assert.IsType<Outcome.Applied>(bench.Host.Submit(create)).Changes.PlansOpened[0].Id;
        bench.Frames(3);
        return (bench, client, plan);
    }

    [Fact]
    public void A_client_gives_the_template_the_host_gives_for_the_same_active_plan()
    {
        var (bench, client, plan) = HostAndClient();

        var onHost = TestKitIds.Must(new PlansView(bench.Host.T.Board, TestBoard.Alice).AsTemplate(plan, Name));
        var onClient = TestKitIds.Must(new PlansView(client, TestBoard.Alice).AsTemplate(plan, Name));

        Assert.Equal(3, onHost.Ghosts.Count);
        Assert.Equal(onHost, onClient);
        Assert.Equal(onHost.Ghosts.Select(static g => ((Intent.Build)g.Intent).Target), onClient.Ghosts.Select(static g => ((Intent.Build)g.Intent).Target));
    }

    [Fact]
    public void After_the_host_moves_a_ghost_the_client_still_gives_the_hosts_template()
    {
        var (bench, client, plan) = HostAndClient();
        var host = bench.Host.T;
        var frame = host.Board.Index.Ghosts.Values.First(static g => g.Shape.TryGet(out var s) && s.Placement.At == new Cell(30, 10, 50));
        Assert.IsType<Outcome.Applied>(bench.Host.Submit(new Command.Move(
            ValueMap.Of(frame.Id, new Placement(frame.Ghost.Intent is Intent.Build b ? b.Target.Piece : default, new Cell(70, 10, 50), Turn.None)), Option<Revision>.None)));
        bench.Frames(3);

        Assert.Equal(
            TestKitIds.Must(new PlansView(host.Board, TestBoard.Alice).AsTemplate(plan, Name)),
            TestKitIds.Must(new PlansView(client, TestBoard.Alice).AsTemplate(plan, Name)));
    }

    [Fact]
    public void A_finished_plan_is_still_refused_on_a_client_as_module_missing()
    {
        var (_, client, _) = HostAndClient();

        var refused = new PlansView(client, TestBoard.Alice).AsTemplate(new PlanId(9001), Name);

        Assert.True(refused.TryGetError(out var why));
        Assert.Equal(ReasonCodes.ModuleMissing, why.Code);
        Assert.Equal("saving", why.Facts["module"]);
    }
}
