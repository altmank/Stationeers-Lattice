using System;
using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>
/// The style service while style resolution is not built: every registration is refused <c>module-missing</c>
/// (module <c>styles</c>), no clip is in force, nothing is picked and no structure matches.
/// </summary>
internal sealed class NoStyles : IGhostStyles
{
    private static Reason Missing => Reason.Of(ReasonCodes.ModuleMissing, RuleId.Known("lattice.styles"), ValueMap.Of("module", "styles"));

    public Option<ClipShape> InForce => Option<ClipShape>.None;

    public event Action<Option<ClipShape>> ClipChanged { add { } remove { } }

    public Result<IStyleLayer, Reason> Add(StyleLayerDescriptor layer) => Result<IStyleLayer, Reason>.Error(Missing);

    public Result<IStyleSet, Reason> Set(StyleSetId id) => Result<IStyleSet, Reason>.Error(Missing);

    public Result<IStructureSet, Reason> StructureSet(StyleSetId id) => Result<IStructureSet, Reason>.Error(Missing);

    public Result<IDisposable, Reason> Highlight(HighlightRequest request) => Result<IDisposable, Reason>.Error(Missing);

    public Result<IDrafts, Reason> Drafts(DraftsId id) => Result<IDrafts, Reason>.Error(Missing);

    public Result<IClip, Reason> Clip(ClipShape shape) => Result<IClip, Reason>.Error(Missing);

    public Option<PickHit> Pick(PickRay ray, PickOptions options) => Option<PickHit>.None;

    public bool TryPick(PickRay ray, PickOptions options, out PickResult hit)
    {
        hit = default;
        return false;
    }

    public Result<StyleStory, Reason> Explain(GhostId ghost) => Result<StyleStory, Reason>.Error(Missing);

    public bool Matches(StyleMatch match, StructureRef structure) => false;
}
