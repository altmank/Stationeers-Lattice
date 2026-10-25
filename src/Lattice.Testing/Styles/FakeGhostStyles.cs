using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing;

/// <summary>
/// A recording <see cref="IGhostStyles"/> for consumer tests. It keeps what was registered (layers, sets, highlights,
/// drafts, the clip stack) and refuses what the contract refuses; it resolves no styles. <see cref="Pick"/>,
/// <see cref="Explain"/> and <see cref="Matches"/> answer what the test set up. <see cref="Frame"/> stands for the end
/// of a frame: it raises <see cref="ClipChanged"/> once when the clip in force changed.
/// </summary>
public sealed class FakeGhostStyles : IGhostStyles
{
    private readonly List<FakeStyleLayer> _layers = new();
    private readonly Dictionary<StyleSetId, FakeStyleSet> _sets = new();
    private readonly Dictionary<StyleSetId, FakeStructureSet> _structureSets = new();
    private readonly List<FakeHighlight> _highlights = new();
    private readonly Dictionary<DraftsId, FakeDrafts> _drafts = new();
    private readonly List<FakeClip> _clips = new();
    private readonly List<(PickRay Ray, PickOptions Options)> _picks = new();
    private readonly Dictionary<GhostId, StyleStory> _stories = new();
    private readonly HashSet<(StyleMatch, StructureRef)> _matches = new();
    private Option<PickHit> _pick;
    private Option<ClipShape> _lastRaised;

    /// <inheritdoc />
    public event Action<Option<ClipShape>>? ClipChanged;

    /// <summary>Every layer added, disposed ones included, in order.</summary>
    public IReadOnlyList<FakeStyleLayer> Layers => _layers;

    /// <summary>The ghost sets named so far.</summary>
    public IReadOnlyDictionary<StyleSetId, FakeStyleSet> Sets => _sets;

    /// <summary>The structure sets named so far.</summary>
    public IReadOnlyDictionary<StyleSetId, FakeStructureSet> StructureSets => _structureSets;

    /// <summary>Every highlight asked for, ended ones included, in order.</summary>
    public IReadOnlyList<FakeHighlight> Highlights => _highlights;

    /// <summary>The live drafts handles.</summary>
    public IReadOnlyDictionary<DraftsId, FakeDrafts> DraftHandles => _drafts;

    /// <summary>Every clip installed, disposed ones included, in order.</summary>
    public IReadOnlyList<FakeClip> Clips => _clips;

    /// <summary>Every pick asked, in order.</summary>
    public IReadOnlyList<(PickRay Ray, PickOptions Options)> Picks => _picks;

    /// <inheritdoc />
    public Option<ClipShape> InForce
    {
        get
        {
            for (var i = _clips.Count - 1; i >= 0; i--)
                if (!_clips[i].Disposed)
                    return Option<ClipShape>.Some(_clips[i].Shape);
            return Option<ClipShape>.None;
        }
    }

    /// <summary>Every later <see cref="Pick"/> hits this.</summary>
    public void WillPick(PickHit hit) => _pick = Option<PickHit>.Some(hit);

    /// <summary>Every later <see cref="Pick"/> hits nothing.</summary>
    public void WillPickNothing() => _pick = Option<PickHit>.None;

    /// <summary><see cref="Explain"/> tells this story for the ghost.</summary>
    public void WillExplain(GhostId ghost, StyleStory story) => _stories[ghost] = story;

    /// <summary><see cref="Matches"/> holds for this match and structure.</summary>
    public void WillMatch(StyleMatch match, StructureRef structure) => _matches.Add((match, structure));

    /// <summary>The end of a frame: raises <see cref="ClipChanged"/> once if the clip in force is not the one last raised.</summary>
    public void Frame()
    {
        var now = InForce;
        if (SameClip(now, _lastRaised))
            return;
        _lastRaised = now;
        ClipChanged?.Invoke(now);
    }

    /// <summary>The host answered a ticket: drafts pending on it are dropped.</summary>
    public void Answered(Ticket ticket)
    {
        foreach (var drafts in _drafts.Values)
            if (drafts.PendingOn.TryGet(out var pending) && pending == ticket)
                drafts.Clear();
    }

    /// <inheritdoc />
    public Result<IStyleLayer, Reason> Add(StyleLayerDescriptor layer)
    {
        if (layer.Id == default || !Enum.IsDefined(typeof(StyleBand), layer.Band))
            return Result<IStyleLayer, Reason>.Error(Refusal(ReasonCodes.InvalidValue, nameof(StyleLayerDescriptor)));
        if (layer.Palette.TryGet(out var palette) && palette.Base.TryGet(out var basis)
            && basis != LibraryPalettes.Default && basis != LibraryPalettes.ColourSafe && basis != LibraryPalettes.HighContrast)
            return Result<IStyleLayer, Reason>.Error(Refusal(ReasonCodes.InvalidValue, nameof(Palette.Base)));
        foreach (var added in _layers)
            if (!added.Disposed && added.Descriptor.Id == layer.Id)
                return Result<IStyleLayer, Reason>.Error(Refusal(ReasonCodes.AlreadyRegistered, nameof(StyleLayerId)));

        var fake = new FakeStyleLayer(layer);
        _layers.Add(fake);
        return Result<IStyleLayer, Reason>.Ok(fake);
    }

    /// <inheritdoc />
    public Result<IStyleSet, Reason> Set(StyleSetId id)
    {
        if (id == default)
            return Result<IStyleSet, Reason>.Error(Refusal(ReasonCodes.InvalidValue, nameof(StyleSetId)));
        if (!_sets.TryGetValue(id, out var set))
            _sets[id] = set = new FakeStyleSet(id);
        return Result<IStyleSet, Reason>.Ok(set);
    }

    /// <inheritdoc />
    public Result<IStructureSet, Reason> StructureSet(StyleSetId id)
    {
        if (id == default)
            return Result<IStructureSet, Reason>.Error(Refusal(ReasonCodes.InvalidValue, nameof(StyleSetId)));
        if (!_structureSets.TryGetValue(id, out var set))
            _structureSets[id] = set = new FakeStructureSet(id);
        return Result<IStructureSet, Reason>.Ok(set);
    }

    /// <inheritdoc />
    public Result<IDisposable, Reason> Highlight(HighlightRequest request)
    {
        if (request.For == Duration.Zero)
            return Result<IDisposable, Reason>.Error(Refusal(ReasonCodes.InvalidValue, nameof(Duration)));
        var highlight = new FakeHighlight(request);
        _highlights.Add(highlight);
        return Result<IDisposable, Reason>.Ok(highlight);
    }

    /// <inheritdoc />
    public Result<IDrafts, Reason> Drafts(DraftsId id)
    {
        if (id == default)
            return Result<IDrafts, Reason>.Error(Refusal(ReasonCodes.InvalidValue, nameof(DraftsId)));
        if (!_drafts.TryGetValue(id, out var drafts))
            _drafts[id] = drafts = new FakeDrafts(id, () => _drafts.Remove(id));
        return Result<IDrafts, Reason>.Ok(drafts);
    }

    /// <inheritdoc />
    public Result<IClip, Reason> Clip(ClipShape shape)
    {
        var clip = new FakeClip(shape);
        _clips.Add(clip);
        return Result<IClip, Reason>.Ok(clip);
    }

    /// <inheritdoc />
    public Option<PickHit> Pick(PickRay ray, PickOptions options)
    {
        _picks.Add((ray, options));
        return _pick;
    }

    /// <inheritdoc />
    /// <remarks>Answers what <see cref="WillPick"/> set, as a struct, and records the call like <see cref="Pick"/>.</remarks>
    public bool TryPick(PickRay ray, PickOptions options, out PickResult hit)
    {
        _picks.Add((ray, options));
        hit = _pick.TryGet(out var union) ? PickResult.Of(union) : default;
        return _pick.IsSome;
    }

    /// <inheritdoc />
    public Result<StyleStory, Reason> Explain(GhostId ghost) =>
        _stories.TryGetValue(ghost, out var story)
            ? Result<StyleStory, Reason>.Ok(story)
            : Result<StyleStory, Reason>.Error(Refusal(ReasonCodes.NotFound, nameof(GhostId)));

    /// <inheritdoc />
    public bool Matches(StyleMatch match, StructureRef structure) => _matches.Contains((match, structure));

    private static bool SameClip(Option<ClipShape> a, Option<ClipShape> b) =>
        a.TryGet(out var x) ? b.TryGet(out var y) && x == y : !b.TryGet(out _);

    private static Reason Refusal(ReasonCode code, string type) => Reason.Of(code, RuleId.Contract, ValueMap.Of("type", type));
}

/// <summary>An added layer as the fake holds it.</summary>
public sealed class FakeStyleLayer(StyleLayerDescriptor descriptor) : IStyleLayer
{
    /// <summary>What was added.</summary>
    public StyleLayerDescriptor Descriptor { get; } = descriptor;

    /// <summary>Whether it was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public StyleLayerId Id => Descriptor.Id;

    /// <inheritdoc />
    public bool Enabled { get; private set; } = descriptor.Enabled;

    /// <inheritdoc />
    public void Enable() => Enabled = true;

    /// <inheritdoc />
    public void Disable() => Enabled = false;

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}

/// <summary>A ghost set as the fake holds it.</summary>
public sealed class FakeStyleSet(StyleSetId id) : IStyleSet
{
    private readonly HashSet<GhostId> _members = new();

    /// <inheritdoc />
    public StyleSetId Id { get; } = id;

    /// <inheritdoc />
    public int Count => _members.Count;

    /// <inheritdoc />
    public bool Contains(GhostId ghost) => _members.Contains(ghost);

    /// <inheritdoc />
    public void Put(GhostId ghost)
    {
        _members.Clear();
        _members.Add(ghost);
    }

    /// <inheritdoc />
    public void Replace(ValueList<GhostId> ghosts)
    {
        _members.Clear();
        Add(ghosts);
    }

    /// <inheritdoc />
    public void Add(ValueList<GhostId> ghosts)
    {
        foreach (var ghost in ghosts)
            _members.Add(ghost);
    }

    /// <inheritdoc />
    public void Remove(ValueList<GhostId> ghosts)
    {
        foreach (var ghost in ghosts)
            _members.Remove(ghost);
    }

    /// <inheritdoc />
    public void Clear() => _members.Clear();
}

/// <summary>A structure set as the fake holds it.</summary>
public sealed class FakeStructureSet(StyleSetId id) : IStructureSet
{
    private readonly HashSet<StructureRef> _members = new();

    /// <inheritdoc />
    public StyleSetId Id { get; } = id;

    /// <inheritdoc />
    public int Count => _members.Count;

    /// <inheritdoc />
    public bool Contains(StructureRef structure) => _members.Contains(structure);

    /// <inheritdoc />
    public void Put(StructureRef structure)
    {
        _members.Clear();
        _members.Add(structure);
    }

    /// <inheritdoc />
    public void Replace(ValueList<StructureRef> structures)
    {
        _members.Clear();
        Add(structures);
    }

    /// <inheritdoc />
    public void Add(ValueList<StructureRef> structures)
    {
        foreach (var structure in structures)
            _members.Add(structure);
    }

    /// <inheritdoc />
    public void Remove(ValueList<StructureRef> structures)
    {
        foreach (var structure in structures)
            _members.Remove(structure);
    }

    /// <inheritdoc />
    public void Clear() => _members.Clear();
}

/// <summary>A highlight as the fake holds it.</summary>
public sealed class FakeHighlight(HighlightRequest request) : IDisposable
{
    /// <summary>What was asked for.</summary>
    public HighlightRequest Request { get; } = request;

    /// <summary>Whether it was ended early.</summary>
    public bool Ended { get; private set; }

    /// <inheritdoc />
    public void Dispose() => Ended = true;
}

/// <summary>A drafts handle as the fake holds it: what it shows now and what it was handed to.</summary>
public sealed class FakeDrafts(DraftsId id, Action freed) : IDrafts
{
    private readonly List<Submission> _hands = new();

    /// <inheritdoc />
    public DraftsId Id { get; } = id;

    /// <summary>The raw drafts shown now; empty when none are.</summary>
    public ValueList<GhostDraft> Shown { get; private set; } = ValueList<GhostDraft>.Empty;

    /// <summary>The preview shown now, if one is.</summary>
    public Option<Preview> ShownPreview { get; private set; }

    /// <summary>The ticket the drafts wait on, after a hand to a <see cref="Submission.Sent"/>.</summary>
    public Option<Ticket> PendingOn { get; private set; }

    /// <summary>Every submission the drafts were handed to, in order.</summary>
    public IReadOnlyList<Submission> Hands => _hands;

    /// <summary>Whether it was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Whether it shows anything.</summary>
    public bool ShowsAnything => Shown.Count > 0 || ShownPreview.TryGet(out _);

    /// <inheritdoc />
    public void Show(ValueList<GhostDraft> drafts)
    {
        Clear();
        Shown = drafts;
    }

    /// <inheritdoc />
    public void Show(Preview preview)
    {
        Clear();
        ShownPreview = Option<Preview>.Some(preview);
    }

    /// <inheritdoc />
    public void Clear()
    {
        Shown = ValueList<GhostDraft>.Empty;
        ShownPreview = Option<Preview>.None;
        PendingOn = Option<Ticket>.None;
    }

    /// <inheritdoc />
    public void Hand(Submission submission)
    {
        _hands.Add(submission);
        switch (submission)
        {
            case Submission.Answered:
                Clear();
                break;
            case Submission.Sent sent:
                PendingOn = Option<Ticket>.Some(sent.Ticket);
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Clear();
        Disposed = true;
        freed();
    }
}

/// <summary>An installed clip as the fake holds it.</summary>
public sealed class FakeClip(ClipShape shape) : IClip
{
    /// <inheritdoc />
    public ClipShape Shape { get; private set; } = shape;

    /// <summary>Whether it was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public void Move(ClipShape to) => Shape = to;

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
