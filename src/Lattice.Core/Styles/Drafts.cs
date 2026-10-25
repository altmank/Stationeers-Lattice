using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>
/// One stream of uncommitted ghosts. <c>Show</c> draws in the call (raw drafts as unchecked, a preview with its
/// findings). Handing over never leaves a frame with neither the drafts nor the applied ghosts drawn, and never one with
/// both: applied drafts are dropped in the drawing slice that first puts the applied change set's ghosts in the draw
/// plan; refused drafts are dropped at once; sent drafts turn pending until their ticket is answered.
/// </summary>
internal sealed class DraftsHandle(DraftsId id, GhostStyles styles) : IDrafts
{
    private readonly List<Thing> _shown = new(8);
    private Option<Ticket> _ticket;
    private Option<ChangeSetId> _awaiting;

    public DraftsId Id { get; } = id;

    /// <summary>Whether it draws anything now.</summary>
    public bool ShowsAnything => _shown.Count > 0;

    public IReadOnlyList<Thing> Shown => _shown;

    /// <summary>Whether it is handed over and still drawn, waiting for an answer or for its change set to be drawn.</summary>
    public bool Handed => _ticket.IsSome || _awaiting.IsSome;

    public bool Disposed { get; private set; }

    public void Show(ValueList<GhostDraft> drafts)
    {
        Begin();
        for (var i = 0; i < drafts.Count; i++)
            styles.ShowDraft(this, i, null, drafts[i], Option<Finding>.None, _shown);
    }

    public void Show(Api.Preview preview)
    {
        Begin();
        var findings = new Dictionary<GhostId, Finding>(preview.Findings.Count);
        foreach (var finding in preview.Findings)
            findings[finding.Ghost] = finding;
        var index = 0;
        foreach (var ghost in preview.WouldChange.Added)
            styles.ShowDraft(this, index++, ghost, null, Found(findings, ghost.Id), _shown);
        foreach (var change in preview.WouldChange.Changed)
            styles.ShowDraft(this, index++, change.After, null, Found(findings, change.After.Id), _shown);
    }

    public void Clear() => Begin();

    public void Hand(Submission submission)
    {
        if (Disposed || _shown.Count == 0)
            return;
        switch (submission)
        {
            case Submission.Answered answered:
                Answer(answered.Outcome);
                break;
            case Submission.Sent sent:
                _ticket = Option.Some(sent.Ticket);
                foreach (var thing in _shown)
                {
                    thing.Pending = true;
                    styles.Resign(thing);
                }

                break;
        }
    }

    /// <summary>The ticket it was handed with is answered.</summary>
    public void Answered(Ticket ticket, Outcome outcome)
    {
        if (_ticket.TryGet(out var waiting) && waiting == ticket)
            Answer(outcome);
    }

    /// <summary>A change set's ghosts are now in the draw plan: drafts waiting for it go in the same slice.</summary>
    public void Drawn(ChangeSetId changes)
    {
        if (_awaiting.TryGet(out var awaited) && awaited == changes)
            Begin();
    }

    public void Dispose()
    {
        if (Disposed)
            return;
        Begin();
        Disposed = true;
        styles.Free(this);
    }

    /// <summary>The world ended: everything shown goes.</summary>
    public void End() => Begin();

    private void Answer(Outcome outcome)
    {
        _ticket = Option<Ticket>.None;
        if (outcome is Outcome.Applied applied && !styles.Drawn(applied.Changes.Id))
            _awaiting = Option.Some(applied.Changes.Id);
        else
            Begin();
    }

    private void Begin()
    {
        _ticket = Option<Ticket>.None;
        _awaiting = Option<ChangeSetId>.None;
        foreach (var thing in _shown)
            styles.Hide(thing);
        _shown.Clear();
    }

    private static Option<Finding> Found(Dictionary<GhostId, Finding> findings, GhostId ghost) =>
        findings.TryGetValue(ghost, out var finding) ? Option.Some(finding) : Option<Finding>.None;
}
