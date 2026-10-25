using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles.Library;
using Lattice.Testing;
using static Lattice.Tests.Styles.Library.LibraryBench;

namespace Lattice.Tests.Styles.Library;

/// <summary>The library palettes, the blueprint example theme and the opt-in label layers.</summary>
public sealed class LibraryLayerTests
{
    [Fact]
    public void The_library_has_the_default_colour_safe_and_high_contrast_palettes()
    {
        Assert.Equal(
            new[] { LibraryPalettes.Default, LibraryPalettes.ColourSafe, LibraryPalettes.HighContrast },
            Starter.Palettes.Select(p => p.Id));
        Assert.Equal(Option.Some(Starter.ColourSafe), Starter.PaletteOf(LibraryPalettes.ColourSafe));
        Assert.True(Starter.PaletteOf(Starter.Blueprint.Palette.Reduce(Starter.Default).Id).IsNone);
    }

    [Fact]
    public void Every_library_palette_defines_every_library_token_opaque_and_an_owner_ramp()
    {
        var tokens = typeof(LibraryPalette).GetFields().Select(f => (PaletteKey)f.GetValue(null)!).ToList();

        foreach (var palette in Starter.Palettes)
        {
            Assert.All(tokens, t => Assert.Equal(255, palette.Colours[t].A));
            Assert.Equal(tokens.Count, palette.Colours.Count);
            Assert.NotEmpty(palette.OwnerRamp);
        }
    }

    [Fact]
    public void The_default_palette_speaks_the_game_cursors_colours()
    {
        var colours = Starter.Default.Colours;

        Assert.Equal(new Rgba(0, 0, 255, 255), colours[LibraryPalette.Planned]);
        Assert.Equal(new Rgba(0, 255, 0, 255), colours[LibraryPalette.Next]);
        Assert.Equal(new Rgba(255, 0, 0, 255), colours[LibraryPalette.Fail]);
        Assert.Equal(new Rgba(255, 235, 4, 255), colours[LibraryPalette.Warn]);
    }

    [Fact]
    public void The_blueprint_theme_is_a_theme_layer_a_consumer_can_add()
    {
        var styles = new FakeGhostStyles();

        var layer = Must(styles.Add(Starter.Blueprint));

        Assert.Equal(StyleBand.Theme, Starter.Blueprint.Band);
        Assert.Equal(LibraryLayers.Blueprint.Id, layer.Id);
        Assert.True(layer.Enabled);
    }

    [Fact]
    public void Over_the_library_the_blueprint_theme_fills_by_finding_in_the_cursors_colours_and_holds_a_failing_ghost_still()
    {
        var rules = Starter.Rules.Concat(Starter.Blueprint.Rules).ToList();

        var fails = Resolve(rules, Thing.Planned with { Finding = FindingKind.WillFail });
        var next = Resolve(rules, Thing.Planned with { Finding = FindingKind.Passes });
        var planned = Resolve(rules, Thing.Planned);

        Assert.Equal(Option.Some<Paint>(new Paint.Token(LibraryPalette.Fail)), fails.FillPaint);
        Assert.Equal(Option.Some<EffectLook>(new EffectLook.Steady()), fails.Effect);
        Assert.Equal(Option.Some<EnterLook>(new EnterLook.None()), fails.Enter);
        Assert.Equal(Option.Some(Pattern.Hatch), fails.Pattern);
        Assert.Equal(Option.Some<Paint>(new Paint.Token(LibraryPalette.Next)), next.FillPaint);
        Assert.Equal(Option.Some<Paint>(new Paint.Token(LibraryPalette.Planned)), planned.FillPaint);
        Assert.Equal(new Rgba(0, 0, 255, 255), Starter.Blueprint.Palette.Reduce(Starter.Default).Colours[LibraryPalette.Planned]);
    }

    [Fact]
    public void The_label_layers_are_view_layers_off_until_enabled_in_the_default_text_style_and_a_consumer_can_add_them()
    {
        var styles = new FakeGhostStyles();

        foreach (var descriptor in Starter.LabelLayers)
        {
            var layer = Must(styles.Add(descriptor));
            var label = Assert.IsType<LabelLook.Shown>(Assert.Single(descriptor.Rules).Style.Label.Reduce(new LabelLook.None()));

            Assert.Equal(StyleBand.View, descriptor.Band);
            Assert.False(layer.Enabled);
            Assert.Equal(LibraryText.Default, label.Style);
        }
    }

    [Fact]
    public void The_label_layers_say_step_numbers_bill_counts_reasons_and_plan_names()
    {
        var says = Starter.LabelLayers.Select(l => ((LabelLook.Shown)l.Rules[0].Style.Label.Reduce(new LabelLook.None())).Says.GetType());

        Assert.Equal(
            new[] { typeof(LabelText.StepNumber), typeof(LabelText.BillCount), typeof(LabelText.FindingReason), typeof(LabelText.PlanTitle) },
            says);
    }

    [Fact]
    public void With_the_reasons_layer_a_failing_ghost_shows_its_reason_without_hover()
    {
        var reasons = Starter.LabelLayers.Single(l => l.Id == LibraryLayers.Reasons.Id);
        var rules = Starter.Rules.Concat(reasons.Rules).ToList();

        var fails = Resolve(rules, Thing.Planned with { Finding = FindingKind.WillFail });
        var passes = Resolve(rules, Thing.Planned with { Finding = FindingKind.Passes });

        Assert.IsType<LabelText.FindingReason>(Assert.IsType<LabelLook.Shown>(fails.Label.Reduce(new LabelLook.None())).Says);
        Assert.True(passes.Label.IsNone);
    }
}
