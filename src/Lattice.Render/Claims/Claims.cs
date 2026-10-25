using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Render.Materials;

namespace Lattice.Render.Claims;

/// <summary>One registered claim: its descriptor, its match watch, and the batches it wins.</summary>
internal sealed class ClaimEntry
{
    public ClaimEntry(RendererClaimDescriptor descriptor, IMatchWatch watch, long sequence)
    {
        Descriptor = descriptor;
        Watch = watch;
        Sequence = sequence;
        SeenGeneration = watch.Generation;
        View = new ClaimView(this);
    }

    public RendererClaimDescriptor Descriptor { get; }

    public IMatchWatch Watch { get; }

    /// <summary>Its registration order: the later of two equal orders wins.</summary>
    public long Sequence { get; }

    public long SeenGeneration { get; set; }

    public bool Removed { get; set; }

    public ClaimParts Parts => Descriptor.Parts;

    public List<BatchDraw> Members { get; } = new(8);

    public ClaimView View { get; }

    /// <summary>Whether this claim beats <paramref name="other"/> on a thing both match.</summary>
    public bool Beats(ClaimEntry other) =>
        Descriptor.Order > other.Descriptor.Order || Descriptor.Order == other.Descriptor.Order && Sequence > other.Sequence;
}

/// <summary>
/// Renderer claims for one world. The registry changes by copy, so a
/// callback that claims or disposes mid-loop leaves the frame's loop on the list it started with. Which claim wins a
/// batch is worked out when the claims, a match's generation or the batch's signature change, in the drawing budget,
/// never per frame. A claim's match is watched through Core's styles (<c>GhostStyles.Watch</c>), held while the claim
/// is. <see cref="Draw"/> is the last thing Render does in a frame: a callback's exception leaves it with Lattice's own
/// drawing already submitted, skips the later claims that frame, and changes no registration.
/// </summary>
internal sealed class ClaimTable(Func<StyleMatch, IMatchWatch> watch)
{
    private ClaimEntry[] _entries = Array.Empty<ClaimEntry>();
    private long _sequence;

    /// <summary>Rises whenever a batch's winner may have changed.</summary>
    public long Stamp { get; private set; } = 1;

    public int Count => _entries.Length;

    /// <summary>Registers a claim; refused (<c>already-registered</c>) for an id already registered.</summary>
    public Result<IDisposable, Reason> Add(RendererClaimDescriptor claim)
    {
        if (claim is null)
            throw new ArgumentNullException(nameof(claim));
        if (claim.Draw is null)
            throw new ArgumentNullException(nameof(claim), "a claim needs its Draw callback");
        if (claim.Id.Value.Length == 0)
            return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RenderCodes.Rule, ValueMap.Of("claim", "").With("problem", "no-id")));
        var entries = _entries;
        for (var i = 0; i < entries.Length; i++)
            if (entries[i].Descriptor.Id == claim.Id)
                return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.AlreadyRegistered, RenderCodes.Rule, ValueMap.Of("claim", claim.Id.Value)));

        var entry = new ClaimEntry(claim, watch(claim.Match), ++_sequence);
        var next = new ClaimEntry[entries.Length + 1];
        Array.Copy(entries, next, entries.Length);
        next[entries.Length] = entry;
        _entries = next;
        Stamp++;
        return Result<IDisposable, Reason>.Ok(new Registration(this, entry));
    }

    /// <summary>A match's generation moved since the last <see cref="Refresh"/>.</summary>
    public bool Moved
    {
        get
        {
            var entries = _entries;
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].Watch.Generation != entries[i].SeenGeneration)
                    return true;
            return false;
        }
    }

    /// <summary>Notes a match whose answer may have changed, once per sort.</summary>
    public void Refresh()
    {
        var entries = _entries;
        for (var i = 0; i < entries.Length; i++)
        {
            var generation = entries[i].Watch.Generation;
            if (generation == entries[i].SeenGeneration)
                continue;
            entries[i].SeenGeneration = generation;
            Stamp++;
        }
    }

    /// <summary>A batch's winning claim, worked out again only when it may have changed; whether the winner changed.</summary>
    public bool Settle(BatchDraw draw)
    {
        var signature = draw.Batch.Key.Signature;
        if (draw.ClaimStamp == Stamp && ReferenceEquals(draw.ClaimSignature, signature))
            return false;
        draw.ClaimStamp = Stamp;
        draw.ClaimSignature = signature;
        var winner = Winner(draw.Batch);
        if (ReferenceEquals(winner, draw.Claim))
            return false;
        draw.Claim?.Members.Remove(draw);
        winner?.Members.Add(draw);
        draw.Claim = winner;
        return true;
    }

    /// <summary>Calls every claim registered when the loop starts, in registration order, with its things in view.</summary>
    public void Draw()
    {
        var entries = _entries;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Removed)
                continue;
            entry.View.Open();
            try
            {
                entry.Descriptor.Draw(entry.View);
            }
            finally
            {
                entry.View.Close();
            }
        }
    }

    /// <summary>Ends every claim (world end).</summary>
    public void End()
    {
        var entries = _entries;
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i].Removed = true;
            entries[i].Watch.Dispose();
        }

        _entries = Array.Empty<ClaimEntry>();
        Stamp++;
    }

    private ClaimEntry? Winner(Batch batch)
    {
        ClaimEntry? best = null;
        var entries = _entries;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Watch.Holds(batch) && (best is null || entry.Beats(best)))
                best = entry;
        }

        return best;
    }

    private void Remove(ClaimEntry entry)
    {
        var entries = _entries;
        var at = Array.IndexOf(entries, entry);
        if (at < 0)
            return;
        var next = new ClaimEntry[entries.Length - 1];
        Array.Copy(entries, 0, next, 0, at);
        Array.Copy(entries, at + 1, next, at, entries.Length - at - 1);
        _entries = next;
        entry.Removed = true;
        entry.Watch.Dispose();
        Stamp++;
    }

    private sealed class Registration(ClaimTable owner, ClaimEntry entry) : IDisposable
    {
        public void Dispose() => owner.Remove(entry);
    }
}

/// <summary>
/// A claim's things in view this frame: its won batches the last sort found in view, read in place. Indexing in order
/// walks the batches once; nothing allocates once each thing's <see cref="DrawnThing"/> is made.
/// </summary>
internal sealed class ClaimView(ClaimEntry entry) : IClaimFrame
{
    private static readonly ConditionalWeakTable<Thing, DrawnThing> Drawn = new();

    private bool _open;
    private int _member;
    private int _base;

    public int Count { get; private set; }

    public ClaimedThing this[int index]
    {
        get
        {
            if (!_open)
                throw new InvalidOperationException("A claim frame is read only during its claim's callback.");
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (index < _base)
            {
                _member = 0;
                _base = 0;
            }

            var members = entry.Members;
            while (true)
            {
                var draw = members[_member];
                var count = Shown(draw);
                if (index < _base + count)
                {
                    var batch = draw.Batch;
                    var instance = batch[index - _base];
                    return new ClaimedThing(Of(instance.Thing), instance.Thing.Part, batch.Piece, Submission.Placement(instance.At, instance.Turn), batch.Look.Style);
                }

                _base += count;
                _member++;
            }
        }
    }

    public void Open()
    {
        var total = 0;
        var members = entry.Members;
        for (var i = 0; i < members.Count; i++)
            total += Shown(members[i]);
        Count = total;
        _member = 0;
        _base = 0;
        _open = true;
    }

    public void Close() => _open = false;

    private static int Shown(BatchDraw draw) => draw.Visible ? draw.Batch.Count : 0;

    private static DrawnThing Of(Thing thing)
    {
        if (Drawn.TryGetValue(thing, out var drawn))
            return drawn;
        drawn = thing.Kind switch
        {
            ThingKind.Ghost => new DrawnThing.OfGhost(thing.Id),
            ThingKind.Draft => new DrawnThing.OfDraft(thing.Drafts!.Id, thing.Index),
            _ => new DrawnThing.OfStructure(thing.Structure),
        };
        Drawn.Add(thing, drawn);
        return drawn;
    }
}
