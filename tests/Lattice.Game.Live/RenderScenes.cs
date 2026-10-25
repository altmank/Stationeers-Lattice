using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Assets.Scripts.Objects;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.EntryPoint;
using Lattice.Core.Styles;
using Lattice.Game.Looks;
using Lattice.Game.World;
using Lattice.Render.Unity;
using UnityEngine;
using Cell = Lattice.Api.Cell;
using Object = UnityEngine.Object;

namespace Lattice.Game.Live;

/// <summary>
/// The render scenes on a real part of the base: the look grid in free air just over the part's top, and
/// the overlay proofs on the part's own structures. Each scene has a camera view; <see cref="End"/>
/// takes the layers away and gives the hidden structures back to the game's drawing.
/// </summary>
internal sealed class Scenes(LatticeWorld world, RenderHost host, LiveCamera rig, Region region, Action<string> line)
{
    private const string FrameName = "StructureFrame";
    private const string WallName = "StructureCompositeWall";
    private const string PipeName = "StructurePipeStraight";
    private const string ControlName = "StructureAreaPowerControl";
    private const string CableName = "StructureCableStraight";

    private static List<string> Findings(ValidationReport report)
    {
        var said = new List<string>(4);
        foreach (var finding in report.Findings)
            said.Add(finding.ToString());
        return said;
    }

    private const float ColumnPitch = 30f;

    private static readonly Maker LiveMaker = Maker.Create(LatticeIdentity.Mod, "live-render").Match(static m => m, static r => throw new InvalidOperationException(r.ToString()));

    private readonly List<IDisposable> _held = new(16);
    private readonly List<Original> _originals = new(4);
    private Original? _faded;
    private Material? _fadedMaterial;

    public List<View> Views { get; } = new(16);

    /// <summary>The look grid's extent along x, in metres, for the clip plane's sweep.</summary>
    public (float Min, float Max) GridSpan { get; private set; }

    public IEnumerator Build()
    {
        if (world.StyleModule is not { } styles)
        {
            line("FAIL scenes: no styles module");
            yield break;
        }

        var ox = Large(region.Middle.x);
        var oy = Large(region.Top + 6f);
        var oz = Large(region.Middle.z);
        yield return Grid(styles, ox, oy, oz);
        yield return Proofs(styles, ox, oy, oz);
        yield return Marks(styles, ox - 40f, oy, oz);
        yield return XRay(styles, ox - 70f, oy, oz);
        rig.EachFrame = DrawFaded;
    }

    private IEnumerator Grid(GhostStyles styles, float ox, float oy, float oz)
    {
        var columns = RenderLooks.Columns();
        var rows = RenderLooks.Rows;
        var frame = Piece(FrameName);
        var wall = Piece(WallName);
        var cells = new List<(int Column, int Row, Intent Intent)>(columns.Count * rows.Count * 2);
        var drafts = new List<GhostDraft>(cells.Capacity);
        for (var c = 0; c < columns.Count; c++)
            for (var r = 0; r < rows.Count; r++)
            {
                var (x, z) = (ox + c * ColumnPitch + r % 4 * 6f, oz + r / 4 * 6f);
                foreach (var (piece, dx) in new[] { (frame, 0f), (wall, 2f) })
                {
                    if (piece is not { } key)
                        continue;
                    var intent = new Intent.Build(new Placement(key, At(x + dx, oy, z), Turn.None), Option<Finish>.None);
                    cells.Add((c, r, intent));
                    drafts.Add(Draft(intent));
                }

                rig.Name(new Vector3(x + 1f, oy - 1.6f, z - 1.2f), rows[r].Name);
            }

        var outcome = Submit(world, PlanOf("Look grid", drafts));
        line($"scenes: look grid {columns.Count} columns x {rows.Count} rows, {drafts.Count} ghosts ({(frame is null ? "no frame" : FrameName)}, {(wall is null ? "no wall" : WallName)}) at ({ox}, {oy}, {oz}): {outcome}");
        var ids = GhostsOf(world, "Look grid");
        var byColumn = new List<GhostId>[columns.Count];
        var byRow = new List<GhostId>[rows.Count];
        for (var i = 0; i < columns.Count; i++)
            byColumn[i] = new List<GhostId>(rows.Count * 2);
        for (var i = 0; i < rows.Count; i++)
            byRow[i] = new List<GhostId>(columns.Count * 2);
        var found = 0;
        foreach (var (column, row, intent) in cells)
            if (ids.TryGetValue(intent, out var id))
            {
                byColumn[column].Add(id);
                byRow[row].Add(id);
                found++;
            }

        for (var i = 0; i < columns.Count; i++)
        {
            Put(styles, columns[i].Set, byColumn[i]);
            Hold(styles.Add(columns[i].Layer), "layer " + columns[i].Name);
            var centre = new Vector3(ox + i * ColumnPitch + 10f, oy, oz + 12f);
            rig.Name(new Vector3(ox + i * ColumnPitch + 8f, oy + 3f, oz - 3f), columns[i].Name.ToUpperInvariant());
            Views.Add(new View("look-" + columns[i].Name, centre + new Vector3(0f, 13f, -19f), centre, $"column {columns[i].Name}: every library state, row names under the ghosts"));
        }

        for (var i = 0; i < rows.Count; i++)
            Put(styles, RenderLooks.RowSet(rows[i]), byRow[i]);
        line($"scenes: {found} of {cells.Count} grid ghosts found on the board and put in their column and row sets");
        var middle = new Vector3(ox + (columns.Count - 1) * ColumnPitch * 0.5f + 10f, oy, oz + 12f);
        var span = columns.Count * ColumnPitch;
        Views.Insert(0, new View("look-overview", middle + new Vector3(0f, span * 0.45f, -span * 0.6f), middle, "every column side by side: library, then the candidate themes"));
        GridSpan = (ox - 2f, ox + span);
        yield return RenderRun.Frames(10);
    }

    // The overlay proofs and the overlays over hidden and faded originals, all in free air beside the grid: the
    // originals are composite walls spawned built there, since a base's own structures can sit inside the terrain of
    // the world they are loaded into.
    private IEnumerator Proofs(GhostStyles styles, float ox, float oy, float oz)
    {
        var view = new GameWorldView();
        var tops = SpawnOriginals(ox - 26f, oy, oz);
        yield return RenderRun.Frames(10);
        line($"scenes: proof structures: {Describe(tops)}");
        var drafts = new List<GhostDraft>(8);
        var frame = Piece(FrameName) ?? throw new InvalidOperationException("no " + FrameName);
        Intent? overR1 = null;
        foreach (var s in tops.Count > 0 ? tops.GetRange(0, Math.Min(3, tops.Count)) : tops)
            if (GameReads.Info(s).TryGet(out var info))
                drafts.Add(Draft(new Intent.Remove(info.Ref)));
        if (tops.Count > 0 && GameReads.Info(tops[0]).TryGet(out var first))
        {
            var above = new Cell(first.Ref.At.X, first.Ref.At.Y + 20, first.Ref.At.Z);
            if (view.At(above).Count == 0)
            {
                overR1 = new Intent.Build(new Placement(frame, above, Turn.None), Option<Finish>.None);
                drafts.Add(Draft(overR1));
            }
            else
                line($"scenes: the cell over {tops[0].PrefabName} is taken; no Build ghost in front of its Remove overlay");
        }

        // Ghost behind ghost (both see-through looks: the far one must not show it) and ghost behind a built frame
        // spawned here (it must).
        var (gx, gz) = (ox - 16f, oz);
        var wallBuilt = Scene.Spawn(FrameName, new Vector3(gx, oy, gz + 6f), Quaternion.identity, int.MaxValue);
        var near = new Intent.Build(new Placement(frame, At(gx, oy, gz), Turn.None), Option<Finish>.None);
        var far = new Intent.Build(new Placement(frame, At(gx + 4f, oy, gz), Turn.None), Option<Finish>.None);
        var hidden = new Intent.Build(new Placement(frame, At(gx + 4f, oy, gz + 6f), Turn.None), Option<Finish>.None);
        var labelled = new Intent.Build(new Placement(frame, At(gx, oy, gz - 8f), Turn.None), Option<Finish>.None);
        drafts.Add(Draft(near));
        drafts.Add(Draft(far));
        drafts.Add(Draft(hidden));
        drafts.Add(Draft(labelled));
        line($"scenes: overlay proofs plan of {drafts.Count} ghosts: {Submit(world, PlanOf("Overlay proofs", drafts))}; frame spawned in front of the hidden ghost: {wallBuilt != null}");
        var ids = GhostsOf(world, "Overlay proofs");

        var seeSet = Set("live-see-through");
        var labelSet = Set("live-label-inside");
        Put(styles, seeSet, Ids(ids, near, far, hidden));
        Put(styles, labelSet, Ids(ids, labelled));
        var words = Lattice.Api.Label.Create("label inside").Match(static l => l, static r => throw new InvalidOperationException(r.ToString()));
        var range = Lattice.Api.Metres.Create(80f).Match(static m => m, static r => throw new InvalidOperationException(r.ToString()));
        var strength = Fraction.Create(0.6f).Match(static f => f, static r => throw new InvalidOperationException(r.ToString()));
        var layer = new StyleLayerDescriptor(StyleLayerId.Create(LatticeIdentity.Mod, "live-proofs").Match(static l => l, static r => throw new InvalidOperationException(r.ToString())),
            StyleBand.Highlight, 20, ValueList.From(new[]
            {
                new StyleRule(new StyleMatch.InSet(seeSet), new Style { SeeThrough = Option.Some<SeeThroughLook>(new SeeThroughLook.Through(strength, range)) }),
                new StyleRule(new StyleMatch.InSet(labelSet), new Style
                {
                    Label = Option.Some<LabelLook>(new LabelLook.Shown(new LabelText.Literal(words), LibraryText.Default, LabelAnchor.Centre, 100, range)),
                }),
            }));
        Hold(styles.Add(layer), "layer live-proofs");

        var meshes = new PieceMeshes();
        if (tops.Count > 1 && meshes.Of(new PrefabKey(tops[1].PrefabHash)).TryGet(out var hiddenParts))
        {
            var original = new Original(tops[1], hiddenParts.Parts);
            original.Hide();
            _originals.Add(original);
        }

        if (tops.Count > 2 && meshes.Of(new PrefabKey(tops[2].PrefabHash)).TryGet(out var fadedParts) && host.Drawing.Material(MaterialKeys.Faded).TryGet(out var faded))
        {
            _faded = new Original(tops[2], fadedParts.Parts);
            _faded.Hide();
            _originals.Add(_faded);
            _fadedMaterial = new Material(faded) { name = "Live faded copy", color = new Color(0.75f, 0.75f, 0.8f, 0.35f) };
        }

        foreach (var (s, name, says) in Pair(tops,
                     ("proof-remove-behind-build", overR1 is null ? "a Remove overlay (no Build ghost: the cell above is taken)" : "a Remove overlay on a built structure, a Build ghost in front of it"),
                     ("overlay-hidden-original", "a Remove overlay over a structure hidden from the game's drawing"),
                     ("overlay-faded-original", "a Remove overlay over a structure drawn with Lattice's faded material")))
        {
            var p = s.ThingTransformPosition;
            Views.Add(new View(name, p + new Vector3(4.5f, 6f, -5.5f), p, says + $" ({s.PrefabName} {s.ReferenceId})"));
        }

        Views.Add(new View("proof-ghost-behind-ghost", new Vector3(gx - 10f, oy + 1.5f, gz + 3f), new Vector3(gx + 4f, oy, gz + 3f),
            "left: ghost behind a ghost (no see-through on the far one); right: ghost behind a built frame (see-through)"));
        Views.Add(new View("proof-label-inside", new Vector3(gx + 1f, oy + 1f, gz - 13f), new Vector3(gx, oy, gz - 8f), "a label anchored inside its own ghost"));
        Views.Add(new View("proof-outline-over-fill", new Vector3(ox + 6f * 3f - 2f, oy + 2f, oz - 3.5f), new Vector3(ox + 6f * 3f + 1f, oy, oz), "outline over the translucent fill (library column, 'next')"));
        yield return RenderRun.Frames(10);
    }

    /// <summary>Every view in turn, 15 s each, until the stop file appears (the look session).</summary>
    public IEnumerator Tour(string stopFile)
    {
        line($"render-look: touring {Views.Count} views, 15 s each, until {stopFile} exists");
        var i = 0;
        while (!File.Exists(stopFile))
        {
            rig.Show(Views[i % Views.Count]);
            var until = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < until && !File.Exists(stopFile))
                yield return null;
            i++;
        }

        line($"render-look: stop file seen after {i} views");
    }

    public void End()
    {
        rig.EachFrame = null;
        foreach (var held in _held)
            held.Dispose();
        _held.Clear();
        foreach (var original in _originals)
            original.Show();
        _originals.Clear();
        if (_fadedMaterial != null)
            Object.Destroy(_fadedMaterial);
    }

    private void DrawFaded()
    {
        if (_faded is not null && _fadedMaterial != null)
            _faded.DrawFaded(_fadedMaterial, rig.Camera);
    }

    // Three built composite walls 10 m apart along z, each with free air above it for a Build ghost.
    private static List<Structure> SpawnOriginals(float x, float y, float z)
    {
        var spawned = new List<Structure>(3);
        for (var k = 0; k < 3; k++)
            if (Scene.Spawn(WallName, new Vector3(x, y, z + 10f * k), Quaternion.identity, int.MaxValue) is { } wall)
                spawned.Add(wall);
        return spawned;
    }

    private static IEnumerable<(Structure, string, string)> Pair(List<Structure> tops, params (string Name, string Says)[] views)
    {
        for (var i = 0; i < views.Length && i < tops.Count; i++)
            yield return (tops[i], views[i].Name, views[i].Says);
    }

    private static string Describe(List<Structure> structures)
    {
        var parts = new List<string>(structures.Count);
        foreach (var s in structures)
            parts.Add($"{s.PrefabName} {s.ReferenceId} at {s.ThingTransformPosition} ({s.structureRenderMode})");
        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    private void Hold<T>(Result<T, Reason> result, string what) where T : IDisposable
    {
        if (result.TryGet(out var held))
            _held.Add(held);
        else
            line($"FAIL scenes: {what} refused: {result}");
    }

    // Marks: a run of three pipes joined end to end (open only at its two ends) and a lone pipe (open at both); a power
    // control with cables out of its output and into its input (flow arrows); and power controls mounted on the four
    // side faces of a frame ghost, each waiting for it (waits links). All in a set whose look shows ports, open ends, flow and waits.
    private IEnumerator Marks(GhostStyles styles, float x, float y, float z)
    {
        if (Piece(PipeName) is not { } pipe)
        {
            line($"scenes: no {PipeName}; no marks view");
            yield break;
        }

        var start = Cell.FromWorld(new WorldPoint(x, y, z), PieceGrid.Small);
        var intents = new List<Intent>(4);
        var at = start;
        for (var i = 0; i < 3; i++)
        {
            intents.Add(new Intent.Build(new Placement(pipe, at, Turn.None), Option<Finish>.None));
            at = at.Neighbour(Face.PlusZ, PieceGrid.Small);
        }

        intents.Add(new Intent.Build(new Placement(pipe, Cell.FromWorld(new WorldPoint(x + 2f, y, z), PieceGrid.Small), Turn.None), Option<Finish>.None));
        var (fx, fz) = (x, z + 6f);
        if (Piece(ControlName) is { } control && Piece(CableName) is { } cable)
        {
            var alongX = Turn.Of(SignedAxis.ZMinus, SignedAxis.YPlus).Match(static t => t, static r => throw new InvalidOperationException(r.ToString()));
            var device = Cell.FromWorld(new WorldPoint(fx, y, fz), PieceGrid.Small);
            intents.Add(new Intent.Build(new Placement(control, device, Turn.None), Option<Finish>.None));
            var run = device;
            for (var i = 0; i < 2; i++)
            {
                run = run.Neighbour(Face.PlusX, PieceGrid.Small);
                intents.Add(new Intent.Build(new Placement(cable, run, alongX), Option<Finish>.None));
            }

            intents.Add(new Intent.Build(new Placement(cable, device.Neighbour(Face.MinusX, PieceGrid.Small), alongX), Option<Finish>.None));
        }
        else
            line($"scenes: no {ControlName} or {CableName}; no flow arrows in the marks view");

        var (wx, wz) = (Large(x + 6f), Large(z));
        var floor = Large(y);
        var frame = Piece(FrameName);
        var built = Scene.Spawn(FrameName, new Vector3(wx, floor, wz), Quaternion.identity, int.MaxValue);
        // A frame ghost on the built frame, and power control ghosts mounted on two of its faces, backs to the frame:
        // a wall-mounted piece is held by what fills the large cell behind it (SmallGrid.CanMountOnWall,
        // Assets.Scripts.Objects/SmallGrid.cs), so each waits for the frame ghost.
        var waiting = new List<Intent>(3);
        if (frame is { } f)
        {
            var held = new Intent.Build(new Placement(f, At(wx, floor + 2f, wz), Turn.None), Option<Finish>.None);
            intents.Add(held);
            waiting.Add(held);
            // One power control on each side face of the frame ghost: on the face plane (1 m from the large cell's
            // centre, where the large faces lie) and turned so its back, local -Z, points into the frame (its +Z along
            // the face's outward normal). Turn.Of sends +X and +Y; +Z is their cross product.
            if (Piece(ControlName) is { } mounted)
                foreach (var (dx, dz, sendX) in new[]
                         {
                             (1f, 0f, SignedAxis.ZMinus), (-1f, 0f, SignedAxis.ZPlus),
                             (0f, 1f, SignedAxis.XPlus), (0f, -1f, SignedAxis.XMinus),
                         })
                {
                    var turn = Turn.Of(sendX, SignedAxis.YPlus).Match(static t => t, static r => throw new InvalidOperationException(r.ToString()));
                    var on = new Intent.Build(new Placement(mounted, Cell.FromWorld(new WorldPoint(wx + dx, floor + 2f, wz + dz), PieceGrid.Small), turn), Option<Finish>.None);
                    intents.Add(on);
                    waiting.Add(on);
                }
        }

        line($"scenes: built frame for the waits link spawned: {built != null}");
        yield return RenderRun.Frames(10);
        var drafts = new List<GhostDraft>(intents.Count);
        foreach (var intent in intents)
            drafts.Add(Draft(intent));
        line($"scenes: marks plan of {drafts.Count} ghosts: {Submit(world, PlanOf("Port marks", drafts))}");
        var ids = GhostsOf(world, "Port marks");
        // The waits scene's ghosts take a look that shows only waits links, so the links stand clear of port, open-end
        // and flow marks; every other marks ghost shows those three.
        var set = Set("live-marks");
        var waitsSet = Set("live-waits");
        var others = new List<Intent>(intents.Count);
        foreach (var intent in intents)
            if (!waiting.Contains(intent))
                others.Add(intent);
        Put(styles, set, Ids(ids, others.ToArray()));
        Put(styles, waitsSet, Ids(ids, waiting.ToArray()));
        var layer = new StyleLayerDescriptor(StyleLayerId.Create(LatticeIdentity.Mod, "live-marks").Match(static l => l, static r => throw new InvalidOperationException(r.ToString())),
            StyleBand.Highlight, 40, ValueList.From(new[]
            {
                new StyleRule(new StyleMatch.InSet(set), new Style
                {
                    Ports = Option.Some(ShowMode.Shown),
                    OpenEnds = Option.Some(ShowMode.Shown),
                    FlowArrows = Option.Some(ShowMode.Shown),
                }),
                new StyleRule(new StyleMatch.InSet(waitsSet), new Style { WaitsLinks = Option.Some(ShowMode.Shown) }),
            }));
        Hold(styles.Add(layer), "layer live-marks");
        Views.Add(new View("marks-ports-and-open-ends", new Vector3(x + 2.2f, y + 1.6f, z - 1.8f), new Vector3(x + 0.8f, y, z + 0.6f),
            "port marks on every pipe end; open-end marks only where nothing joins (the run's two ends, both ends of the lone pipe)"));
        Views.Add(new View("marks-flow", new Vector3(fx + 0.5f, y + 1.4f, fz - 2.2f), new Vector3(fx + 0.3f, y, fz),
            "flow arrows: into the power control at its input, out at its output, and along the cables away from the output"));
        Views.Add(new View("marks-waits", new Vector3(wx + 4f, floor + 4.5f, wz - 4.5f), new Vector3(wx, floor + 2f, wz),
            "waits links: each power control mounted on the frame ghost, to the frame ghost it waits for"));
        yield return new WaitForSecondsRealtime(3f);
        if (waiting.Count > 0)
        {
            var stacked = Ids(ids, waiting.ToArray());
            for (var tries = 0; tries < 20; tries++)
            {
                var checkedNow = world.Validation.Check(new Selector.Ids(ValueList.From(stacked)));
                if (checkedNow.TryGet(out var answer) && answer is Answer<ValidationReport>.Ready ready)
                {
                    line($"scenes: waits scene findings (frame ghost, then the mounted power controls): {string.Join("; ", Findings(ready.Value))}");
                    break;
                }

                yield return new WaitForSecondsRealtime(1f);
            }
        }

        yield return RenderRun.Frames(10);
    }

    // X-ray: three built frames side by side, each with a frame ghost hidden behind it, seen through at strength 0.6,
    // at 1, and at 1 with its own opacity 0.7; beside each hidden ghost a visible one with the same look, to compare
    // the hidden part with the visible fill.
    private IEnumerator XRay(GhostStyles styles, float x, float y, float z)
    {
        var frame = Piece(FrameName) ?? throw new InvalidOperationException("no " + FrameName);
        var range = Lattice.Api.Metres.Create(80f).Match(static m => m, static r => throw new InvalidOperationException(r.ToString()));
        var looks = new (string Name, SeeThroughLook.Through Look)[]
        {
            ("strength 0.6", new SeeThroughLook.Through(Fraction.Create(0.6f).Match(static f => f, static r => throw new InvalidOperationException(r.ToString())), range)),
            ("strength 1", new SeeThroughLook.Through(Fraction.One, range)),
            ("strength 1, opacity 0.7", new SeeThroughLook.Through(Fraction.One, range)
            {
                Opacity = Option.Some(Fraction.Create(0.7f).Match(static f => f, static r => throw new InvalidOperationException(r.ToString()))),
            }),
        };

        var drafts = new List<GhostDraft>(looks.Length * 2);
        var pairs = new List<(Intent Hidden, Intent Visible)>(looks.Length);
        var walls = 0;
        for (var k = 0; k < looks.Length; k++)
        {
            var cx = x + 8f * k;
            if (Scene.Spawn(FrameName, new Vector3(cx, y, z), Quaternion.identity, int.MaxValue) != null)
                walls++;
            var hidden = new Intent.Build(new Placement(frame, At(cx, y, z + 2f), Turn.None), Option<Finish>.None);
            var visible = new Intent.Build(new Placement(frame, At(cx + 2f, y, z + 2f), Turn.None), Option<Finish>.None);
            drafts.Add(Draft(hidden));
            drafts.Add(Draft(visible));
            pairs.Add((hidden, visible));
            rig.Name(new Vector3(cx + 1f, y + 2.5f, z), looks[k].Name.ToUpperInvariant());
        }

        line($"scenes: x-ray plan of {drafts.Count} ghosts: {Submit(world, PlanOf("X-ray", drafts))}; {walls} of {looks.Length} frames spawned in front");
        var ids = GhostsOf(world, "X-ray");
        var rules = new StyleRule[looks.Length];
        for (var k = 0; k < looks.Length; k++)
        {
            var set = Set("live-xray-" + k);
            Put(styles, set, Ids(ids, pairs[k].Hidden, pairs[k].Visible));
            rules[k] = new StyleRule(new StyleMatch.InSet(set), new Style { SeeThrough = Option.Some<SeeThroughLook>(looks[k].Look) });
        }

        Hold(styles.Add(new StyleLayerDescriptor(StyleLayerId.Create(LatticeIdentity.Mod, "live-xray").Match(static l => l, static r => throw new InvalidOperationException(r.ToString())),
            StyleBand.Highlight, 21, ValueList.From(rules))), "layer live-xray");
        Views.Add(new View("xray-strength", new Vector3(x + 9f, y + 2.5f, z - 15f), new Vector3(x + 9f, y + 0.5f, z + 2f),
            "x-ray through a built frame: strength 0.6, strength 1, strength 1 with opacity 0.7; each beside a visible ghost with the same look"));
        yield return RenderRun.Frames(10);
    }

    private void Put(GhostStyles styles, StyleSetId id, List<GhostId> ghosts)
    {
        if (styles.Set(id).TryGet(out var set))
            set.Add(ValueList.From(ghosts));
        else
            line($"FAIL scenes: set {id} refused");
    }

    private static List<GhostId> Ids(Dictionary<Intent, GhostId> ids, params Intent[] intents)
    {
        var found = new List<GhostId>(intents.Length);
        foreach (var intent in intents)
            if (ids.TryGetValue(intent, out var id))
                found.Add(id);
        return found;
    }

    private static StyleSetId Set(string name) =>
        StyleSetId.Create(LatticeIdentity.Mod, name).Match(static s => s, static r => throw new InvalidOperationException(r.ToString()));

    private static PrefabKey? Piece(string name) =>
        Prefab.Find<Structure>(name) is { } prefab && prefab != null ? new PrefabKey(prefab.PrefabHash) : null;

    /// <summary>A large cell's centre: odd metres on the large grid.</summary>
    private static float Large(float metres) => (float)Math.Floor(metres / 2f) * 2f + 1f;

    private static Cell At(float x, float y, float z) =>
        new((int)Math.Round(x * 10f), (int)Math.Round(y * 10f), (int)Math.Round(z * 10f));

    public static GhostDraft Draft(Intent intent) => new(
        intent, Option<PlanId>.None, LiveMaker, new Lattice.Api.Visibility.Everyone(),
        Option<Lattice.Api.Label>.None, Option<Tint>.None, ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty);

    public static Command PlanOf(string name, List<GhostDraft> drafts) =>
        new Command.CreatePlan(new PlanDraft(
            PlanName.Create(name).Match(static n => n, static r => throw new InvalidOperationException(r.ToString())),
            LiveMaker, new Lattice.Api.Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty))
        {
            Ghosts = ValueList.From(drafts),
        };

    public static Outcome Submit(LatticeWorld world, Command command) =>
        world.Commands.Submit(new CommandId(Guid.NewGuid()), command, LatticeIdentity.Actor) switch
        {
            Submission.Answered answered => answered.Outcome,
            var other => throw new InvalidOperationException("not answered at once: " + other),
        };

    /// <summary>The ghosts of the plan with this name, by intent.</summary>
    public static Dictionary<Intent, GhostId> GhostsOf(LatticeWorld world, string plan)
    {
        var ids = new Dictionary<Intent, GhostId>(256);
        foreach (var entry in world.Ghosts.Index.Plans)
        {
            if (entry.Value.Plan.Name.Value != plan)
                continue;
            foreach (var id in entry.Value.Ghosts)
                if (world.Ghosts.Index.Ghosts.TryGetValue(id, out var ghost))
                    ids[ghost.Ghost.Intent] = id;
        }

        return ids;
    }
}
