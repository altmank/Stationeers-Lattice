using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Serialization;
using Assets.Scripts.Util;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Lattice.Api;
using Lattice.Game.Catalogue;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using UnityEngine;

namespace Lattice.Game.Live;

/// <summary>
/// The game's key and cursor reads, answered by the run (test instance only). Cites are the game's decompile,
/// <c>Assets.Scripts.Inventory/InventoryManager.cs</c> unless named. While a piece is the construction cursor,
/// <c>PlacementMode</c> (:2493) reads the rotate keys with <c>KeyManager.GetButtonUp</c> (<c>KeyManager.cs:823</c>): in a
/// cell (<c>PlacementSnap.Grid</c>, :2522) Y at :2528-2539, X at :2541-2552, Z at :2554-2565; on a cell face
/// (<c>PlacementSnap.Face</c>, :2695) :2706-2790; the smart rotate is <c>SmartRotate.GetNext</c> when the quantity modifier is
/// held, :2575-2591 and :2793-2810. The construct press is <c>KeyManager.GetMouseDown("Primary")</c> after the
/// <c>CanConstruct</c> gate (:2838, :2880), which is <c>GetButtonDown(KeyMap.PrimaryAction)</c> (<c>KeyManager.cs:851</c>);
/// it ends in <c>UsePrimaryComplete</c> (:3090), and a build with a placement time waits holding
/// <c>GetMouse("Primary")</c> (<c>GetButton</c>, <c>KeyManager.cs:869</c>; :3317). The cell comes from
/// <c>InputHelpers.GetCameraForwardGrid</c> (<c>Assets.Scripts.Util/InputHelpers.cs:65</c>, called at :2508). Each answer lasts
/// the frames the run sets.
/// </summary>
internal static class BuilderKeys
{
    private static bool _installed;

    public static KeyCode? UpKey { get; set; }

    public static bool PrimaryDown { get; set; }

    public static bool PrimaryHeld { get; set; }

    public static bool SmartHeld { get; set; }

    public static Vector3? AimAt { get; set; }

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        var harmony = new Harmony("lattice.live.builder-keys");
        harmony.Patch(AccessTools.Method(typeof(KeyManager), nameof(KeyManager.GetButtonUp), [typeof(KeyCode)]),
            prefix: new HarmonyMethod(typeof(BuilderKeys), nameof(ButtonUp)));
        harmony.Patch(AccessTools.Method(typeof(KeyManager), nameof(KeyManager.GetButtonDown), [typeof(KeyCode)]),
            prefix: new HarmonyMethod(typeof(BuilderKeys), nameof(ButtonDown)));
        harmony.Patch(AccessTools.Method(typeof(KeyManager), nameof(KeyManager.GetButton), [typeof(KeyCode)]),
            prefix: new HarmonyMethod(typeof(BuilderKeys), nameof(Button)));
        harmony.Patch(AccessTools.Method(typeof(InputHelpers), nameof(InputHelpers.GetCameraForwardGrid)),
            prefix: new HarmonyMethod(typeof(BuilderKeys), nameof(Aim)));
    }

    private static bool ButtonUp(KeyCode key, ref bool __result)
    {
        if (UpKey is not { } up || key != up)
            return true;
        __result = true;
        return false;
    }

    private static bool ButtonDown(KeyCode key, ref bool __result)
    {
        if (!PrimaryDown || key != KeyMap.PrimaryAction)
            return true;
        __result = true;
        return false;
    }

    private static bool Button(KeyCode key, ref bool __result)
    {
        if ((!PrimaryHeld || key != KeyMap.PrimaryAction) && (!SmartHeld || key != KeyMap.QuantityModifier))
            return true;
        __result = true;
        return false;
    }

    private static bool Aim(ref Vector3 __result, out Vector3 normal)
    {
        normal = Vector3.zero;
        if (AimAt is not { } at)
            return true;
        __result = at;
        return false;
    }
}

/// <summary>
/// <c>builder-turns</c>, a new world: for each structure piece whose catalogue turns are not all 24 and that the builder
/// places in a cell, its build kit goes in the active hand, the game's placement code picks the cursor, and the run steps
/// the game's own rotate keys until no new turn appears (<see cref="BuilderTurnsFacts.Reach"/>). Each turn reached is
/// then built through the game's construct press, one cell of the piece's column each. The world is saved as
/// <c>lattice-builder-turns</c>. Pieces another placement kind covers, and pieces with no kit or cursor, are skipped and logged.
/// </summary>
internal static class BuilderTurnsRun
{
    public const string Station = "lattice-builder-turns";
    private const float Cell = 2f;
    private const int PerRow = 12;
    private const float Spacing = 4f;
    private const float Lift = 6f;
    private const float PlaceSeconds = 10f;

    private static Vector3? _stand;
    private static Action<string>? Log;

    public static IEnumerator Run(string outDir, Action<string> line)
    {
        void Say(string text) => line("builder-turns: " + text);
        var failed = false;
        Log = Say;

        if (GameManager.GameState != GameState.Running)
        {
            yield return NewWorld(Say, () => failed = true);
            if (failed)
                yield break;
        }

        yield return WaitLanded(Say, () => failed = true);
        if (failed)
            yield break;
        var body = InventoryManager.Parent;
        if (body == null)
        {
            line("FAIL builder-turns: no body");
            yield break;
        }

        BuilderKeys.Install();
        _stand = body.ThingTransformPosition;
        yield return Hold(60);

        var source = new GamePrefabSource();
        var catalogue = new PrefabCatalogue(source);
        var plan = new List<(PieceFacts Piece, Structure Prefab, Item Kit)>();
        var skipped = 0;
        foreach (var key in source.Structures)
        {
            if (!catalogue.Piece(key).TryGet(out var piece) || piece.AllowedTurns.IsAll)
                continue;
            var prefab = Prefab.Find<Structure>(key.Hash);
            if (prefab == null)
                continue;
            if (prefab.PlacementType == PlacementSnap.FaceMount)
            {
                skipped++;
                Say($"{prefab.name} skipped: mounted on another structure's face");
                continue;
            }

            var kit = piece.Kits.Count > 0 ? Prefab.Find<Item>(piece.Kits[0].Kit.Hash) : null;
            if (kit == null)
            {
                skipped++;
                Say($"{prefab.name} skipped: no kit");
                continue;
            }

            plan.Add((piece, prefab, kit));
        }

        Say($"{plan.Count} pieces to try, {skipped} skipped");
        if (plan.Count == 0)
        {
            line("FAIL builder-turns: no piece to try");
            yield break;
        }

        var floorY = Snap(_stand.Value.y) + Lift;
        var originX = Snap(_stand.Value.x) - (PerRow - 1) * Spacing / 2f;
        var originZ = Snap(_stand.Value.z) + 8f;
        var frame = Prefab.Find<Structure>("StructureFrame");
        var floor = 0;
        for (var i = 0; i < plan.Count && frame != null; i++)
            if (Scene.Spawn("StructureFrame", ColumnBase(i, originX, originZ, floorY), Quaternion.identity, int.MaxValue) != null)
                floor++;
        Say($"floor {floor} frames at y {floorY.ToString("0.##", CultureInfo.InvariantCulture)}");
        yield return Hold(30);

        int tried = 0, placed = 0, refused = 0, outside = 0, noCursor = 0, done = 0;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < plan.Count; i++)
        {
            var (piece, prefab, kit) = plan[i];
            var column = ColumnBase(i, originX, originZ, floorY);
            var session = new Session(prefab, kit);
            yield return session.Enter();
            if (!session.HasCursor)
            {
                noCursor++;
                Say($"{prefab.name} skipped: no cursor");
                continue;
            }

            var cursor = InventoryManager.ConstructionCursor;
            var moves = MovesOf(cursor);
            var turns = new Dictionary<int, Quaternion>();
            var start = TurnIndex(cursor.ThingTransform.rotation);
            turns[start] = cursor.ThingTransform.rotation;
            var reached = new List<int> { start };
            var cell = column + Vector3.up * Cell;
            var pressed = new List<(int From, TurnMove Move, int To)>();
            for (var at = 0; at < reached.Count; at++)
                foreach (var move in moves)
                {
                    var from = reached[at];
                    yield return session.Press(turns[from], move, cell);
                    var to = TurnIndex(InventoryManager.ConstructionCursor.ThingTransform.rotation);
                    pressed.Add((from, move, to));
                    if (to >= 0 && !turns.ContainsKey(to))
                    {
                        turns[to] = InventoryManager.ConstructionCursor.ThingTransform.rotation;
                        reached.Add(to);
                    }
                }

            Say($"{prefab.name} reaches {reached.Count} turns from {start} with {string.Join(" ", moves)}: {string.Join(" ", reached)}");
            var row = 0;
            foreach (var turn in reached)
            {
                row++;
                var at = column + Vector3.up * (Cell * row);
                tried++;
                var attempt = new Attempt();
                yield return session.Place(turns[turn], at, attempt);
                if (attempt.Placed is { } structure)
                {
                    placed++;
                    var seen = TurnIndex(structure.ThingTransform.rotation);
                    var allowed = piece.AllowedTurns.Contains(Turn.Create(seen < 0 ? 0 : seen).Match(static t => t, static r => throw new InvalidOperationException(r.ToString())));
                    if (!allowed)
                        outside++;
                    Say(BuilderTurnsFacts.PlacementLine(prefab.name, seen, true, $"at {WorldFile.Metres(structure.ThingTransformPosition.x, structure.ThingTransformPosition.y, structure.ThingTransformPosition.z)} cursor turn {turn} allowed {(allowed ? "yes" : "no")}"));
                }
                else
                {
                    refused++;
                    Say(BuilderTurnsFacts.PlacementLine(prefab.name, turn, false, attempt.Why));
                }
            }

            yield return session.Leave();
            done++;
            if (done % 10 == 0)
                Say($"piece {done} of {plan.Count}: {placed} placed, {refused} refused in {watch.Elapsed.TotalSeconds:F0} s");
        }

        Say($"placed {placed}, refused {refused}, outside the catalogue's turns {outside}, of {tried} turns tried, pieces {plan.Count}, no cursor {noCursor}, in {watch.Elapsed.TotalSeconds:F1} s");
        if (placed == 0)
        {
            line("FAIL builder-turns: nothing was placed");
            yield break;
        }

        yield return Hold(180);
        var landing = ReadLanding();
        if (!landing.Landed)
        {
            line($"FAIL builder-turns: the landing is not over at the save: {landing.Pending}");
            yield break;
        }

        SaveResult? saved = null;
        yield return SaveHelper.NewSave(Station, CancellationToken.None).ToCoroutine(r => saved = r);
        Say($"save {Station}: {(saved is { } result ? $"success {result.Success} '{result.Message}'" : "no result")}");
        var facts = new List<KeyValuePair<string, string?>>
        {
            new("station", Station),
            new("world", WorldSetting.Current?.Id),
            new("difficulty", DifficultySetting.Current?.Id),
            new("start", WorldSetting.Current?.StartConditionData?.Id),
            new("pieces", plan.Count.ToString(CultureInfo.InvariantCulture)),
            new("pieces-skipped", skipped.ToString(CultureInfo.InvariantCulture)),
            new("pieces-no-cursor", noCursor.ToString(CultureInfo.InvariantCulture)),
            new("turns-tried", tried.ToString(CultureInfo.InvariantCulture)),
            new("turns-placed", placed.ToString(CultureInfo.InvariantCulture)),
            new("turns-refused", refused.ToString(CultureInfo.InvariantCulture)),
            new("turns-outside", outside.ToString(CultureInfo.InvariantCulture)),
            new("floor", floor.ToString(CultureInfo.InvariantCulture)),
            new("structures", Scene.AllStructures().Count.ToString(CultureInfo.InvariantCulture)),
            new("landed", landing.Landed ? "yes" : "no"),
            new("save-success", saved?.Success ?? false ? "True" : "False"),
            new("lattice-version", typeof(Turn).Assembly.GetName().Version.ToString()),
        };
        File.WriteAllText(Path.Combine(outDir, "world.txt"), WorldFile.Text(facts));
    }

    private static Vector3 ColumnBase(int index, float originX, float originZ, float floorY) =>
        new(originX + index % PerRow * Spacing, floorY, originZ + index / PerRow * Spacing);

    private static float Snap(float metres) => Mathf.Floor(metres / Cell) * Cell + Cell / 2f;

    private static List<TurnMove> MovesOf(Structure cursor)
    {
        var moves = new List<TurnMove>();
        if ((cursor.RotationAxis & RotationAxis.Y) != RotationAxis.None)
            moves.AddRange([TurnMove.Left, TurnMove.Right]);
        if ((cursor.RotationAxis & RotationAxis.X) != RotationAxis.None)
            moves.AddRange([TurnMove.Up, TurnMove.Down]);
        if ((cursor.RotationAxis & RotationAxis.Z) != RotationAxis.None)
            moves.AddRange([TurnMove.RollLeft, TurnMove.RollRight]);
        if (cursor is ISmartRotatable)
            moves.Add(TurnMove.Smart);
        return moves;
    }

    private static int TurnIndex(Quaternion rotation)
    {
        for (var i = 0; i < 24; i++)
            if (Turn.Create(i).Match(static t => t, static r => throw new InvalidOperationException(r.ToString())).ToRotation() is var q
                && Mathf.Abs(Quaternion.Dot(q, rotation)) > 0.999f)
                return i;
        return -1;
    }

    private sealed class Attempt
    {
        public Structure? Placed { get; set; }

        public string Why { get; set; } = "";
    }

    // The turn as the builder keeps it: the cursor's rotation and, for a piece set on a cell face, the face and rotation it
    // re-reads each frame (InventoryManager.CurrentFace, CurrentRotation).
    private static void SetTurn(Quaternion turn)
    {
        var cursor = InventoryManager.ConstructionCursor;
        cursor.ThingTransform.rotation = turn;
        InventoryManager.CurrentRotation = turn;
        InventoryManager.CurrentFace = RocketGrid.FaceInt.FaceIntFromDir(RocketGrid.GetForwardDir(cursor.ThingTransform.forward));
    }

    private static IEnumerator Hold(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            if (_stand is { } at && InventoryManager.Parent is { } body && Vector3.Distance(body.ThingTransformPosition, at) > 0.5f)
                InventoryManager.ParentHuman?.ForceSetPosition(at);
            yield return null;
        }
    }

    /// <summary>One piece held as the builder's cursor: the kit in the active hand and the game's placement mode.</summary>
    private sealed class Session(Structure prefab, Item kit)
    {
        private readonly HashSet<long> _claimed = new();

        private static readonly MethodInfo EnterSingle = AccessTools.Method(typeof(InventoryManager), "SetConstructorItemPlacement");
        private static readonly MethodInfo EnterMulti = AccessTools.Method(typeof(InventoryManager), "SetMultiConstructorItemPlacement");

        public bool HasCursor => InventoryManager.CurrentMode == InventoryManager.Mode.Placement
            && InventoryManager.ConstructionCursor != null && InventoryManager.ConstructionCursor == InventoryManager.GetStructureCursor(prefab.name);

        // The cursor comes from the kit in the active hand: the single kit's placement (:2399, entered from :2279 and :2290)
        // or the multi kit's (:2391, from :2274 and :2285), each ending in UpdatePlacement, whose cursor pick is
        // _constructionCursors.TryGetValue at :1727 and :1776 and whose face re-orientation for the piece's AllowedRotations is
        // :1789-1810.
        public IEnumerator Enter()
        {
            Clear();
            yield return null;
            yield return null;
            var hand = InventoryManager.ActiveHandSlot;
            var held = OnServer.Create<Item>(kit, hand);
            if (held is Stackable stack)
                stack.Quantity = stack.MaxQuantity;
            yield return null;
            if (held is MultiConstructor multi)
            {
                var index = multi.Constructables.FindIndex(c => c != null && c.PrefabHash == prefab.PrefabHash);
                if (index < 0)
                    yield break;
                multi.LastSelectedIndex = index;
                EnterMulti.Invoke(InventoryManager.Instance, [multi]);
                if (!InventoryManager.Instance.ConstructionPanel.IsVisible)
                    InventoryManager.Instance.ConstructionPanel.SetVisible(true);
            }
            else if (held is Constructor single)
                EnterSingle.Invoke(null, [single]);
            yield return null;
            yield return null;
            if (!HasCursor)
                Log?.Invoke($"{prefab.name} no cursor: kit {held?.GetType().Name ?? "not created"} in hand {InventoryManager.ActiveHandSlot.Get<Item>()?.name ?? "none"}, mode {InventoryManager.CurrentMode}, cursor {(InventoryManager.ConstructionCursor != null ? InventoryManager.ConstructionCursor.name : "none")}, game's cursor for the piece {(InventoryManager.GetStructureCursor(prefab.name) != null ? "exists" : "missing")}, panel {InventoryManager.Instance.ConstructionPanel.IsVisible}");
        }

        public IEnumerator Leave()
        {
            BuilderKeys.AimAt = null;
            Clear();
            yield return null;
        }

        private static void Clear()
        {
            InventoryManager.Instance.CancelPlacement();
            if (InventoryManager.ActiveHandSlot.Get<Item>() is { } occupant)
                OnServer.Destroy(occupant);
            BuilderKeys.PrimaryHeld = false;
            BuilderKeys.PrimaryDown = false;
            BuilderKeys.SmartHeld = false;
            BuilderKeys.UpKey = null;
        }

        /// <summary>Sets the cursor's turn, then one frame of the move's key.</summary>
        public IEnumerator Press(Quaternion from, TurnMove move, Vector3 cell)
        {
            if (InventoryManager.ConstructionCursor == null)
                yield break;
            SetTurn(from);
            BuilderKeys.AimAt = cell;
            yield return null;
            if (move == TurnMove.Smart)
            {
                BuilderKeys.SmartHeld = true;
                yield return null;
                BuilderKeys.SmartHeld = false;
                yield return null;
                yield break;
            }

            BuilderKeys.UpKey = move switch
            {
                TurnMove.Left => KeyMap.RotateLeft,
                TurnMove.Right => KeyMap.RotateRight,
                TurnMove.Up => KeyMap.RotateUp,
                TurnMove.Down => KeyMap.RotateDown,
                TurnMove.RollLeft => KeyMap.RotateRollLeft,
                _ => KeyMap.RotateRollRight,
            };
            yield return null;
            BuilderKeys.UpKey = null;
            yield return null;
        }

        /// <summary>The construct press at the cell with the cursor in the turn, held until the structure stands or time runs out.</summary>
        public IEnumerator Place(Quaternion turn, Vector3 cell, Attempt attempt)
        {
            if (!HasCursor || !InventoryManager.ActiveHandSlot.Contains<Item>())
                yield return Enter();
            if (!HasCursor)
            {
                attempt.Why = "no cursor";
                yield break;
            }

            var cursor = InventoryManager.ConstructionCursor;
            SetTurn(turn);
            BuilderKeys.AimAt = cell;
            yield return null;
            yield return null;
            var info = cursor.CanConstruct();
            if (!info.CanConstruct)
            {
                attempt.Why = "the builder's gate: " + info.ErrorMessage;
                yield break;
            }

            BuilderKeys.PrimaryHeld = true;
            BuilderKeys.PrimaryDown = true;
            yield return null;
            BuilderKeys.PrimaryDown = false;
            var until = Time.realtimeSinceStartup + PlaceSeconds;
            while (Time.realtimeSinceStartup < until && attempt.Placed == null)
            {
                attempt.Placed = StandingAt(cell);
                if (attempt.Placed == null)
                    yield return null;
            }

            BuilderKeys.PrimaryHeld = false;
            yield return null;
            attempt.Placed ??= StandingAt(cell);
            if (attempt.Placed == null)
                attempt.Why = "the press built nothing";
        }

        private Structure? StandingAt(Vector3 cell) =>
            Scene.AllStructures()
                .Where(s => s.PrefabHash == prefab.PrefabHash && !_claimed.Contains(s.ReferenceId) && Vector3.Distance(s.ThingTransformPosition, cell) < Cell)
                .OrderBy(s => Vector3.Distance(s.ThingTransformPosition, cell))
                .FirstOrDefault() is { } found && _claimed.Add(found.ReferenceId)
                ? found
                : null;
    }

    private static Landing ReadLanding()
    {
        int descending = 0, venting = 0;
        foreach (var capsule in UnityEngine.Object.FindObjectsOfType<LanderCapsule>())
        {
            if (capsule.LanderMode == LanderMode.Descending)
                descending++;
            else if (capsule.LanderMode == LanderMode.Venting)
                venting++;
        }

        return new Landing(descending, venting, InventoryManager.ParentHuman?.ParentSlot?.Parent is LanderCapsule);
    }

    private static IEnumerator WaitLanded(Action<string> say, Action fail)
    {
        var watch = Stopwatch.StartNew();
        var progress = new WaitProgress();
        var landing = ReadLanding();
        while (!landing.Landed && watch.Elapsed.TotalSeconds < 180)
        {
            if (progress.Due(watch.Elapsed.TotalSeconds))
                say($"waiting for the landing: {watch.Elapsed.TotalSeconds:F0} s of 180: {landing.Pending}");
            yield return null;
            landing = ReadLanding();
        }

        if (!landing.Landed)
        {
            say($"FAIL the landing did not finish in {watch.Elapsed.TotalSeconds:F0} s: {landing.Pending}");
            fail();
            yield break;
        }

        say($"landed (after {watch.Elapsed.TotalSeconds:F1} s)");
    }

    private static IEnumerator MainMenu(Action<string> say, Action fail)
    {
        var watch = Stopwatch.StartNew();
        var progress = new WaitProgress();
        var menuBy = Time.realtimeSinceStartup + 300f;
        while ((Assets.Scripts.UI.MainMenu.Instance == null || !Assets.Scripts.UI.MainMenu.Instance.IsVisible || XmlSaveLoad.Instance == null)
               && Time.realtimeSinceStartup < menuBy)
        {
            if (progress.Due(watch.Elapsed.TotalSeconds))
                say($"waiting for the main menu: {watch.Elapsed.TotalSeconds:F0} s of 300");
            yield return null;
        }

        if (Assets.Scripts.UI.MainMenu.Instance == null || XmlSaveLoad.Instance == null)
        {
            say($"FAIL the main menu did not come up in {watch.Elapsed.TotalSeconds:F0} s");
            fail();
            yield break;
        }

        yield return new WaitForSecondsRealtime(2f);
        say($"main menu up after {watch.Elapsed.TotalSeconds:F1} s");
    }

    // A new Lunar Creative world with the first default start, as the game's own newgame command makes one.
    private static IEnumerator NewWorld(Action<string> say, Action fail)
    {
        var menuFailed = false;
        yield return MainMenu(say, () => menuFailed = true);
        if (menuFailed)
        {
            fail();
            yield break;
        }

        var world = WorldSetting.Find("Lunar");
        var difficulty = DifficultySetting.Find("Creative");
        StartConditionData? start = null;
        if (world != null)
            foreach (var condition in world.Data.StartConditionDatas)
                if (condition.IsDefault)
                {
                    start = condition;
                    break;
                }

        if (world == null || difficulty == null || start == null)
        {
            say($"FAIL the game has no world Lunar ({world != null}), difficulty Creative ({difficulty != null}) or default start ({start != null})");
            fail();
            yield break;
        }

        var watch = Stopwatch.StartNew();
        var progress = new WaitProgress();
        var starts = LatticeTiming.Starts;
        WorldSetting.SetCurrent(world, start);
        DifficultySetting.SetCurrent(difficulty);
        Assets.Scripts.UI.ImGuiLoadingScreen.WorldName = world.Id;
        Assets.Scripts.Objects.World.StartNewWorld(world.Id).Forget();
        var deadline = Time.realtimeSinceStartup + 600f;
        while ((GameManager.GameState != GameState.Running || InventoryManager.Parent == null || LatticeTiming.Starts == starts)
               && Time.realtimeSinceStartup < deadline)
        {
            if (progress.Due(watch.Elapsed.TotalSeconds))
                say($"waiting for the new world: {watch.Elapsed.TotalSeconds:F0} s of 600 (state {GameManager.GameState}, body {InventoryManager.Parent != null})");
            yield return null;
        }

        if (GameManager.GameState != GameState.Running || InventoryManager.Parent == null || LatticeTiming.Starts == starts)
        {
            say($"FAIL the new world did not start (state {GameManager.GameState}, body {InventoryManager.Parent != null})");
            fail();
            yield break;
        }

        say($"new world {world.Id} {difficulty.Id} {start.Id}: running after {watch.Elapsed.TotalSeconds:F1} s");
    }
}
