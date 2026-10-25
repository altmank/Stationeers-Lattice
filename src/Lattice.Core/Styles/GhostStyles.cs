using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.Core.Styles.Library;
using Lattice.Core.Validation;

namespace Lattice.Core.Styles;

/// <summary>Validation's verdict changes, as styles read them.</summary>
internal interface IVerdictFeed
{
    event Action<ValueList<VerdictChange>> VerdictsChanged;
}

/// <summary>Executors' reports on a ghost, as styles read them.</summary>
internal interface IStatusReader
{
    ValueList<ExecutorReport> StatusOf(GhostId ghost);
}

/// <summary>What the styles are built over.</summary>
/// <param name="Board">The board.</param>
/// <param name="Viewer">The local viewer.</param>
/// <param name="Shapes">Pieces' placed shapes.</param>
/// <param name="Grid">The grid.</param>
/// <param name="Scheduler">The frame scheduler: the styles' work runs in the drawing budget.</param>
/// <param name="Clock">The main-thread clock (highlights' lives, label choice).</param>
/// <param name="Looks">Pieces' families and bounds.</param>
internal sealed record StyleParts(GhostBoard Board, Actor Viewer, Shapes Shapes, IGrid Grid, FrameScheduler Scheduler, IClock Clock, IPieceLooks Looks)
{
    /// <summary>Validation's verdicts; none when validation is off (every finding reads unchecked).</summary>
    public IVerdictFeed? Verdicts { get; init; }

    /// <summary>Executors' reports; none when executors are off (every status reads none).</summary>
    public IStatusReader? Statuses { get; init; }

    /// <summary>The built world, for an overlay's turn; none draws overlays unturned.</summary>
    public IWorldQuery? World { get; init; }

    /// <summary>Whether this seat draws (module <c>render</c>): without it no draw plan and no labels are kept.</summary>
    public bool Drawing { get; init; } = true;

    /// <summary>
    /// Lattice's starter library: its looks are the Library band, below every consumer's layer, its default palette is
    /// the base every layer's palette overlays, and its palettes are the bases a layer's palette may name. None: no
    /// Library band, a stand-in base palette, and no palette base is known.
    /// </summary>
    public StarterLibrary? Library { get; init; }

    /// <summary>The game's paint colours, for finish paint; by default none, so finish paint shows its fallback token.</summary>
    public IPaintColours Paints { get; init; } = new NoPaintColours();
}

/// <summary>Counts the invalidation tests read.</summary>
internal sealed class StyleCounters
{
    /// <summary>Things that took a new signature.</summary>
    public long Resignatures { get; set; }

    /// <summary>Things moved to a new generation (layer added or removed, policy change, board reset).</summary>
    public long Passed { get; set; }
}

/// <summary>A layer as the styles hold it.</summary>
internal sealed class LayerEntry(StyleLayerDescriptor descriptor, long registration, GhostStyles styles, bool library = false) : IStyleLayer
{
    private readonly StyleSource[] _sources = library ? LibrarySources(descriptor) : Sources(descriptor);

    public StyleLayerDescriptor Descriptor { get; } = descriptor;

    public long Registration { get; } = registration;

    public int Band { get; } = library ? Bands.Library : (int)descriptor.Band;

    public StyleLayerId Id => Descriptor.Id;

    public bool Enabled { get; private set; } = descriptor.Enabled;

    public bool Disposed { get; private set; }

    public StyleSource Source(int rule) => _sources[rule];

    public void Enable()
    {
        if (Enabled || Disposed)
            return;
        Enabled = true;
        styles.Toggled();
    }

    public void Disable()
    {
        if (!Enabled || Disposed)
            return;
        Enabled = false;
        styles.Toggled();
    }

    public void Dispose()
    {
        if (Disposed)
            return;
        Disposed = true;
        styles.Removed(this);
    }

    private static StyleSource[] Sources(StyleLayerDescriptor layer)
    {
        var sources = new StyleSource[layer.Rules.Count];
        for (var i = 0; i < sources.Length; i++)
            sources[i] = new StyleSource.Layer(layer.Band, layer.Id, i);
        return sources;
    }

    private static StyleSource[] LibrarySources(StyleLayerDescriptor layer)
    {
        var sources = new StyleSource[layer.Rules.Count];
        for (var i = 0; i < sources.Length; i++)
            sources[i] = new StyleSource.Library(i);
        return sources;
    }
}

/// <summary>Relational selectors' picks, worked out afresh when the board or the built world moved.</summary>
internal sealed class Relations(GhostBoard board, Actor viewer)
{
    public bool Stale(Leaf leaf) => leaf.PickedAt != Now;

    /// <summary>The leaf with its picks as of now.</summary>
    public Leaf Fresh(Leaf leaf)
    {
        if (Stale(leaf))
            leaf.Picked = Pick(leaf);
        leaf.PickedAt = Now;
        return leaf;
    }

    public HashSet<GhostId> Pick(Leaf leaf)
    {
        var picked = new HashSet<GhostId>();
        if (leaf.Match is StyleMatch.Picks picks && board.Select(picks.Which, viewer).TryGet(out var entries))
            foreach (var entry in entries)
                picked.Add(entry.Id);
        return picked;
    }

    public (long Board, long Selectors) Now => (board.Revision.Value, board.Selectors?.Revision ?? 0);
}

/// <summary>
/// Style resolution: works out every drawn thing's look from the layers, in pure Core code. A thing's facets form a
/// signature, and looks are resolved and cached per signature. What moves a thing to a new signature costs that thing
/// (a change set's ghosts, verdicts and statuses in drawing-budget slices; set membership in the call); toggles, palettes
/// and motion re-resolve live signatures only; adding or removing a layer, a policy change and a board reset start a
/// new generation that things move to in slices. Main thread. An idle frame allocates nothing. Consumer callbacks
/// (<see cref="ClipChanged"/>) run last in <see cref="Frame"/>, as plain calls.
/// </summary>
internal sealed class GhostStyles : IGhostStyles, IMembership, ISlicedWork
{
    /// <summary>Things worked out per slice step.</summary>
    private const int StepSize = 32;

    private const int RecentDrawn = 64;

    private readonly GhostBoard _board;
    private readonly Actor _viewer;
    private readonly StyleParts _parts;
    private readonly LeafBook _leaves = new();
    private readonly List<LayerEntry> _layers = new(16);
    private readonly List<Generation> _generations = new(2);
    private readonly List<MatchWatch> _watches = new(4);
    private readonly PaletteInForce _palette;
    private readonly Resolver _resolver;
    private readonly StyleSets _sets;
    private readonly Relations _relations;
    private readonly Facets _facets;
    private readonly Dictionary<(PrefabKey Piece, Turn Turn, int X, int Y, int Z), Axis?> _faceAxes = new();
    private readonly Clips _clips = new();
    private readonly PickIndex _pick;
    private readonly PickFilter _pickFilter;
    private readonly DrawPlan _plan;
    private readonly PlanView _planView;
    private readonly LabelChooser _labels = new();
    private readonly Dictionary<GhostId, GhostThings> _ghosts = new();
    private readonly Dictionary<ReferenceId, Thing> _structures = new();
    private readonly List<Thing> _all = new(1024);
    private readonly Dictionary<GhostId, Finding> _findings = new();
    private readonly Dictionary<DraftsId, DraftsHandle> _drafts = new();
    private readonly Dictionary<Actor, int> _owners = new();
    private readonly Queue<GhostId> _queue = new(256);
    private readonly HashSet<GhostId> _queued = new();
    private readonly List<(ChangeSetId Id, long At)> _awaiting = new(16);
    private readonly ChangeSetId[] _drawn = new ChangeSetId[RecentDrawn];
    private readonly List<Leaf> _relational = new(4);
    private readonly Action<ChangeSet> _onChanged;
    private readonly Action<ValueList<VerdictChange>> _onVerdicts;
    private readonly Action<GhostId> _onStatus;
    private readonly Action<Ticket, Outcome> _onAnswered;
    private readonly Action _onReset;
    private ulong[] _scratch = new ulong[4];
    private Generation _current;
    private Generation? _next;
    private List<Thing>? _pass;
    private int _passAt;
    private int _generationNumber;
    private int _lookVersion;
    private long _registrations;
    private long _enqueued;
    private long _dequeued;
    private int _drawnAt;
    private int _policyEpoch;
    private (long Board, long Selectors) _relationsAt = (-1, -1);
    private SlicedJob? _job;
    private bool _ended;
    private Option<WorldPoint> _camera;

    public GhostStyles(StyleParts parts)
    {
        _parts = parts;
        _board = parts.Board;
        _viewer = parts.Viewer;
        _palette = new PaletteInForce(parts.Library?.Default, parts.Library is { } held ? held.PaletteOf : null, parts.Paints);
        _resolver = new Resolver(_palette);
        _sets = new StyleSets(this);
        _relations = new Relations(_board, _viewer);
        _facets = new Facets(_viewer, _sets, _relations);
        _pick = new PickIndex(parts.Shapes, parts.Looks);
        _pickFilter = new PickFilter(this);
        _plan = new DrawPlan(Look);
        _planView = new PlanView(this);
        _current = new Generation(++_generationNumber);
        _generations.Add(_current);
        if (parts.Library is { } library)
        {
            var band = new StyleLayerDescriptor(StyleLayerId.Known("library"), (StyleBand)Bands.Library, 0, library.Rules);
            _layers.Add(new LayerEntry(band, ++_registrations, this, library: true));
            foreach (var rule in library.Rules)
                _leaves.Take(rule.Match);
            Compile(_current, new List<HighlightEntry>(0));
        }
        _policyEpoch = _board.Policies.Epoch;
        _onChanged = OnChanged;
        _onVerdicts = OnVerdicts;
        _onStatus = OnStatus;
        _onAnswered = OnAnswered;
        _onReset = OnReset;
        _board.Events.Changed += _onChanged;
        _board.Events.StatusChanged += _onStatus;
        _board.Events.Answered += _onAnswered;
        _board.Events.BoardReset += _onReset;
        if (parts.Verdicts is { } verdicts)
            verdicts.VerdictsChanged += _onVerdicts;
        foreach (var entry in _board.Index.Ghosts.Keys)
            Enqueue(entry);
    }

    public StyleCounters Counters { get; } = new();

    /// <summary>How many looks were resolved.</summary>
    public long Resolves => _resolver.Count;

    /// <summary>Whether work is waiting for the drawing slices.</summary>
    public bool Busy => _queue.Count > 0 || _pass is not null || RelationsStale;

    /// <summary>The renderer's read surface.</summary>
    public IDrawPlan Plan => _planView;

    /// <summary>The generation things are signed in now.</summary>
    internal Generation Newest => _next ?? _current;

    internal int ThingCount => _all.Count;

    internal PickIndex Picks => _pick;

    /// <summary>The renderer's projector for label rectangles; none culls nothing.</summary>
    public ILabelProjector? Projector { get; set; }

    public Option<ClipShape> InForce => _clips.InForce;

    public event Action<Option<ClipShape>> ClipChanged { add => _clips.Changed += value; remove => _clips.Changed -= value; }

    /// <summary>Where the camera is, for label choice; the renderer sets it each frame.</summary>
    public void View(WorldPoint camera) => _camera = Option.Some(camera);

    /// <summary>
    /// Watches a match per batch (a renderer's claims): its leaves join every generation while the watch is held, in a
    /// new generation worked out in slices as a layer add is; the batches drawn now are judged at once with the bits
    /// their generation has. On ended styles the watch holds for nothing.
    /// </summary>
    internal IMatchWatch Watch(StyleMatch match)
    {
        var watch = new MatchWatch(match, Unwatch);
        if (_ended)
            return watch;
        _leaves.Take(match);
        _watches.Add(watch);
        foreach (var generation in _generations)
            watch.Put(generation, RuleProgram.Compile(match, generation, _leaves, known: true));
        StartGeneration();
        return watch;
    }

    private void Unwatch(MatchWatch watch)
    {
        if (_ended || !_watches.Remove(watch))
            return;
        _leaves.Give(watch.Match);
        StartGeneration();
    }

    // ---- IGhostStyles ----

    public Result<IStyleLayer, Reason> Add(StyleLayerDescriptor layer)
    {
        if (string.IsNullOrEmpty(layer.Id.Value) || !Bands.IsNameable(layer.Band))
            return Result<IStyleLayer, Reason>.Error(Reason.InvalidValue(nameof(StyleLayerDescriptor), layer.Id.Value, "named-layer-in-a-named-band"));
        if (layer.Palette.TryGet(out var palette) && !_palette.Knows(palette))
            return Result<IStyleLayer, Reason>.Error(Reason.InvalidValue(nameof(Palette.Base), palette.Base.Reduce(default(PaletteId)).Value, "library-palette"));
        foreach (var held in _layers)
            if (held.Id == layer.Id)
                return Result<IStyleLayer, Reason>.Error(Reason.Of(ReasonCodes.AlreadyRegistered, StyleRules.Styles, ValueMap.Of("layer", layer.Id.Value)));
        var entry = new LayerEntry(layer, ++_registrations, this);
        _layers.Add(entry);
        foreach (var rule in layer.Rules)
            _leaves.Take(rule.Match);
        Repaint();
        StartGeneration();
        return Result<IStyleLayer, Reason>.Ok(entry);
    }

    public Result<IStyleSet, Reason> Set(StyleSetId id) =>
        string.IsNullOrEmpty(id.Value)
            ? Result<IStyleSet, Reason>.Error(Reason.InvalidValue(nameof(StyleSetId), "", "named"))
            : Result<IStyleSet, Reason>.Ok(_sets.Ghosts(id));

    public Result<IStructureSet, Reason> StructureSet(StyleSetId id) =>
        string.IsNullOrEmpty(id.Value)
            ? Result<IStructureSet, Reason>.Error(Reason.InvalidValue(nameof(StyleSetId), "", "named"))
            : Result<IStructureSet, Reason>.Ok(_sets.Structures(id));

    public Result<IDisposable, Reason> Highlight(HighlightRequest request)
    {
        if (request.For.Seconds <= 0f)
            return Result<IDisposable, Reason>.Error(Reason.InvalidValue(nameof(HighlightRequest), "0", "positive-duration"));
        var ghosts = new HashSet<GhostId>();
        var structures = new StructureRefs();
        switch (request.Targets)
        {
            case HighlightTargets.Ghosts ids:
                foreach (var id in ids.Ids)
                    ghosts.Add(id);
                break;
            case HighlightTargets.Picked picked:
                if (_board.Select(picked.Which, _viewer).TryGet(out var entries))
                    foreach (var entry in entries)
                        ghosts.Add(entry.Id);
                break;
            case HighlightTargets.Structures refs:
                foreach (var structure in refs.Refs)
                    structures.Add(structure);
                break;
        }

        var highlight = new HighlightEntry(++_registrations, request.Style, _parts.Clock.Now + TimeSpan.FromSeconds(request.For.Seconds), ghosts, structures, Ended);
        _sets.Highlights.Add(highlight);
        var owner = Paints<Paint.OwnerColour>(request.Style);
        var finish = Paints<Paint.FinishColour>(request.Style);
        foreach (var generation in _generations)
        {
            generation.Program.Insert(generation, highlight);
            generation.UsesOwner |= owner;
            generation.UsesFinish |= finish;
        }

        Touch(highlight);
        return Result<IDisposable, Reason>.Ok(highlight);
    }

    public Result<IDrafts, Reason> Drafts(DraftsId id)
    {
        if (string.IsNullOrEmpty(id.Value))
            return Result<IDrafts, Reason>.Error(Reason.InvalidValue(nameof(DraftsId), "", "named"));
        if (!_drafts.TryGetValue(id, out var handle))
            _drafts[id] = handle = new DraftsHandle(id, this);
        return Result<IDrafts, Reason>.Ok(handle);
    }

    public Result<IClip, Reason> Clip(ClipShape shape) => Result<IClip, Reason>.Ok(_clips.Install(shape));

    public Option<PickHit> Pick(PickRay ray, PickOptions options)
    {
        _pickFilter.Options = options;
        if (!_pick.Walk(ray, _pickFilter, out var found))
            return Option<PickHit>.None;
        var thing = found.Thing;
        return Option.Some<PickHit>(thing.Kind == ThingKind.Ghost
            ? new PickHit.OnGhost(thing.Id, thing.Part, found.Box.Cell, found.Face, found.Point, found.Distance)
            : new PickHit.OnDraft(thing.Drafts!.Id, thing.Index, thing.Record is { } trial ? Option.Some(trial) : Option<Ghost>.None,
                thing.Part, found.Box.Cell, found.Face, found.Point, found.Distance));
    }

    public bool TryPick(PickRay ray, PickOptions options, out PickResult hit)
    {
        _pickFilter.Options = options;
        if (!_pick.Walk(ray, _pickFilter, out var found))
        {
            hit = default;
            return false;
        }

        var thing = found.Thing;
        hit = thing.Kind == ThingKind.Ghost
            ? new PickResult(PickKind.Ghost, thing.Id, default, 0, thing.Part, found.Box.Cell, found.Face, found.Point, found.Distance)
            : new PickResult(PickKind.Draft, default, thing.Drafts!.Id, thing.Index, thing.Part, found.Box.Cell, found.Face, found.Point, found.Distance);
        return true;
    }

    public Result<StyleStory, Reason> Explain(GhostId ghost)
    {
        if (!_ghosts.TryGetValue(ghost, out var things) || things.Main is not { } thing)
            return Result<StyleStory, Reason>.Error(Reason.Of(ReasonCodes.NotFound, StyleRules.Styles, ValueMap.Of("ghost", ghost.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var look = Look(thing.Signature);
        var sources = new Dictionary<StyleProperty, StyleSource>(look.Sources.Length);
        for (var p = (int)StyleProperty.Presence; p < look.Sources.Length; p++)
            sources[(StyleProperty)p] = look.Sources[p];
        var facets = new List<StyleMatch>();
        var generation = thing.Signature.Generation;
        for (var bit = 0; bit < generation.Leaves.Count; bit++)
            if (thing.Signature.Has(bit) && generation.Leaves[bit].Match is { } match)
                facets.Add(match);
        return Result<StyleStory, Reason>.Ok(new StyleStory(ghost, look.Style, ValueMap<StyleProperty, StyleSource>.From(sources), ValueList.From(facets)));
    }

    public bool Matches(StyleMatch match, StructureRef structure) => StructureMatch(match, structure);

    // ---- what tests and the renderer read ----

    /// <summary>A drawn ghost's look (its placement part, else its overlay); none when it is not drawn.</summary>
    public Option<ResolvedLook> LookOf(GhostId ghost) =>
        _ghosts.TryGetValue(ghost, out var things) && things.Main is { } thing ? Option.Some(Look(thing.Signature)) : Option<ResolvedLook>.None;

    /// <summary>One part of a drawn ghost's look (a swap draws both); none when that part is not drawn.</summary>
    public Option<ResolvedLook> LookOf(GhostId ghost, PartKind part) =>
        _ghosts.TryGetValue(ghost, out var things) && (part == PartKind.Existing ? things.Existing : things.Placement) is { } thing
            ? Option.Some(Look(thing.Signature))
            : Option<ResolvedLook>.None;

    /// <summary>Whether a ghost is in the draw plan now.</summary>
    public bool Draws(GhostId ghost) => _ghosts.TryGetValue(ghost, out var things) && things.Main is { Batch: not null };

    public IReadOnlyCollection<DraftsHandle> DraftHandles => _drafts.Values;

    /// <summary>A signature's look under the layers and palette in force: cached until a toggle, palette or motion change.</summary>
    public ResolvedLook Look(Signature signature)
    {
        if (signature.LookVersion != _lookVersion || signature.Look is null)
        {
            signature.Look = _resolver.Resolve(signature);
            signature.LookVersion = _lookVersion;
        }

        return signature.Look;
    }

    // ---- frame and end ----

    /// <summary>
    /// The styles' frame, after the world's slices and events: Lattice's own work (<see cref="OwnFrame"/>), then
    /// consumers' callbacks (<see cref="RaiseChanges"/>) last. An idle frame allocates nothing.
    /// </summary>
    public void Frame()
    {
        OwnFrame();
        RaiseChanges();
    }

    /// <summary>Raises <see cref="ClipChanged"/> when the clip in force changed this frame: a plain call.</summary>
    public void RaiseChanges()
    {
        if (!_ended)
            _clips.Raise();
    }

    /// <summary>Ends timed highlights, notices a policy change and chooses labels 10 times a second.</summary>
    public void OwnFrame()
    {
        if (_ended)
            return;
        _plan.Frame++;
        var now = _parts.Clock.Now;
        var highlights = _sets.Highlights;
        for (var i = highlights.Count - 1; i >= 0; i--)
            if (now >= highlights[i].Until)
                highlights[i].Dispose();
        if (_board.Policies.Epoch != _policyEpoch)
        {
            _policyEpoch = _board.Policies.Epoch;
            Resync();
        }

        if (RelationsStale)
            Schedule();
        if (_parts.Drawing && _camera.TryGet(out var camera) && _labels.Due(now))
            _labels.Choose(now, camera, _plan.Batches, Projector);
    }

    /// <summary>The world ended: drafts are dropped, nothing is drawn, events are left.</summary>
    public void End()
    {
        if (_ended)
            return;
        _ended = true;
        foreach (var handle in _drafts.Values)
            handle.End();
        _board.Events.Changed -= _onChanged;
        _board.Events.StatusChanged -= _onStatus;
        _board.Events.Answered -= _onAnswered;
        _board.Events.BoardReset -= _onReset;
        if (_parts.Verdicts is { } verdicts)
            verdicts.VerdictsChanged -= _onVerdicts;
        _plan.Clear();
        _pick.Clear();
        _labels.Clear();
        _clips.End();
        foreach (var watch in _watches)
            watch.Drop();
        _watches.Clear();
        _queue.Clear();
        _queued.Clear();
        _pass = null;
    }

    // ---- sliced work ----

    StepOutcome ISlicedWork.Step()
    {
        if (_ended)
            return StepOutcome.Done;

        if (RelationsStale)
        {
            RefreshRelations();
            return StepOutcome.More;
        }

        var budget = StepSize;
        while (budget > 0 && _queue.Count > 0)
        {
            var ghost = _queue.Dequeue();
            _queued.Remove(ghost);
            _dequeued++;
            Refresh(ghost);
            budget--;
            Landed();
        }

        while (budget > 0 && _pass is not null)
        {
            if (_passAt < _pass.Count)
            {
                var thing = _pass[_passAt++];
                if (!thing.Gone && !ReferenceEquals(thing.Signature.Generation, Newest))
                    Sign(thing);

                budget--;
            }
            else
                FinishGeneration();
        }

        return _queue.Count > 0 || _pass is not null || RelationsStale ? StepOutcome.More : StepOutcome.Done;
    }

    // ---- what the handles call ----

    void IMembership.Moved(GhostId ghost) => Moved(ghost);

    void IMembership.Overlay(StructureRef structure) => Overlay(structure);

    internal void Toggled()
    {
        Repaint();
    }

    internal void Removed(LayerEntry layer)
    {
        _layers.Remove(layer);
        foreach (var rule in layer.Descriptor.Rules)
            _leaves.Give(rule.Match);
        Repaint();
        StartGeneration();
    }

    internal void Free(DraftsHandle handle)
    {
        if (_drafts.TryGetValue(handle.Id, out var held) && ReferenceEquals(held, handle))
            _drafts.Remove(handle.Id);
    }

    /// <summary>Draws one draft (a raw one, or a preview's trial ghost) as its parts, in the call.</summary>
    internal void ShowDraft(DraftsHandle handle, int index, Ghost? trial, GhostDraft? raw, Option<Finding> finding, List<Thing> shown)
    {
        if (_ended)
            return;
        var intent = trial?.Intent ?? raw!.Intent;
        if (Placed(intent).TryGet(out var placement))
        {
            var thing = Thing.OfDraft(handle, index, trial, raw, PartKind.Placement, placement.Piece, placement.At, placement.Turn,
                _parts.Shapes.Of(placement).Match(static s => s, static _ => (PlacedShape?)null), default);
            Start(thing, finding);
            shown.Add(thing);
        }

        if (Overlaid(intent).TryGet(out var target))
        {
            var thing = Thing.OfDraft(handle, index, trial, raw, PartKind.Existing, target.Piece, target.At, TurnOf(target), null, target);
            Start(thing, finding);
            shown.Add(thing);
        }
    }

    internal void Hide(Thing thing) => Drop(thing);

    /// <summary>Takes a new signature for a thing whose own facts changed (a draft gone pending).</summary>
    internal void Resign(Thing thing) => Sign(thing);

    /// <summary>Whether a change set's ghosts are already in the draw plan.</summary>
    internal bool Drawn(ChangeSetId changes)
    {
        for (var i = 0; i < _drawn.Length; i++)
            if (_drawn[i] == changes && changes != default)
                return true;
        return false;
    }

    private void Ended(HighlightEntry highlight)
    {
        _sets.Highlights.Remove(highlight);
        foreach (var generation in _generations)
            generation.Program.Remove(highlight);
        Touch(highlight);
    }

    private void Touch(HighlightEntry highlight)
    {
        foreach (var ghost in highlight.Ghosts)
            Moved(ghost);
        foreach (var structure in highlight.Structures.All)
            Overlay(structure);
    }

    // ---- board events ----

    private void OnChanged(ChangeSet changes)
    {
        foreach (var removal in changes.Removed)
        {
            var id = removal.Ghost.Id;
            RemoveGhost(id);
            _sets.Forget(id);
            _findings.Remove(id);
        }

        foreach (var ghost in changes.Added)
            Enqueue(ghost.Id);
        foreach (var change in changes.Changed)
            Enqueue(change.After.Id);
        _awaiting.Add((changes.Id, _enqueued));
        Landed();
    }

    private void OnVerdicts(ValueList<VerdictChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.After.TryGet(out var finding))
                _findings[change.Ghost] = finding;
            else
                _findings.Remove(change.Ghost);
            Enqueue(change.Ghost);
        }
    }

    private void OnStatus(GhostId ghost) => Enqueue(ghost);

    private void OnAnswered(Ticket ticket, Outcome outcome)
    {
        foreach (var handle in _drafts.Values)
            handle.Answered(ticket, outcome);
    }

    private void OnReset() => Resync();

    // ---- things ----

    private void Enqueue(GhostId ghost)
    {
        if (_queued.Add(ghost))
        {
            _queue.Enqueue(ghost);
            _enqueued++;
        }

        Schedule();
    }

    private void Schedule()
    {
        if (_ended || _job is { State: JobState.Running })
            return;
        _job = _parts.Scheduler.Schedule(WorkKind.Drawing, this, Priority.High).Match(static job => job, static _ => (SlicedJob?)null);
    }

    /// <summary>Change sets whose ghosts are all worked out are drawn now: drafts waiting for them go in the same slice.</summary>
    private void Landed()
    {
        while (_awaiting.Count > 0 && _awaiting[0].At <= _dequeued)
        {
            var changes = _awaiting[0].Id;
            _awaiting.RemoveAt(0);
            _drawn[_drawnAt] = changes;
            _drawnAt = (_drawnAt + 1) % _drawn.Length;
            foreach (var handle in _drafts.Values)
                handle.Drawn(changes);
        }
    }

    /// <summary>Brings a ghost's things up to the board: made, refreshed or removed. Linear in its footprint.</summary>
    private void Refresh(GhostId id)
    {
        if (!_board.Index.Ghosts.TryGetValue(id, out var entry) || !_board.Policies.MaySee(_viewer, entry.Ghost))
        {
            RemoveGhost(id);
            return;
        }

        var ghost = entry.Ghost;
        var placement = Placed(ghost.Intent);
        var target = Overlaid(ghost.Intent);
        _findings.TryGetValue(id, out var finding);
        var statuses = _parts.Statuses?.StatusOf(id) ?? ValueList<ExecutorReport>.Empty;
        if (_ghosts.TryGetValue(id, out var held) && held.Fits(placement, target))
        {
            held.Placement?.Refresh(ghost);
            held.Existing?.Refresh(ghost);
            Update(held.Placement, finding, statuses);
            Update(held.Existing, finding, statuses);
            return;
        }

        RemoveGhost(id);
        var things = new GhostThings();
        if (placement.TryGet(out var place))
        {
            things.Placement = Thing.OfGhost(ghost, PartKind.Placement, place.Piece, place.At, place.Turn, entry.Shape.Match(static s => s, static () => (PlacedShape?)null), default);
            things.Placement.Finding = finding;
            things.Placement.Statuses = statuses;
            Start(things.Placement, Option<Finding>.None);
        }

        if (target.TryGet(out var built))
        {
            things.Existing = Thing.OfGhost(ghost, PartKind.Existing, built.Piece, built.At, TurnOf(built), null, built);
            things.Existing.Finding = finding;
            things.Existing.Statuses = statuses;
            Start(things.Existing, Option<Finding>.None);
        }

        _ghosts[id] = things;
    }

    private void Update(Thing? thing, Finding? finding, ValueList<ExecutorReport> statuses)
    {
        if (thing is null)
            return;
        thing.Finding = finding;
        thing.Statuses = statuses;
        Sign(thing);
    }

    private void RemoveGhost(GhostId id)
    {
        if (!_ghosts.TryGetValue(id, out var things))
            return;
        _ghosts.Remove(id);
        if (things.Placement is { } placement)
            Drop(placement);
        if (things.Existing is { } existing)
            Drop(existing);
    }

    /// <summary>A ghost entered or left a set: its things take new signatures now. Constant cost.</summary>
    private void Moved(GhostId ghost)
    {
        if (_ended || !_ghosts.TryGetValue(ghost, out var things))
            return;
        if (things.Placement is { } placement)
            Sign(placement);
        if (things.Existing is { } existing)
            Sign(existing);
    }

    /// <summary>A structure entered or left a set or highlight: its overlay is drawn while any holds it.</summary>
    private void Overlay(StructureRef structure)
    {
        if (_ended)
            return;
        var held = _structures.TryGetValue(structure.Id, out var thing);
        if (_sets.Overlaid(structure))
        {
            if (held)
                Sign(thing!);
            else
            {
                thing = Thing.OfStructure(structure, TurnOf(structure));
                _structures[structure.Id] = thing;
                Start(thing, Option<Finding>.None);
            }
        }
        else if (held)
        {
            _structures.Remove(structure.Id);
            Drop(thing!);
        }
    }

    /// <summary>A new thing: listed, signed, drawn and indexed for picking.</summary>
    private void Start(Thing thing, Option<Finding> finding)
    {
        if (finding.TryGet(out var found))
            thing.Finding = found;
        thing.Family = FamilyOf(thing.Piece);
        thing.Families = _parts.Looks.Families(thing.Piece);
        thing.FaceAxis = thing.Shape is { } shape ? FaceAxes.Of(shape.Spots)
            : thing.Part == PartKind.Existing && Built(thing.Structure).TryGet(out var built) ? FaceAxisOf(thing.Piece, thing.At, built.Turn)
            : null;
        thing.ListSlot = _all.Count;
        _all.Add(thing);
        Sign(thing);
        _pick.Add(thing);
    }

    private void Drop(Thing thing)
    {
        if (thing.Gone)
            return;
        thing.Gone = true;
        if (_parts.Drawing)
            _plan.Remove(thing);
        _pick.Remove(thing);
        thing.Signature.Holders--;
        var last = _all.Count - 1;
        var moved = _all[last];
        _all[thing.ListSlot] = moved;
        moved.ListSlot = thing.ListSlot;
        _all.RemoveAt(last);
        thing.ListSlot = -1;
    }

    /// <summary>Signs a thing in the newest generation; a new signature moves it between batches. Allocates nothing for a known signature.</summary>
    private void Sign(Thing thing)
    {
        var generation = Newest;
        var signature = SignatureOf(thing, generation);
        if (ReferenceEquals(signature, thing.Signature))
            return;
        Counters.Resignatures++;
        var before = thing.Signature;
        if (before is not null)
        {
            before.Holders--;
            if (!ReferenceEquals(before.Generation, generation))
                Counters.Passed++;
        }

        signature.Holders++;
        thing.Signature = signature;
        if (!_parts.Drawing)
            return;
        _plan.Remove(thing);
        thing.StartFrame = Look(signature).Animated ? _plan.Frame : 0;
        _plan.Add(thing);
    }

    private Signature SignatureOf(Thing thing, Generation generation)
    {
        var words = generation.Words;
        if (_scratch.Length < words)
            _scratch = new ulong[Math.Max(words, _scratch.Length * 2)];
        Array.Clear(_scratch, 0, words);
        var leaves = generation.Leaves;
        for (var bit = 0; bit < leaves.Count; bit++)
            if (_facets.Holds(leaves[bit], thing))
                _scratch[bit >> 6] |= 1UL << (bit & 63);
        var owner = generation.UsesOwner ? OwnerOf(thing) : -1;
        var finish = generation.UsesFinish && Facets.FinishOf(thing.Intent) is { } asked && asked.Colour.TryGet(out var paint) ? paint.Index : -1;
        return generation.Intern(_scratch, words, thing.Tint, owner, finish);
    }

    private int OwnerOf(Thing thing)
    {
        if (thing.Kind == ThingKind.Structure)
            return -1;
        var owner = thing.Record?.PlacedBy ?? _viewer;
        if (!_owners.TryGetValue(owner, out var index))
            _owners[owner] = index = _owners.Count;
        return index;
    }

    /// <summary>The piece's family now: constant cost, and a family a mod registers later is seen from then on.</summary>
    private PieceFamily FamilyOf(PrefabKey piece) => _parts.Looks.Family(piece);

    private Turn TurnOf(StructureRef structure) => Built(structure).TryGet(out var info) ? info.Turn : Turn.None;

    /// <summary>The structure as Lattice's copy of the world holds it; none when it has not seen it.</summary>
    private Option<StructureInfo> Built(StructureRef structure) =>
        _parts.World is { } world ? world.Structure(structure.Id) : Option<StructureInfo>.None;

    /// <summary>
    /// The axis a piece's face is square to, at a cell and turn; null for a piece off the faces. Kept per piece, turn and
    /// place within its large cell (the spots move with whole cells), so asking again allocates nothing.
    /// </summary>
    private Axis? FaceAxisOf(PrefabKey piece, Cell at, Turn turn)
    {
        var key = (piece, turn, Within(at.X), Within(at.Y), Within(at.Z));
        if (_faceAxes.TryGetValue(key, out var known))
            return known;
        var axis = _parts.Shapes.Of(new Placement(piece, at, turn)).TryGet(out var shape) ? FaceAxes.Of(shape.Spots) : null;
        _faceAxes[key] = axis;
        return axis;
    }

    private static int Within(int key) => ((key % 20) + 20) % 20;

    private static Option<Placement> Placed(Intent intent) => intent switch
    {
        Intent.Build build => Option.Some(build.Target),
        Intent.Swap swap => Option.Some(swap.New),
        _ => Option<Placement>.None,
    };

    private static Option<StructureRef> Overlaid(Intent intent) => intent switch
    {
        Intent.Remove remove => Option.Some(remove.Target),
        Intent.Swap swap => Option.Some(swap.Old),
        Intent.FinishOnly finish => Option.Some(finish.Target),
        _ => Option<StructureRef>.None,
    };

    // ---- generations, palette ----

    /// <summary>Layers, palettes or motion changed: palette and motion are worked out again and live signatures re-resolve.</summary>
    private void Repaint()
    {
        _palette.Overlay(LayersInOrder());
        _lookVersion++;
    }

    private List<LayerEntry> LayersInOrder()
    {
        var ordered = new List<LayerEntry>(_layers);
        ordered.Sort(static (a, b) =>
            a.Band != b.Band ? a.Band.CompareTo(b.Band)
            : a.Descriptor.Order != b.Descriptor.Order ? a.Descriptor.Order.CompareTo(b.Descriptor.Order)
            : a.Registration.CompareTo(b.Registration));
        return ordered;
    }

    /// <summary>A new generation from the live leaves only; every drawn thing moves to it in drawing slices.</summary>
    private void StartGeneration()
    {
        _leaves.Forget();
        var generation = new Generation(++_generationNumber);
        var live = new List<HighlightEntry>(_sets.Highlights.Count);
        foreach (var highlight in _sets.Highlights)
            if (!highlight.Ended)
                live.Add(highlight);
        Compile(generation, live);
        _next = generation;
        _generations.Add(generation);
        _pass = new List<Thing>(_all);
        _passAt = 0;
        _relationsAt = (-1, -1);
        Schedule();
    }

    /// <summary>The generation's rule program from the layers and live highlights, and what it reads.</summary>
    private void Compile(Generation generation, List<HighlightEntry> live)
    {
        generation.Program = RuleProgram.Of(generation, _layers, live, _leaves);
        foreach (var rule in generation.Program.Rules)
        {
            generation.UsesOwner |= Paints<Paint.OwnerColour>(rule.Style);
            generation.UsesFinish |= Paints<Paint.FinishColour>(rule.Style);
        }

        foreach (var watch in _watches)
            watch.Put(generation, RuleProgram.Compile(watch.Match, generation, _leaves));
        _relational.Clear();
        foreach (var leaf in generation.Leaves)
            if (leaf.Relational)
                _relational.Add(leaf);
    }

    private void FinishGeneration()
    {
        var next = _next!;
        _current = next;
        _next = null;
        _pass = null;
        for (var i = _generations.Count - 1; i >= 0; i--)
        {
            var old = _generations[i];
            if (ReferenceEquals(old, next))
                continue;
            _generations.RemoveAt(i);
            if (_parts.Drawing)
                _plan.DropEmpty(old);
        }

        next.DropUnheld();
        foreach (var watch in _watches)
            watch.Keep(next);
    }

    /// <summary>A policy change or a board reset: every ghost is worked out again (shown or not), in a new generation.</summary>
    private void Resync()
    {
        foreach (var ghost in _ghosts.Keys)
            Enqueue(ghost);
        foreach (var ghost in _board.Index.Ghosts.Keys)
            Enqueue(ghost);
        StartGeneration();
    }

    private bool RelationsStale => _relational.Count > 0 && _relationsAt != _relations.Now;

    /// <summary>Relational selectors pick afresh; ghosts whose membership changed take new signatures in slices.</summary>
    private void RefreshRelations()
    {
        _relationsAt = _relations.Now;
        foreach (var leaf in _relational)
        {
            var before = leaf.Picked;
            var after = _relations.Pick(leaf);
            leaf.Picked = after;
            leaf.PickedAt = _relationsAt;
            if (before is not null)
                foreach (var ghost in before)
                    if (!after.Contains(ghost))
                        Enqueue(ghost);
            foreach (var ghost in after)
                if (before is null || !before.Contains(ghost))
                    Enqueue(ghost);
        }
    }

    /// <summary>Whether a style paints anything with a paint of this kind.</summary>
    private static bool Paints<TPaint>(Style style) where TPaint : Paint =>
        style.FillPaint.TryGet(out var fill) && fill is TPaint
        || style.Edge.TryGet(out var edge) && edge is EdgeLook.Line { Paint: TPaint }
        || style.Glow.TryGet(out var glow) && glow is GlowLook.Lit { Paint: TPaint };

    // ---- structures ----

    /// <summary>
    /// Whether a match holds for a built structure: its piece's family (a constant-cost lookup), <c>Picks(OfPiece)</c>,
    /// <c>Everything</c>, <c>Part(Existing)</c>, <c>Structure</c> and structure-set membership; ghost-only cases are
    /// false. Judged on the match as given, without hashing it: linear in the match's size.
    /// </summary>
    private bool StructureMatch(StyleMatch match, StructureRef structure)
    {
        switch (match)
        {
            case StyleMatch.Everything:
            case StyleMatch.Structure:
                return true;
            case StyleMatch.Part part:
                return part.Kind == PartKind.Existing;
            case StyleMatch.Family family:
                return FamilyTree.Holds(_parts.Looks.Families(structure.Piece), family.Of);
            case StyleMatch.Facing facing:
                return Built(structure).TryGet(out var built) && FaceAxisOf(structure.Piece, structure.At, built.Turn) == facing.Normal;
            case StyleMatch.Picks picks:
                return PieceSelects(picks.Which, structure.Piece);
            case StyleMatch.InSet inSet:
                return _sets.Structures(inSet.Set).Contains(structure);
            case StyleMatch.All all:
                for (var i = 0; i < all.Of.Count; i++)
                    if (!StructureMatch(all.Of[i], structure))
                        return false;
                return true;
            case StyleMatch.Any any:
                for (var i = 0; i < any.Of.Count; i++)
                    if (StructureMatch(any.Of[i], structure))
                        return true;
                return false;
            case StyleMatch.Not not:
                return !StructureMatch(not.Match, structure);
            default:
                return false;
        }
    }

    private static bool PieceSelects(Selector selector, PrefabKey piece)
    {
        switch (selector)
        {
            case Selector.Everything:
                return true;
            case Selector.OfPiece of:
                return of.Piece == piece;
            case Selector.All all:
                if (all.Of.Count == 0)
                    return false;
                for (var i = 0; i < all.Of.Count; i++)
                    if (!PieceSelects(all.Of[i], piece))
                        return false;
                return true;
            case Selector.Any any:
                for (var i = 0; i < any.Of.Count; i++)
                    if (PieceSelects(any.Of[i], piece))
                        return true;
                return false;
            default:
                return false;
        }
    }

    /// <summary>A ghost's drawn things: the planned piece and the overlay of its built one.</summary>
    private sealed class GhostThings
    {
        public Thing? Placement { get; set; }

        public Thing? Existing { get; set; }

        /// <summary>The part <see cref="IGhostStyles.Explain"/> and looks read: the placement, else the overlay.</summary>
        public Thing? Main => Placement ?? Existing;

        public bool Fits(Option<Placement> placement, Option<StructureRef> target) =>
            (placement.TryGet(out var p) ? Placement is { } held && held.Piece == p.Piece && held.At == p.At && held.Turn == p.Turn : Placement is null)
            && (target.TryGet(out var t) ? Existing is { } over && over.Structure == t : Existing is null);
    }

    /// <summary>The pick's filter, reused for every pick.</summary>
    private sealed class PickFilter(GhostStyles styles) : IPickFilter
    {
        public PickOptions Options { get; set; } = PickOptions.Default;

        public bool Accept(Thing thing, PickBox box)
        {
            if (thing.Gone || thing.Kind == ThingKind.Structure)
                return false;
            var draft = thing.Kind == ThingKind.Draft;
            if ((Options.Drafts == DraftPicks.Exclude && draft) || (Options.Drafts == DraftPicks.Only && !draft))
                return false;
            var look = styles.Look(thing.Signature);
            if (!look.Pickable)
                return false;
            if (styles._clips.Beyond(box.Centre)
                && (Options.BeyondClip == BeyondClipPicks.Exclude || look.Style.BeyondClip.TryGet(out var beyond) && beyond is BeyondClipLook.Hidden))
                return false;
            return !Options.Only.TryGet(out var only) || styles._facets.Judge(only, thing, styles._leaves);
        }

        public bool Before(Thing thing, Thing other)
        {
            if (thing.Kind != other.Kind)
                return thing.Kind == ThingKind.Ghost;
            return thing.Kind == ThingKind.Ghost ? thing.Id.Value < other.Id.Value : thing.Index < other.Index;
        }
    }

    /// <summary>The draw plan as the renderer reads it.</summary>
    private sealed class PlanView(GhostStyles styles) : IDrawPlan
    {
        public long Frame => styles._plan.Frame;

        public int BatchCount => styles._plan.Batches.Count;

        public Batch BatchAt(int index) => styles._plan.Batches[index];

        public Option<ClipShape> Clip => styles._clips.InForce;

        public int LabelCount => styles._labels.Count;

        public ShownLabel LabelAt(int index) => styles._labels.At(index);
    }
}

/// <summary>The rule ids of the styles' refusals.</summary>
internal static class StyleRules
{
    public static readonly RuleId Styles = RuleId.Known("lattice.styles");
}
