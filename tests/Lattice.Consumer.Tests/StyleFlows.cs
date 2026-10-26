using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A renderer mod's flows over the style contract (what a planner UI draws: overlays, fades, clips), written against
/// <c>Lattice.Api</c> alone. Compiled here and into Lattice.Tests, which derives it over a recording
/// <see cref="IGhostStyles"/> and a real host world. A derived class gives the styles, the system, a player, drafts at
/// free spots, built structures, and the observations a renderer test needs that the contract does not expose
/// (whether a drafts handle draws anything) plus what the picture would answer (picks, stories, structure matches).
/// </summary>
public abstract class StyleFlows
{
    private const int MostFrames = 32;

    private protected static readonly ModId Planner = Must(ModId.Create("planner"));

    private protected abstract IGhostStyles Styles { get; }

    private protected abstract IGhostSystem System { get; }

    private protected abstract Actor Player { get; }

    /// <summary>A draft of a ghost at a free spot; a different spot for each <paramref name="spot"/>.</summary>
    private protected abstract GhostDraft Draft(int spot);

    /// <summary>A built structure the world knows, with no ghost on it.</summary>
    private protected abstract StructureRef Built { get; }

    /// <summary>Runs world frames until the world has nothing left to work out.</summary>
    private protected abstract void Frames();

    /// <summary>Ends one drawing frame.</summary>
    private protected abstract void EndFrame();

    /// <summary>Whether a drafts handle draws anything now.</summary>
    private protected abstract bool Shows(DraftsId drafts);

    /// <summary>Picks hit this from now on.</summary>
    private protected abstract void WillPick(PickHit hit);

    /// <summary>The ghost is drawn and its look has this story.</summary>
    private protected abstract void WillExplain(GhostId ghost, StyleStory story);

    /// <summary>The match holds for the built structure.</summary>
    private protected abstract void WillMatch(StyleMatch match, StructureRef structure);

    private protected static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static value => value, static why => throw new Xunit.Sdk.XunitException($"refused: {why}"));

    private static Reason Refused<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static value => throw new Xunit.Sdk.XunitException($"accepted: {value}"), static why => why);

    private static StyleLayerId LayerId(string name) => Must(StyleLayerId.Create(Planner, name));

    private static Style EdgeOf(LineStyle line, PaletteKey colour) => new()
    {
        Edge = Option.Some<EdgeLook>(new EdgeLook.Line(line, Must(Pixels.Create(2f)), new Paint.Token(colour))),
    };

    private static StyleLayerDescriptor Layer(string name, StyleBand band, params StyleRule[] rules) =>
        new(LayerId(name), band, 0, ValueList.From(rules));

    private T Settled<T>(Func<Answer<T>> ask) where T : notnull
    {
        for (var i = 0; i < MostFrames; i++)
        {
            if (ask() is Answer<T>.Ready ready)
                return ready.Value;
            Frames();
        }

        throw new Xunit.Sdk.XunitException("the answer stayed pending");
    }

    private Ghost Place(GhostDraft draft)
    {
        var submission = System.Commands.Submit(CommandId.New(), new Command.Place(ValueList.Of(draft)), Player);
        var answered = Assert.IsType<Submission.Answered>(submission);
        return Assert.Single(Assert.IsType<Outcome.Applied>(answered.Outcome).Changes.Added);
    }

    [Fact]
    public void Need_1_a_viewer_layer_switches_on_and_off_without_re_adding()
    {
        var layer = Must(Styles.Add(Layer("failures", StyleBand.Meaning,
            new StyleRule(new StyleMatch.Finding(FindingKind.WillFail, Option<ReasonCode>.None), EdgeOf(LineStyle.Dashed, LibraryPalette.Fail)))));

        layer.Disable();
        Assert.False(layer.Enabled);
        layer.Enable();

        Assert.True(layer.Enabled);
        Assert.Equal(LayerId("failures"), layer.Id);
    }

    [Fact]
    public void Need_1_a_layer_id_is_held_until_its_layer_is_disposed()
    {
        var layer = Must(Styles.Add(Layer("mine", StyleBand.View)));

        Assert.Equal(ReasonCodes.AlreadyRegistered, Refused(Styles.Add(Layer("mine", StyleBand.View))).Code);
        layer.Dispose();
        Must(Styles.Add(Layer("mine", StyleBand.View))).Dispose();
    }

    [Fact]
    public void Need_1_a_default_layer_id_or_unnamed_band_is_refused()
    {
        Assert.Equal(ReasonCodes.InvalidValue, Refused(Styles.Add(new StyleLayerDescriptor(default, StyleBand.View, 0, ValueList<StyleRule>.Empty))).Code);
        Assert.Equal(ReasonCodes.InvalidValue, Refused(Styles.Add(Layer("odd", (StyleBand)99))).Code);
    }

    [Fact]
    public void Need_2_and_gap_1_drafts_show_at_once_then_the_preview_replaces_them_then_the_answer_hands_them_over()
    {
        var id = Must(DraftsId.Create(Planner, "cursor"));
        var drafts = Must(Styles.Drafts(id));
        var draft = Draft(0);

        drafts.Show(ValueList.Of(draft));
        Assert.True(Shows(id));

        var command = new Command.Place(ValueList.Of(draft));
        var preview = Must(Settled(() => System.Preview.Of(command, Player)).Match(
            static p => Result<Preview, Reason>.Ok(p),
            static refusals => Result<Preview, Reason>.Error(refusals[0].Why)));
        drafts.Show(preview);
        Assert.True(Shows(id));

        drafts.Hand(System.Commands.Submit(CommandId.New(), command, Player));
        for (var i = 0; i < MostFrames && Shows(id); i++)
            EndFrame();
        Assert.False(Shows(id));
        Assert.Same(drafts, Must(Styles.Drafts(id)));
    }

    [Fact]
    public void Need_2_disposing_drafts_clears_them_and_frees_the_id()
    {
        var id = Must(DraftsId.Create(Planner, "drag"));
        var drafts = Must(Styles.Drafts(id));
        drafts.Show(ValueList.Of(Draft(1)));

        drafts.Dispose();

        Assert.False(Shows(id));
        Assert.NotSame(drafts, Must(Styles.Drafts(id)));
    }

    [Fact]
    public void Need_3_and_gap_2_a_pick_filters_by_match_and_drafts_and_says_what_it_hit()
    {
        var ghost = Place(Draft(2));
        Frames();
        WillPick(new PickHit.OnGhost(ghost.Id, PartKind.Placement, new Cell(0, 0, 0), Face.PlusY, new WorldPoint(0f, 0.5f, 0f), 3f));
        var ray = RayOnto(ghost);
        var options = PickOptions.Default with
        {
            Only = Option.Some<StyleMatch>(new StyleMatch.Not(new StyleMatch.State(StateKind.Stale))),
            Drafts = DraftPicks.Exclude,
            BeyondClip = BeyondClipPicks.Exclude,
        };

        var hit = Styles.Pick(ray, options);

        Assert.True(hit.TryGet(out var found));
        Assert.Equal($"ghost {ghost.Id.Value}", Describe(found));
        Assert.True(Styles.TryPick(ray, options, out var aim));
        Assert.Equal(PickResult.Of(found), aim);
        Assert.Equal((PickKind.Ghost, ghost.Id), (aim.Kind, aim.Ghost));
        Assert.Equal(found is PickHit.OnGhost g ? (g.Part, g.Cell, g.Face, g.Point, g.Distance, aim.Drafts, aim.Index) : default,
            (aim.Part, aim.Cell, aim.Face, aim.Point, aim.Distance, default(DraftsId), 0));
    }

    /// <summary>A ray that reaches the ghost from above.</summary>
    private protected virtual PickRay RayOnto(Ghost ghost) => Must(PickRay.Create(new WorldPoint(0f, 5f, 0f), new WorldVector(0f, -1f, 0f), 20f));

    private static string Describe(PickHit hit) => hit switch
    {
        PickHit.OnGhost g => $"ghost {g.Ghost.Value}",
        PickHit.OnDraft d => d.Trial.TryGet(out var trial) ? $"draft {d.Index} as {trial.Id.Value}" : $"draft {d.Index}",
        _ => throw new ArgumentOutOfRangeException(nameof(hit)),
    };

    [Fact]
    public void Need_4_hover_holds_one_ghost_and_selection_is_shared_by_id()
    {
        var a = Place(Draft(3));
        var b = Place(Draft(4));
        var hovered = Must(Styles.Set(LibrarySets.Hovered));
        var selected = Must(Styles.Set(LibrarySets.Selected));

        hovered.Put(a.Id);
        hovered.Put(b.Id);
        selected.Replace(ValueList.From(new[] { a.Id, b.Id }));
        selected.Remove(ValueList.Of(a.Id));

        Assert.Equal(1, hovered.Count);
        Assert.True(hovered.Contains(b.Id));
        Assert.True(Must(Styles.Set(LibrarySets.Selected)).Contains(b.Id));
        Assert.Equal(1, selected.Count);
        Assert.Equal(ReasonCodes.InvalidValue, Refused(Styles.Set(default)).Code);
    }

    [Fact]
    public void Need_5_every_state_finding_and_status_is_restyled_by_line_and_icon_not_colour_alone()
    {
        var rules = new List<StyleRule>();
        foreach (StateKind state in Enum.GetValues(typeof(StateKind)))
            rules.Add(new StyleRule(new StyleMatch.State(state), EdgeOf(LineStyle.Dotted, LibraryPalette.Stale)));
        foreach (FindingKind finding in Enum.GetValues(typeof(FindingKind)))
            rules.Add(new StyleRule(new StyleMatch.Finding(finding, Option<ReasonCode>.None), new Style
            {
                Pattern = Option.Some(Pattern.Hatch),
                Icon = Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Fails)),
            }));
        foreach (StatusKind status in Enum.GetValues(typeof(StatusKind)))
            rules.Add(new StyleRule(new StyleMatch.Status(status, Option<ExecutorId>.None), new Style { Icon = Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Waiting)) }));

        var layer = Must(Styles.Add(new StyleLayerDescriptor(LayerId("states"), StyleBand.Theme, 0, ValueList.From(rules))));

        Assert.True(layer.Enabled);
    }

    [Fact]
    public void Need_6_the_fill_keeps_the_tint_and_the_edge_carries_the_state()
    {
        var rule = new StyleRule(
            new StyleMatch.All(ValueList.From(new StyleMatch[] { new StyleMatch.Tinted(), new StyleMatch.Finding(FindingKind.Warns, Option<ReasonCode>.None) })),
            EdgeOf(LineStyle.Dashed, LibraryPalette.Warn) with { FillPaint = Option.Some<Paint>(new Paint.GhostTint(LibraryPalette.Planned)) });

        Must(Styles.Add(Layer("tint", StyleBand.Theme, rule)));
    }

    [Fact]
    public void Need_7_motion_turns_off_and_a_highlight_ends_early_when_disposed()
    {
        var ghost = Place(Draft(5));
        Must(Styles.Add(Layer("calm", StyleBand.View) with { Motion = Option.Some(Motion.Off) }));
        var flash = new Style
        {
            Effect = Option.Some<EffectLook>(new EffectLook.Pulse(Must(Period.Create(0.5f)), Must(Fraction.Create(0.3f)))),
            Ease = Option.Some(Must(Duration.Create(0.2f))),
        };

        var highlight = Must(Styles.Highlight(new HighlightRequest(new HighlightTargets.Ghosts(ValueList.Of(ghost.Id)), flash, Must(Duration.Create(2f)))));
        highlight.Dispose();

        Assert.Equal(ReasonCodes.InvalidValue,
            Refused(Styles.Highlight(new HighlightRequest(new HighlightTargets.Ghosts(ValueList.Of(ghost.Id)), flash, Duration.Zero))).Code);
    }

    [Fact]
    public void Need_8_built_structures_get_overlays_through_a_structure_set()
    {
        var overlays = Must(Styles.StructureSet(Must(StyleSetId.Create(Planner, "inspected"))));

        overlays.Put(Built);
        overlays.Add(ValueList.Of(Built));

        Assert.Equal(1, overlays.Count);
        Assert.True(overlays.Contains(Built));
        overlays.Clear();
        Assert.False(overlays.Contains(Built));
    }

    [Fact]
    public void Need_8_one_highlight_lights_ghosts_and_built_structures_together()
    {
        Place(Draft(6));
        var flash = new Style { Dim = Option.Some(Must(Fraction.Create(0.5f))) };
        var targets = new HighlightTargets.PickedAndBuilt(new Selector.Everything(), new StructureSelector.Refs(ValueList.Of(Built)));

        var highlight = Must(Styles.Highlight(new HighlightRequest(targets, flash, Must(Duration.Create(2f)))));
        highlight.Dispose();
    }

    [Fact]
    public void Need_9_a_view_filter_hides_a_family_and_unpicks_it()
    {
        var pipes = Must(PieceFamily.Create(Planner, "pipes"));
        var hide = new Style { Presence = Option.Some(Presence.Hidden), Pickable = Option.Some(Pickable.No) };

        Must(Styles.Add(Layer("only-pipes", StyleBand.View, new StyleRule(new StyleMatch.Not(new StyleMatch.Family(pipes)), hide))));
    }

    [Fact]
    public void Need_10_see_through_fades_with_distance_and_a_clip_stacks()
    {
        var far = new Style
        {
            SeeThrough = Option.Some<SeeThroughLook>(new SeeThroughLook.Through(Must(Fraction.Create(0.4f)), Must(Metres.Create(15f)))),
            DistanceFade = Option.Some(Must(FadeRange.Create(Must(Metres.Create(40f)), Must(Metres.Create(60f))))),
            NearLimit = Option.Some(Must(Metres.Create(0.3f))),
            BeyondClip = Option.Some<BeyondClipLook>(new BeyondClipLook.Dim(Must(Fraction.Create(0.2f)))),
        };
        Must(Styles.Add(Layer("depth", StyleBand.Theme, new StyleRule(new StyleMatch.Everything(), far))));
        var floor = Must(ClipShape.Plane.Create(new WorldPoint(0f, 2f, 0f), new WorldVector(0f, 1f, 0f)));
        var room = Must(ClipShape.Box.Create(new WorldPoint(-5f, 0f, -5f), new WorldPoint(5f, 3f, 5f)));

        var under = Must(Styles.Clip(floor));
        var over = Must(Styles.Clip(room));
        Assert.Equal(Option.Some<ClipShape>(room), Styles.InForce);

        over.Dispose();
        Assert.Equal(Option.Some<ClipShape>(floor), Styles.InForce);
        under.Dispose();
        Assert.False(Styles.InForce.TryGet(out _));
    }

    [Fact]
    public void See_through_takes_its_own_opacity_at_full_strength()
    {
        var plain = new SeeThroughLook.Through(Must(Fraction.Create(1f)), Must(Metres.Create(40f)));
        var strong = plain with { Opacity = Option.Some(Must(Fraction.Create(0.7f))) };
        var edges = new Style
        {
            Fill = Option.Some(FillMode.None),
            SeeThrough = Option.Some<SeeThroughLook>(new SeeThroughLook.Through(Must(Fraction.Create(1f)), Must(Metres.Create(40f)))
            {
                Opacity = Option.Some(Must(Fraction.Create(0.5f))),
            }),
        };

        Assert.False(plain.Opacity.TryGet(out _));
        Assert.NotEqual(plain, strong);
        var (strength, range) = strong;
        Assert.Equal((plain.Strength, plain.Range), (strength, range));
        Must(Styles.Add(Layer("x-ray", StyleBand.Theme, new StyleRule(new StyleMatch.Everything(), edges))));
    }

    [Fact]
    public void Need_11_and_gap_7_labels_name_a_text_style_and_ease_in_and_out()
    {
        var label = new Style
        {
            Label = Option.Some<LabelLook>(new LabelLook.Shown(new LabelText.FindingReason(), LibraryText.Default, LabelAnchor.Above, 10, Must(Metres.Create(12f)))),
            Appear = Option.Some(Must(Duration.Create(0.15f))),
            Vanish = Option.Some(Must(Duration.Create(0.3f))),
        };

        Must(Styles.Add(Layer("reasons", StyleBand.Theme, new StyleRule(new StyleMatch.Finding(FindingKind.WillFail, Option<ReasonCode>.None), label))));
    }

    [Fact]
    public void Need_12_a_decoration_mark_and_ports_are_style_opinions()
    {
        var decorated = new Style
        {
            Mark = Option.Some<MarkLook>(new MarkLook.Shown(Must(DecorationKey.Create(Planner, "arrow")))),
            Ports = Option.Some(ShowMode.Shown),
            FlowArrows = Option.Some(ShowMode.Shown),
        };

        Must(Styles.Add(Layer("marks", StyleBand.Theme, new StyleRule(new StyleMatch.Part(PartKind.Placement), decorated))));
    }

    [Fact]
    public void Need_13_explain_names_the_rule_behind_a_look()
    {
        var ghost = Place(Draft(6));
        var id = LayerId("failures");
        var resolved = EdgeOf(LineStyle.Dashed, LibraryPalette.Fail);
        Must(Styles.Add(Layer("failures", StyleBand.Meaning, new StyleRule(new StyleMatch.Mine(), resolved))));
        Frames();
        WillExplain(ghost.Id, new StyleStory(ghost.Id, resolved,
            ValueMap.Of<StyleProperty, StyleSource>(StyleProperty.Edge, new StyleSource.Layer(StyleBand.Meaning, id, 0)),
            ValueList.Of<StyleMatch>(new StyleMatch.Mine())));

        var story = Must(Styles.Explain(ghost.Id));

        Assert.Equal(new StyleSource.Layer(StyleBand.Meaning, id, 0), story.Sources[StyleProperty.Edge]);
        Assert.Equal(resolved.Edge, story.Resolved.Edge);
        Assert.Contains(new StyleMatch.Mine(), story.Facets);
    }

    [Fact]
    public void Need_14_others_ghosts_draw_in_their_owner_colour()
    {
        var ramp = new Palette(Must(PaletteId.Create(Planner, "owners")), ValueMap<PaletteKey, Rgba>.Empty)
        {
            OwnerRamp = ValueList.From(new[] { new Rgba(230, 120, 40, 255), new Rgba(40, 160, 230, 255) }),
        };
        var others = new Style { FillPaint = Option.Some<Paint>(new Paint.OwnerColour()) };

        Must(Styles.Add(Layer("others", StyleBand.Theme, new StyleRule(new StyleMatch.Not(new StyleMatch.Mine()), others)) with { Palette = Option.Some(ramp) }));
    }

    [Fact]
    public void Need_15_and_gap_8_a_theme_bundles_rules_palette_and_motion_and_turns_one_effect_off()
    {
        var calm = new Palette(Must(PaletteId.Create(Planner, "calm")), ValueMap.Of(LibraryPalette.Fail, new Rgba(200, 60, 60, 255)));
        var quiet = new Style
        {
            Effect = Option.Some<EffectLook>(new EffectLook.Steady()),
            Enter = Option.Some<EnterLook>(new EnterLook.None()),
        };
        var dark = Must(Styles.Add(Layer("dark", StyleBand.Theme, new StyleRule(new StyleMatch.Everything(), quiet))
            with { Palette = Option.Some(calm), Motion = Option.Some(Motion.Reduced) }));
        var light = Must(Styles.Add(Layer("light", StyleBand.Theme) with { Enabled = false }));

        dark.Disable();
        light.Enable();

        Assert.False(dark.Enabled);
        Assert.True(light.Enabled);
    }

    [Fact]
    public void A_theme_takes_a_library_palette_whole_adds_ready_made_layers_and_marks_finishes_in_their_paint()
    {
        var safe = new Palette(Must(PaletteId.Create(Planner, "safe")), ValueMap.Of(LibraryPalette.Fail, new Rgba(200, 60, 60, 255)))
        {
            Base = Option.Some(LibraryPalettes.ColourSafe),
        };
        var painted = new Style
        {
            Edge = Option.Some<EdgeLook>(new EdgeLook.Line(LineStyle.Solid, Must(Pixels.Create(2f)), new Paint.FinishColour(LibraryPalette.Planned))),
        };
        var theme = Must(Styles.Add(Layer("safe", StyleBand.Theme, new StyleRule(new StyleMatch.Finishes(), painted)) with { Palette = Option.Some(safe) }));
        var blueprint = Must(Styles.Add(LibraryLayers.Blueprint));
        var reasons = Must(Styles.Add(LibraryLayers.Reasons));
        var odd = new Palette(Must(PaletteId.Create(Planner, "odd")), ValueMap<PaletteKey, Rgba>.Empty) { Base = Option.Some(Must(PaletteId.Create(Planner, "nowhere"))) };

        reasons.Enable();

        Assert.True(theme.Enabled);
        Assert.True(blueprint.Enabled);
        Assert.True(reasons.Enabled);
        Assert.Equal(ReasonCodes.InvalidValue, Refused(Styles.Add(Layer("odd", StyleBand.Theme) with { Palette = Option.Some(odd) })).Code);
        Assert.Equal(ReasonCodes.AlreadyRegistered, Refused(Styles.Add(LibraryLayers.Blueprint)).Code);
    }

    [Fact]
    public void The_clip_in_force_is_raised_once_a_frame()
    {
        var seen = new List<Option<ClipShape>>();
        Action<Option<ClipShape>> onChanged = seen.Add;
        Styles.ClipChanged += onChanged;
        try
        {
            var first = Must(ClipShape.Plane.Create(new WorldPoint(0f, 1f, 0f), new WorldVector(0f, 1f, 0f)));
            var moved = Must(ClipShape.Plane.Create(new WorldPoint(0f, 2f, 0f), new WorldVector(0f, 1f, 0f)));
            var clip = Must(Styles.Clip(first));
            clip.Move(moved);
            EndFrame();
            EndFrame();
            clip.Dispose();
            EndFrame();

            Assert.Equal(new[] { Option.Some<ClipShape>(moved), Option<ClipShape>.None }, seen);
        }
        finally
        {
            Styles.ClipChanged -= onChanged;
        }
    }

    [Fact]
    public void A_style_match_is_asked_of_a_built_structure()
    {
        var pipes = new StyleMatch.Family(Must(PieceFamily.Create(Planner, "pipes")));
        WillMatch(pipes, Built);

        Assert.True(Styles.Matches(pipes, Built));
        Assert.False(Styles.Matches(new StyleMatch.Family(Must(PieceFamily.Create(Planner, "cables"))), Built));
        Assert.False(Styles.Matches(new StyleMatch.Facing(Axis.Y), Built));
        Assert.False(Styles.Matches(new StyleMatch.Family(PieceFamilies.Window), Built));
    }

    [Fact]
    public void A_ghost_on_a_built_structure_names_it_and_the_board_finds_it_from_the_structure()
    {
        var removal = Place(Draft(7) with { Intent = new Intent.Remove(Built) });

        var on = System.Board.On(Built);

        Assert.Equal(Option.Some(Built), Target(removal.Intent));
        Assert.Contains(on, g => g.Id == removal.Id);
    }

    [Fact]
    public void Remove_swap_and_finish_each_name_their_built_structure()
    {
        var placement = Assert.IsType<Intent.Build>(Draft(8).Intent).Target;
        var finish = Must(Finish.Create(Option.Some(Must(PaintColour.Create(1))), Option<ThingName>.None, Option<Setting>.None));

        Assert.Equal(Option.Some(Built), Target(new Intent.Swap(Built, placement, Option<Finish>.None)));
        Assert.Equal(Option.Some(Built), Target(new Intent.FinishOnly(Built, finish)));
        Assert.Equal(Option<StructureRef>.None, Target(new Intent.Build(placement, Option<Finish>.None)));
    }

    private static Option<StructureRef> Target(Intent intent) => intent switch
    {
        Intent.Remove remove => Option.Some(remove.Target),
        Intent.Swap swap => Option.Some(swap.Old),
        Intent.FinishOnly finish => Option.Some(finish.Target),
        _ => Option<StructureRef>.None,
    };
}
