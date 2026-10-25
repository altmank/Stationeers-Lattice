using System;
using Lattice.Api;
using Lattice.Core.Styles;

namespace Lattice.Tests.Styles;

/// <summary>The styles stand-in, for a world with module <c>styles</c> off: every registration is refused as a missing module, and nothing is drawn.</summary>
public sealed class NoStylesTests
{
    private static readonly ModId Mod = Ok(ModId.Create("mymod"));

    [Fact]
    public void Every_registration_is_refused_module_missing_for_the_styles_module()
    {
        IGhostStyles styles = new NoStyles();
        var layer = new StyleLayerDescriptor(Ok(StyleLayerId.Create(Mod, "mine")), StyleBand.Meaning, 0, ValueList<StyleRule>.Empty);
        var set = Ok(StyleSetId.Create(Mod, "picked"));

        MissingStyles(styles.Add(layer));
        MissingStyles(styles.Set(set));
        MissingStyles(styles.StructureSet(set));
        MissingStyles(styles.Highlight(new HighlightRequest(new HighlightTargets.Ghosts(ValueList.Of(new GhostId(1))), Style.Empty, Duration.Zero)));
        MissingStyles(styles.Drafts(Ok(DraftsId.Create(Mod, "tool"))));
        MissingStyles(styles.Clip(Ok(ClipShape.Box.Create(new WorldPoint(0f, 0f, 0f), new WorldPoint(1f, 1f, 1f)))));
        MissingStyles(styles.Explain(new GhostId(1)));
    }

    [Fact]
    public void Nothing_is_clipped_picked_or_matched()
    {
        IGhostStyles styles = new NoStyles();
        var ray = Ok(PickRay.Create(new WorldPoint(0f, 0f, 0f), new WorldVector(0f, 0f, 1f), 10f));

        Assert.False(styles.InForce.TryGet(out _));
        Assert.False(styles.Pick(ray, new PickOptions()).TryGet(out _));
        Assert.False(styles.Matches(new StyleMatch.Everything(), new StructureRef(new ReferenceId(5), new PrefabKey(10), new Cell(0, 0, 0))));
    }

    private static void MissingStyles<T>(Result<T, Reason> result) where T : notnull
    {
        var reason = result.Match(v => throw new InvalidOperationException($"accepted {v}"), r => r);

        Assert.Equal(ReasonCodes.ModuleMissing, reason.Code);
        Assert.Equal("styles", reason.Facts["module"]);
    }

    private static T Ok<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(v => v, r => throw new InvalidOperationException(r.Code.Value));
}
