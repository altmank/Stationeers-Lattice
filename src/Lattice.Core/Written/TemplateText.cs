using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Written;

/// <summary>
/// The template form as the contract promises it (<see cref="IPlans.WriteTemplate"/>, <see cref="IPlans.ReadTemplate"/>):
/// <see cref="TemplateForm"/>'s text at schema <see cref="WrittenForms.Schema"/>, written without plan fields and read
/// whole or not at all. A draft this version cannot read in full (an unknown intent, a field it does not know) refuses
/// the template rather than be dropped or read in part: placing it would make something other than what was written.
/// Unknown header fields describe the document and are dropped. Pure; linear in the drafts.
/// </summary>
internal static class TemplateText
{
    /// <summary>The problem <see cref="DraftForm"/> gives a draft whose intent word this version does not know.</summary>
    private const string UnknownIntent = "unknown intent";

    public static string Write(Template template)
    {
        var drafts = new DraftRecord[template.Ghosts.Count];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = DraftRecord.Of(template.Ghosts[i] with { Plan = Option<PlanId>.None });
        return TemplateForm.Write(new WrittenTemplate(template.Name, Lists.Own(drafts)));
    }

    public static Result<Template, Reason> Read(string text)
    {
        var read = Documents.ReadText(text ?? "", WrittenForms.Template);
        if (!read.TryGet(out var document))
            return Result<Template, Reason>.Error(read.ErrorOf());
        if (document.Header.Schema > WrittenForms.Schema)
            return Refused(ReasonCodes.UnknownKind, ValueMap.Of("problem", "newer-schema")
                .With("schema", document.Header.Schema.ToString(CultureInfo.InvariantCulture)));
        foreach (var problem in document.Problems)
            if (problem is ReadProblem.TornLastLine torn)
                return Refused(ReasonCodes.InvalidValue, ValueMap.Of("problem", "cut line").With("line", Number(torn.At)));

        var written = TemplateForm.TemplateFrom(document.Header, document.Items);
        if (!written.TryGet(out var template))
            return Result<Template, Reason>.Error(written.ErrorOf());

        var drafts = new GhostDraft[template.Ghosts.Count];
        var unknown = new List<int>();
        for (var i = 0; i < drafts.Length; i++)
        {
            switch (template.Ghosts[i])
            {
                case DraftRecord.Known { Extra.Count: 0 } known:
                    drafts[i] = known.Draft with { Plan = Option<PlanId>.None };
                    break;
                case DraftRecord.Opaque { Problem: UnknownIntent }:
                case DraftRecord.Known:
                    unknown.Add(i);
                    break;
                case DraftRecord.Opaque opaque:
                    return Refused(ReasonCodes.InvalidValue, ValueMap.Of("problem", opaque.Problem).With("line", Number(i + 2)));
                default:
                    unknown.Add(i);
                    break;
            }
        }

        if (unknown.Count > 0)
            return Refused(ReasonCodes.UnknownKind, ValueMap.Of("drafts", string.Join(",", unknown)));
        return Result<Template, Reason>.Ok(new Template(template.Name, Lists.Own(drafts)));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static Result<Template, Reason> Refused(ReasonCode code, ValueMap<string, string> facts) =>
        Result<Template, Reason>.Error(Reason.Of(code, WrittenForms.Rule, facts));
}
