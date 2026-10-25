using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Transforms;

/// <summary>
/// Makes a template from ghosts: every build ghost, and every swap ghost as a build of its new piece, in local
/// coordinates about the centre of the large cell at the low corner of their box, so placing the template on a large
/// cell centre puts large and small pieces back on their grids. Remove and finish ghosts are about standing structures
/// and are left out. Looks, maker, groups and attached data are kept; plan fields are none. Linear in the ghosts.
/// </summary>
internal static class TemplateMaker
{
    /// <summary>The template of the ghosts, in the order given; refused <see cref="ReasonCodes.UnknownKind"/> for an intent this version does not know.</summary>
    public static Result<Template, Reason> Of(TemplateName name, IReadOnlyList<Ghost> ghosts)
    {
        var builds = new List<(Ghost Ghost, Intent.Build Build)>(ghosts.Count);
        foreach (var ghost in ghosts)
            switch (ghost.Intent)
            {
                case Intent.Build build:
                    builds.Add((ghost, build));
                    break;
                case Intent.Swap swap:
                    builds.Add((ghost, new Intent.Build(swap.New, swap.Finish)));
                    break;
                case Intent.Remove or Intent.FinishOnly:
                    break;
                default:
                    return Result<Template, Reason>.Error(Unknown.Kind(ghost.Intent));
            }

        var origin = Origin(builds);
        var drafts = new GhostDraft[builds.Count];
        for (var i = 0; i < drafts.Length; i++)
        {
            var (ghost, build) = builds[i];
            var at = build.Target.At;
            var local = build.Target with { At = new Cell(at.X - origin.X, at.Y - origin.Y, at.Z - origin.Z) };
            drafts[i] = new GhostDraft(
                new Intent.Build(local, build.Finish), Option<PlanId>.None, ghost.MadeBy, ghost.Visibility, ghost.Label,
                ghost.Tint, ghost.Groups, ghost.Attached);
        }

        return Result<Template, Reason>.Ok(new Template(name, Lists.Own(drafts)));
    }

    private static Cell Origin(List<(Ghost Ghost, Intent.Build Build)> builds)
    {
        if (builds.Count == 0)
            return default;
        var (x, y, z) = (int.MaxValue, int.MaxValue, int.MaxValue);
        foreach (var (_, build) in builds)
        {
            var at = build.Target.At;
            (x, y, z) = (System.Math.Min(x, at.X), System.Math.Min(y, at.Y), System.Math.Min(z, at.Z));
        }

        return new Cell(LargeCentreAtOrBelow(x), LargeCentreAtOrBelow(y), LargeCentreAtOrBelow(z));
    }

    private static int LargeCentreAtOrBelow(int key)
    {
        var shifted = key - 10;
        var floor = shifted >= 0 ? shifted / 20 : -((-shifted + 19) / 20);
        return floor * 20 + 10;
    }
}
