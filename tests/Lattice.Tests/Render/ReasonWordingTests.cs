using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Render.Labels;
using Lattice.TestKit;
using Lattice.TestKit.Allocations;
using Lattice.Tests.Styles;
using UnityEngine;
using static Lattice.Tests.Styles.StyleBench;
using Renderer = Lattice.Render.Renderer;

namespace Lattice.Tests.Render;

/// <summary>
/// What a hovered finding says: plain words for every reason Lattice's own rules put in a finding, the style's words
/// first, the game's message for a refusal that has one, a plain fallback for a mod's code, never the code itself.
/// </summary>
public sealed class ReasonWordingTests
{
    private static readonly TextStyle Plain = new(null!, 24f, new Rgba(255, 255, 255, 255));

    private static readonly RuleId From = RuleId.Contract;

    private static readonly ReasonCode ModCode = TestKitIds.Must(ReasonCode.Create(TestKitIds.Must(ModId.Create("othermod")), "some-code"));

    private static readonly ReasonCode[] FindingCodes =
    {
        ReasonCodes.NeedsSupport, ReasonCodes.CannotShare, ReasonCodes.EndsCollide, ReasonCodes.FaceOverRating,
        ReasonCodes.GameRefused, ReasonCodes.TargetGone, ReasonCodes.Different, ReasonCodes.OffSnap,
        ReasonCodes.TurnNotAllowed, ReasonCodes.NoTool, ReasonCodes.NoPiece, ReasonCodes.FlowBlocked,
        ReasonCodes.Cycle, ReasonCodes.UnknownKind, ReasonCodes.UnknownPiece, ReasonCodes.RuleTimedOut,
        ReasonCodes.RuleSwitchedOff, ReasonCodes.SwapOnAirRoom, ReasonCodes.CutsPower, ReasonCodes.GasIndoors,
        ReasonCodes.OxidiserOutside, ReasonCodes.HotGas, ReasonCodes.ControlsUnreachable, ReasonCodes.RunThroughDoor,
        ReasonCodes.OnWindow, ReasonCodes.PaintIndoors, ReasonCodes.TerrainInTheWay,
    };

    private sealed class Hover
    {
        private readonly StyleBench _bench = new();
        private readonly RenderRig _render = new();
        private readonly FakeLabelSurface _surface = new();
        private readonly Renderer _renderer;
        private readonly GhostId _ghost;

        public Hover(TextStyle? style = null)
        {
            _renderer = RenderResults.Ok(Renderer.Over(_bench.Styles.Plan, _render.Ports, _bench.Scheduler, _bench.Styles.Watch,
                new LabelPorts(_surface, new FakeLabelFacts(), style ?? Plain, _bench.Clock)));
            _bench.Styles.Projector = _renderer.Labels;
            _ghost = _bench.PlaceOne("StructureFrame", new Cell(10, 10, 10)).Id;
            _bench.Add(LayerOf("reasons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), new Style
            {
                Label = Option.Some<LabelLook>(new LabelLook.Shown(new LabelText.FindingReason(), LibraryText.Default, LabelAnchor.Above, 0, Must(Metres.Create(500f)))),
            })));
            RenderRig.Settle(_bench, _renderer);
        }

        public string Says(bool warns, Reason why)
        {
            _bench.Verdicts.Raise((_ghost, warns ? new Finding.Warns(_ghost, why) : new Finding.WillFail(_ghost, why)));
            Tick();
            return Assert.Single(_surface.Slots, static s => s.Visible).Words!;
        }

        public void Tick()
        {
            _bench.Styles.View(new WorldPoint(0f, 1f, 1f));
            _bench.Clock.Advance(LabelChooser.Period);
            _bench.Frame();
            _renderer.Frame(0f);
        }

        public void Steady() => Allocations.AssertNone(() => _renderer.Frame(0f), "a steady hovered finding");
    }

    private static Reason Of(ReasonCode code) => Reason.Of(code, From);

    private static string Fails(Reason why, TextStyle? style = null) => new Hover(style).Says(false, why);

    private static string Warns(Reason why, TextStyle? style = null) => new Hover(style).Says(true, why);

    [Fact]
    public void A_lattice_code_shows_its_plain_words()
    {
        Assert.Equal("Nothing to stand on or hang from", Fails(Of(ReasonCodes.NeedsSupport)));
        Assert.Equal("Oxidiser would reach the outside", Warns(Of(ReasonCodes.OxidiserOutside)));
    }

    [Fact]
    public void Every_code_the_rules_put_in_a_finding_has_plain_words_that_are_not_the_code()
    {
        foreach (var code in FindingCodes)
        {
            Assert.True(DefaultWording.Map.TryGetValue(code, out var words), code.Value);
            Assert.False(string.IsNullOrWhiteSpace(words.Value), code.Value);
            Assert.DoesNotContain(code.Value, words.Value);
        }
    }

    [Fact]
    public void A_style_that_words_a_code_wins_over_lattice_s_words()
    {
        var style = Plain with { Wording = ValueMap.Of(ReasonCodes.NeedsSupport, TestKitIds.Must(Label.Create("X"))) };

        Assert.Equal("X", Fails(Of(ReasonCodes.NeedsSupport), style));
    }

    [Fact]
    public void A_refusal_from_the_game_shows_the_game_s_message_or_a_plain_line_without_one()
    {
        var said = new Reason(ReasonCodes.GameRefused, ValueMap<string, string>.Empty, Option.Some("Not enough room"), From);
        var silent = Of(ReasonCodes.GameRefused);

        Assert.Equal("Not enough room", Fails(said));
        Assert.Equal("The game won't let you build this here", Fails(silent));
    }

    [Fact]
    public void A_mod_code_with_no_words_shows_a_plain_fallback_and_never_the_code()
    {
        var failing = Fails(Of(ModCode));
        var warning = Warns(Of(ModCode));

        Assert.Equal("Can't be built as planned", failing);
        Assert.Equal("Check this before building", warning);
        Assert.DoesNotContain("some-code", failing + warning);
    }

    [Fact]
    public void A_fact_can_make_the_words_more_exact()
    {
        var broken = Reason.Of(ReasonCodes.Different, From, ValueMap.Of("problem", "broken"));
        var turned = Reason.Of(ReasonCodes.Different, From, ValueMap.Of("problem", "turn"));
        var paint = Reason.Of(ReasonCodes.NoTool, From, ValueMap.Of("way", "paint"));
        var building = Reason.Of(ReasonCodes.NoTool, From, ValueMap.Of("way", "build"));

        Assert.Equal("The piece built here is broken", Fails(broken));
        Assert.Equal("This piece is already built here, turned another way", Fails(turned));
        Assert.Equal("No spray can of this colour", Fails(paint));
        Assert.Equal("No tool for this step", Fails(building));
    }

    [Fact]
    public void A_steady_hovered_finding_allocates_nothing()
    {
        var hover = new Hover();
        hover.Says(false, Of(ReasonCodes.NeedsSupport));
        hover.Tick();

        hover.Steady();
    }
}
