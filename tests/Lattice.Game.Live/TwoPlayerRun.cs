using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.EntryPoint;
using Lattice.Core.Sync;
using Lattice.Game.Sync;
using Networking.Servers;
using UnityEngine;
using Cell = Lattice.Api.Cell;

namespace Lattice.Game.Live;

/// <summary>
/// The client's player cookie kept off the machine's own profile (test instance only). Without Steam the client's id
/// comes from <c>NetworkManager.GenerateUniqueClientId</c> (<c>Networking.Servers/PlayerCookie.cs</c>, CreateNewCookie);
/// a batch-mode game loads no cookie at all (<c>Assets.Scripts.Networking/NetworkManager.cs</c>, Init), so the run
/// builds one in memory. Its file paths point into the test folder and saving it writes nothing.
/// </summary>
internal static class ClientCookie
{
    private static string _folder = "";

    public static int SavesSkipped { get; private set; }

    /// <summary>The environment variable naming the test instance's root folder; without it the client run refuses to start.</summary>
    public const string RootVariable = "LATTICE_TEST_ROOT";

    public static Result<string, string> Install(string folder)
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrWhiteSpace(root))
            return Result<string, string>.Error($"{RootVariable} is not set: the client data folder must lie inside the test instance's root");
        var inside = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(folder).StartsWith(inside, StringComparison.OrdinalIgnoreCase))
            return Result<string, string>.Error($"client data folder '{folder}' is outside {RootVariable} ({inside})");
        _folder = folder;
        var harmony = new Harmony("lattice.live.client-cookie");
        harmony.Patch(AccessTools.PropertyGetter(typeof(PlayerCookie), "PATH_VERSIONED"), prefix: new HarmonyMethod(typeof(ClientCookie), nameof(Versioned)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(PlayerCookie), "PATH_NONVERSIONED"), prefix: new HarmonyMethod(typeof(ClientCookie), nameof(Unversioned)));
        harmony.Patch(AccessTools.Method(typeof(PlayerCookie), nameof(PlayerCookie.Save)), prefix: new HarmonyMethod(typeof(ClientCookie), nameof(SkipSave)));
        var versioned = (string)AccessTools.PropertyGetter(typeof(PlayerCookie), "PATH_VERSIONED").Invoke(null, null);
        var unversioned = (string)AccessTools.PropertyGetter(typeof(PlayerCookie), "PATH_NONVERSIONED").Invoke(null, null);
        return versioned.StartsWith(_folder, StringComparison.OrdinalIgnoreCase) && unversioned.StartsWith(_folder, StringComparison.OrdinalIgnoreCase)
            ? Result<string, string>.Ok($"{versioned}; {unversioned}")
            : Result<string, string>.Error($"cookie paths not redirected: {versioned}; {unversioned}");
    }

    public static ulong Use()
    {
        var cookie = (PlayerCookie)Activator.CreateInstance(typeof(PlayerCookie), nonPublic: true);
        cookie.Version = 2;
        cookie.ClientId = NetworkManager.GenerateUniqueClientId();
        cookie.Username = "LatticeClient";
        AccessTools.Property(typeof(NetworkManager), nameof(NetworkManager.Cookie)).SetValue(null, cookie);
        return cookie.ClientId;
    }

    private static bool Versioned(ref string __result)
    {
        __result = Path.Combine(_folder, "PlayerCookie-v2.xml");
        return false;
    }

    private static bool Unversioned(ref string __result)
    {
        __result = Path.Combine(_folder, "PlayerCookie.xml");
        return false;
    }

    private static bool SkipSave()
    {
        SavesSkipped++;
        return false;
    }
}

/// <summary>
/// Every sync message Lattice receives, heard before <see cref="GameSync.Received"/>: its kind, frame header and
/// thread. Checks LaunchPadBooster's order on one connection and that messages are processed on the main thread.
/// </summary>
internal static class SyncHeard
{
    private static readonly Dictionary<SyncKind, int> Expected = new();
    private static int _mainThread;
    private static bool _installed;

    public static int OffMainThread { get; private set; }

    public static int OutOfOrder { get; private set; }

    public static int Messages { get; private set; }

    public static Dictionary<SyncKind, int> Starts { get; } = new();

    public static Dictionary<SyncKind, int> Frames { get; } = new();

    public static Dictionary<SyncKind, int> MostFrames { get; } = new();

    /// <summary>UTF-8 bytes of the largest whole message of each kind, frame headers left out.</summary>
    public static Dictionary<SyncKind, int> MostBytes { get; } = new();

    private static readonly Dictionary<SyncKind, int> Gathered = new();

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        _mainThread = Thread.CurrentThread.ManagedThreadId;
        new Harmony("lattice.live.sync-heard").Patch(AccessTools.Method(typeof(GameSync), nameof(GameSync.Received)),
            prefix: new HarmonyMethod(typeof(SyncHeard), nameof(Heard)));
    }

    public static void Reset()
    {
        Starts.Clear();
        Frames.Clear();
        MostFrames.Clear();
        MostBytes.Clear();
        Gathered.Clear();
    }

    public static string Summary()
    {
        var text = new StringBuilder();
        foreach (var kind in new[] { SyncKind.Command, SyncKind.Lines, SyncKind.Join, SyncKind.Answer })
            text.Append($"{kind} {Count(Starts, kind)} messages in {Count(Frames, kind)} frames (most {Count(MostFrames, kind)}); ");
        return text.ToString();
    }

    public static int Count(Dictionary<SyncKind, int> counts, SyncKind kind) => counts.TryGetValue(kind, out var n) ? n : 0;

    private static void Heard(SyncMessage message)
    {
        Messages++;
        if (Thread.CurrentThread.ManagedThreadId != _mainThread)
            OffMainThread++;
        Add(Frames, message.Kind, 1);
        if (message.Kind == SyncKind.Command)
        {
            Add(Starts, message.Kind, 1);
            return;
        }

        // Host to client texts are cut into frames "<k> <n>\n<piece>" (Core/Sync/Frames.cs).
        var text = message.Text;
        var newline = text.IndexOf('\n');
        var header = newline < 0 ? text : text.Substring(0, newline);
        var parts = header.Split(' ');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var k)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            OutOfOrder++;
            return;
        }

        var expected = Expected.TryGetValue(message.Kind, out var e) ? e : 0;
        if (k != expected)
            OutOfOrder++;
        Expected[message.Kind] = k + 1 == n ? 0 : k + 1;
        if (k == 0)
        {
            Add(Starts, message.Kind, 1);
            MostFrames[message.Kind] = Math.Max(Count(MostFrames, message.Kind), n);
            Gathered[message.Kind] = 0;
        }

        Add(Gathered, message.Kind, SyncLimits.Bytes(text) - (newline + 1));
        if (k + 1 == n)
            MostBytes[message.Kind] = Math.Max(Count(MostBytes, message.Kind), Count(Gathered, message.Kind));
    }

    private static void Add(Dictionary<SyncKind, int> counts, SyncKind kind, int by) => counts[kind] = Count(counts, kind) + by;
}

/// <summary>
/// Host plus client on the test instance. The host is the test dedicated server
/// (<c>two-player-host</c>); the client is a copy of the game client in batch mode with Steam off
/// (<c>two-player-client</c>), joined by address. The two runs meet at barrier files in <c>out\tp</c>: at each step one
/// side acts, the other waits for the board revision the actor reached, and both write and compare their boards.
/// </summary>
internal static class TwoPlayerRun
{
    private const string Station = "lattice-2p";
    // The test server binds only its setting.xml LocalIpAddress (Assets.Scripts.Networking/NetworkManager.cs:793-808);
    // the run script passes it as -lattice-join <address>.
    private static readonly string Address = JoinAddress();
    private const ushort Port = 27600;
    private const int BigSet = 20000;

    private static string JoinAddress()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, "-lattice-join");
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : "127.0.0.1";
    }
    private const ushort LocalPort = 27602;
    private const ulong FirstSteamId = 76561197960265728UL;
    private static readonly PrefabKey Frame = new(Animator.StringToHash(Scene.FrameName));
    private static readonly Maker LiveMaker = Maker.Create(LatticeIdentity.Mod, "live")
        .Match(static m => m, static r => throw new InvalidOperationException(r.ToString()));

    private static string _tp = "";
    private static bool _passed = true;

    public static IEnumerator Host(string outDir, Action<string> report)
    {
        void Line(string text) => Note(text, report);
        _tp = Folder(outDir);
        SyncHeard.Install();
        if (!Lattice(Line, out var world))
            yield break;
        Line($"host: seat {world.Seat}; game {GameManager.GetGameVersion()}");
        Line($"catalogue view {CatalogueSummary.Of(world.Catalogue)}");

        // A batch-mode client cannot answer the password prompt (NetworkMessages.VerifyPlayerRequest); the test
        // server's password is cleared in memory for this run only.
        Settings.CurrentData.ServerPassword = "";

        var before = Submit(world, PlanOf("Before join", Cells(600, 1010)));
        Line($"host: plan of 600 ghosts before the join: {Kind(before)}; board {Board(world)}");
        File.WriteAllText(Path.Combine(_tp, "host-ready.txt"), DateTime.Now.ToString("O"));

        Client? client = null;
        yield return Until(() => (client = ReadyClient()) != null, 900f);
        if (client == null)
        {
            Line("FAIL host: no client reached Ready in 900 s (two-player check not run)");
            yield break;
        }

        Line($"host: client {client.ClientId} '{client.name}' ready on connection {client.connectionId}");
        yield return Sync("joined", "host", true, Line);
        var reported = ReadText(Path.Combine(_tp, "client-id.txt")).Trim();
        Check($"the client's id is the generated one, not a Steam id: host sees {client.ClientId}, the client reports {reported}",
            reported == client.ClientId.ToString(CultureInfo.InvariantCulture) && client.ClientId != 0 && client.ClientId < FirstSteamId, Line);

        yield return Sync("client-place", "host", false, Line);
        if (Lattice(Line, out world))
            CheckStamped(world, "Client wing", new Actor.Player(new ClientId(client.ClientId)), Line);
        yield return Sync("client-remove", "host", false, Line);

        if (Lattice(Line, out world))
        {
            var placed = Submit(world, new Command.Place(Drafts(Cells(20, 1090))));
            var theirs = GhostsOf(world, "Client wing", 5);
            var removed = Submit(world, new Command.Remove(new Selector.Ids(ValueList.From(theirs))));
            Line($"host: placed 20 ({Kind(placed)}), removed 5 of the client's ({Kind(removed)})");
        }

        yield return Sync("host-act", "host", true, Line);
        yield return Sync("refused", "host", false, Line);

        if (Lattice(Line, out world))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var big = Submit(world, PlanOf("Big set", Cells(BigSet, 1130)));
            var bytes = big is Outcome.Applied applied ? SyncLimits.Bytes(SetLines.Write(applied.Changes)) : 0;
            Line($"host: plan of {BigSet:N0} ghosts: {Kind(big)} in {watch.Elapsed.TotalMilliseconds:F0} ms; its lines message {bytes:N0} bytes (limit {SyncLimits.Default.MaxMessageBytes:N0}); board {Board(world)}");
        }

        yield return Sync("big-set", "host", true, Line, 600f);
        yield return Sync("rejoined", "host", false, Line, 900f);

        if (Lattice(Line, out world))
        {
            var dump = Dump(world);
            SaveResult? saved = null;
            yield return SaveHelper.NewSave(Station, CancellationToken.None).ToCoroutine(r => saved = r);
            Line($"host: save {Station}: {(saved is { } s ? $"success {s.Success}" : "no result")}");
            var starts = LatticeTiming.Starts;
            var path = Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, Station, Station + ".save");
            Line($"host: loading {path} while the client is joined");
            LoadHelper.LoadGame(path, Station);
            yield return Until(() => LatticeTiming.Starts > starts && GameManager.GameState == GameState.Running && LatticeSystem.Current.IsSome, 300f);
            yield return new WaitForSecondsRealtime(2f);
            if (Lattice(Line, out world))
                Check("the host's plans after save and reload equal the plans before", Body(Dump(world)) == Body(dump), Line);
            File.WriteAllText(Path.Combine(_tp, "host-reloaded.txt"), DateTime.Now.ToString("O"));
        }

        yield return Sync("reloaded", "host", true, Line, 900f);
        yield return Until(() => File.Exists(Path.Combine(_tp, "client-done.txt")), 120f);
        Line($"host: sync heard: {SyncHeard.Summary()}");
        Check($"host: commands processed on the main thread ({SyncHeard.Messages} messages, {SyncHeard.OffMainThread} off it)", SyncHeard.OffMainThread == 0, Line);
        Line($"{(_passed ? "PASS" : "FAIL")} two-player host run");
    }

    public static IEnumerator Client(string outDir, Action<string> report)
    {
        void Line(string text) => Note(text, report);
        _tp = Folder(outDir);
        SyncHeard.Install();
        Line($"client: cookie paths {LiveRun.CookiePaths}; game {GameManager.GetGameVersion()}; batch {GameManager.IsBatchMode}");
        if (!LiveRun.CookieReady)
        {
            Line("FAIL client: the cookie path patch did not land; no join (two-player check not run)");
            yield break;
        }

        yield return Until(() => File.Exists(Path.Combine(_tp, "host-ready.txt")), 900f);
        yield return Until(() => GameManager.GameState == GameState.None, 120f);
        yield return new WaitForSecondsRealtime(5f);
        var id = ClientCookie.Use();
        Line($"client: generated id {id} (Steam ids start at {FirstSteamId})");

        yield return Join(Line, "first join");
        File.WriteAllText(Path.Combine(_tp, "client-id.txt"), NetworkManager.LocalClientId.ToString(CultureInfo.InvariantCulture));
        yield return Sync("joined", "client", false, Line, 900f);
        Check($"a join of a board over 500 ghosts arrives in parts: {SyncHeard.Count(SyncHeard.Starts, SyncKind.Join)} join messages",
            SyncHeard.Count(SyncHeard.Starts, SyncKind.Join) > 1, Line);

        if (Lattice(Line, out var world))
        {
            Line($"catalogue view {CatalogueSummary.Of(world.Catalogue)}");
            var placed = default(Outcome);
            yield return Ask(world, PlanOf("Client wing", Cells(20, 1050)), o => placed = o);
            Line($"client: plan of 20 ghosts: {Kind(placed)}");
            var wing = Option<PlanId>.None;
            yield return Until(() => (wing = PlanNamed(world, "Client wing")).IsSome, 60f);
            if (wing.TryGet(out var plan) && GhostsOf(world, "Client wing", 20).Count == 0)
            {
                var into = default(Outcome);
                yield return Ask(world, new Command.Place(Drafts(Cells(20, 1050), Option.Some(plan))), o => into = o);
                Line($"client: the plan arrived without its ghosts; placed 20 into it: {Kind(into)}");
            }
        }

        yield return Sync("client-place", "client", true, Line);

        if (Lattice(Line, out world))
        {
            var removed = default(Outcome);
            yield return Ask(world, new Command.Remove(new Selector.Ids(ValueList.From(GhostsOf(world, "Client wing", 5)))), o => removed = o);
            Line($"client: removed 5: {Kind(removed)}");
        }

        yield return Sync("client-remove", "client", true, Line);
        yield return Sync("host-act", "client", false, Line);

        if (Lattice(Line, out world))
        {
            var revision = world.Ghosts.Revision;
            var unknown = default(Outcome);
            var draft = Draft(new Placement(new PrefabKey(0x5EEDBEEF), new Cell(10, 1170, 10), Turn.None));
            yield return Ask(world, new Command.Place(ValueList.From(new[] { draft })), o => unknown = o);
            Line($"client: place of an unknown piece: {Describe(unknown)}");
            var missing = default(Outcome);
            yield return Ask(world, new Command.Remove(new Selector.Ids(ValueList.From(new[] { new GhostId(987654321) }))), o => missing = o);
            Line($"client: remove of a ghost that does not exist: {Describe(missing)}");
            Check("a refused command answers Refused to the client and leaves its board unchanged",
                (unknown is Outcome.Refused || missing is Outcome.Refused) && world.Ghosts.Revision == revision, Line);
        }

        yield return Sync("refused", "client", true, Line);

        SyncHeard.Reset();
        yield return Sync("big-set", "client", false, Line, 600f);
        var bigFrames = SyncHeard.Count(SyncHeard.MostFrames, SyncKind.Lines);
        var bigBytes = SyncHeard.Count(SyncHeard.MostBytes, SyncKind.Lines);
        Check($"a set over 1 MB arrives in frames: the largest lines message {bigBytes:N0} bytes in {bigFrames} frames",
            bigBytes > SyncLimits.Default.MaxMessageBytes && bigFrames > 1, Line);

        yield return Leave(Line);
        SyncHeard.Reset();
        yield return Join(Line, "rejoin after leaving");
        Check($"the client that left and came back gets a fresh join: {SyncHeard.Count(SyncHeard.Starts, SyncKind.Join)} join messages",
            SyncHeard.Count(SyncHeard.Starts, SyncKind.Join) > 0, Line);
        yield return Sync("rejoined", "client", true, Line, 900f);

        yield return Until(() => GameManager.GameState != GameState.Running || LatticeSystem.Current.IsNone, 300f);
        Line($"client: the host's reload ended the session: state {GameManager.GameState}, network {NetworkManager.NetworkState}");
        yield return Until(() => File.Exists(Path.Combine(_tp, "host-reloaded.txt")), 600f);
        yield return Leave(Line);
        SyncHeard.Reset();
        yield return Join(Line, "rejoin after the host's reload");
        yield return Sync("reloaded", "client", false, Line, 900f);

        Line($"client: sync heard: {SyncHeard.Summary()}");
        Check($"client: sync messages processed on the main thread ({SyncHeard.Messages} messages, {SyncHeard.OffMainThread} off it)", SyncHeard.OffMainThread == 0, Line);
        Check($"client: frames arrive in order on channel 169 ({SyncHeard.OutOfOrder} out of order)", SyncHeard.OutOfOrder == 0, Line);
        Line($"client: cookie saves skipped {ClientCookie.SavesSkipped}");
        Line($"{(_passed ? "PASS" : "FAIL")} two-player client run");
        File.WriteAllText(Path.Combine(_tp, "client-done.txt"), DateTime.Now.ToString("O"));
    }

    private static IEnumerator Join(Action<string> line, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // The menu's join by address (NetworkClient.JoinClientFromMenu, Assets.Scripts/NetworkClient.cs:310-336):
        // OnJoinStart creates the join's cancellation that ProcessJoinData reads (NetworkClient.cs:346, 384).
        NetworkClient.ClientPreJoin();
        var started = NetworkManager.StartClient(Address, Port, LocalPort);
        if (started)
            NetworkClient.OnJoinStart();
        line($"client: {what}: StartClient({Address}, {Port}, {LocalPort}) {started}");
        yield return Until(() => LatticeSystem.Current.TryGet(out var s) && s is LatticeWorld { Client.Joined: true }, 900f);
        var joined = LatticeSystem.Current.TryGet(out var system) && system is LatticeWorld { Client.Joined: true };
        Check($"client: {what}: joined in {watch.Elapsed.TotalSeconds:F1} s, state {GameManager.GameState}, network {NetworkManager.NetworkState}, id {NetworkManager.LocalClientId}", joined, line);
    }

    private static IEnumerator Leave(Action<string> line)
    {
        if (NetworkManager.NetworkState != NetworkState.Offline || GameManager.GameState != GameState.None)
        {
            try
            {
                GameManager.LeaveGame();
            }
            catch (Exception e)
            {
                line($"client: LeaveGame threw {e.GetType().Name}: {e.Message}; disconnecting instead");
                NetworkClient.Disconnect(force: true).Forget();
            }
        }

        yield return Until(() => GameManager.GameState == GameState.None && NetworkManager.NetworkState == NetworkState.Offline && LatticeSystem.Current.IsNone, 120f);
        line($"client: left: state {GameManager.GameState}, network {NetworkManager.NetworkState}, Lattice {(LatticeSystem.Current.IsSome ? "running" : "ended")}");
        yield return new WaitForSecondsRealtime(5f);
    }

    // At a barrier one side has acted; the other waits until its board reaches the actor's revision; both write their
    // boards and compare.
    private static IEnumerator Sync(string step, string side, bool acting, Action<string> line, float limit = 300f)
    {
        var other = side == "host" ? "client" : "host";
        var mine = Path.Combine(_tp, $"{side}-{step}.txt");
        var theirs = Path.Combine(_tp, $"{other}-{step}.txt");
        if (!acting)
        {
            yield return Until(() => File.Exists(theirs), limit);
            var target = Revision(ReadText(theirs));
            yield return Until(() => Lattice(static _ => { }, out var w) && w.Ghosts.Revision.Value >= target, limit);
        }

        yield return new WaitForSecondsRealtime(1f);
        File.WriteAllText(mine, Lattice(line, out var world) ? Dump(world) : "rev -1\n");
        yield return Until(() => File.Exists(theirs), limit);
        var a = ReadText(mine);
        var b = ReadText(theirs);
        Check($"{side}: boards equal after '{step}' ({Board(world)}; {Lines(a)} plan lines here, {Lines(b)} there)", a == b && world != null, line);
    }

    private static IEnumerator Ask(LatticeWorld world, Command command, Action<Outcome> answered)
    {
        Outcome? outcome = null;
        Ticket? ticket = null;
        void Heard(Ticket t, Outcome o)
        {
            if (ticket is { } mine && t == mine)
                outcome = o;
        }

        world.Events.Answered += Heard;
        switch (world.Commands.Submit(new CommandId(Guid.NewGuid()), command, LatticeIdentity.Actor))
        {
            case Submission.Answered now:
                outcome = now.Outcome;
                break;
            case Submission.Sent sent:
                ticket = sent.Ticket;
                break;
        }

        yield return Until(() => outcome != null, 120f);
        world.Events.Answered -= Heard;
        answered(outcome ?? new Outcome.Refused(ValueList<Refusal>.Empty));
    }

    private static IEnumerator Until(Func<bool> done, float limit)
    {
        var deadline = Time.realtimeSinceStartup + limit;
        while (!done() && Time.realtimeSinceStartup < deadline)
            yield return null;
    }

    private static Client? ReadyClient()
    {
        foreach (var client in NetworkBase.Clients)
            if (client.state == ClientState.Ready)
                return client;
        return null;
    }

    private static void CheckStamped(LatticeWorld world, string plan, Actor expected, Action<string> line)
    {
        var count = 0;
        var stamped = 0;
        var other = "";
        foreach (var entry in world.Ghosts.Index.Plans)
        {
            if (entry.Value.Plan.Name.Value != plan)
                continue;
            foreach (var id in entry.Value.Ghosts)
            {
                if (!world.Ghosts.Index.Ghosts.TryGetValue(id, out var ghost))
                    continue;
                count++;
                if (ghost.Ghost.PlacedBy == expected)
                    stamped++;
                else
                    other = ghost.Ghost.PlacedBy.ToString();
            }
        }

        Check($"the host stamps the client's player on its ghosts: {stamped} of {count} placed by {expected}{(other.Length > 0 ? $", other {other}" : "")}",
            count > 0 && stamped == count, line);
    }

    private static List<GhostId> GhostsOf(LatticeWorld world, string plan, int count)
    {
        var ids = new List<GhostId>(count);
        foreach (var entry in world.Ghosts.Index.Plans)
            if (entry.Value.Plan.Name.Value == plan)
                foreach (var id in entry.Value.Ghosts)
                    ids.Add(id);
        ids.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        if (ids.Count > count)
            ids.RemoveRange(count, ids.Count - count);
        return ids;
    }

    private static List<Cell> Cells(int count, int y)
    {
        var side = (int)Math.Ceiling(Math.Sqrt(count));
        var cells = new List<Cell>(count);
        for (var i = 0; i < count; i++)
            cells.Add(new Cell(10 + 20 * (i % side), y, 10 + 20 * (i / side)));
        return cells;
    }

    private static Command PlanOf(string name, List<Cell> cells) =>
        new Command.CreatePlan(new PlanDraft(
            PlanName.Create(name).Match(static n => n, static r => throw new InvalidOperationException(r.ToString())),
            LiveMaker, new Lattice.Api.Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty))
        {
            Ghosts = Drafts(cells),
        };

    private static ValueList<GhostDraft> Drafts(List<Cell> cells) => Drafts(cells, Option<PlanId>.None);

    private static ValueList<GhostDraft> Drafts(List<Cell> cells, Option<PlanId> plan)
    {
        var drafts = new List<GhostDraft>(cells.Count);
        foreach (var cell in cells)
            drafts.Add(Draft(new Placement(Frame, cell, Turn.None), plan));
        return ValueList.From(drafts);
    }

    private static Option<PlanId> PlanNamed(LatticeWorld world, string name)
    {
        foreach (var entry in world.Ghosts.Index.Plans)
            if (entry.Value.Plan.Name.Value == name)
                return Option.Some(entry.Key);
        return Option<PlanId>.None;
    }

    private static GhostDraft Draft(Placement placement) => Draft(placement, Option<PlanId>.None);

    private static GhostDraft Draft(Placement placement, Option<PlanId> plan) => new(
        new Intent.Build(placement, Option<Finish>.None), plan, LiveMaker, new Lattice.Api.Visibility.Everyone(),
        Option<Lattice.Api.Label>.None, Option<Tint>.None, ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty);

    private static Outcome Submit(LatticeWorld world, Command command) =>
        world.Commands.Submit(new CommandId(Guid.NewGuid()), command, LatticeIdentity.Actor) switch
        {
            Submission.Answered answered => answered.Outcome,
            var other => throw new InvalidOperationException("not answered at once: " + other),
        };

    private static string Kind(Outcome? outcome) => outcome switch
    {
        Outcome.Applied applied => $"applied at revision {applied.Changes.Revision.Value} ({applied.Changes.Added.Count} added, {applied.Changes.Removed.Count} removed)",
        Outcome.Refused refused => $"refused ({refused.Why.Count} reasons)",
        _ => "no answer",
    };

    private static string Describe(Outcome? outcome)
    {
        if (outcome is not Outcome.Refused refused)
            return Kind(outcome);
        var text = new StringBuilder("refused:");
        foreach (var why in refused.Why)
            text.Append(' ').Append(why.Why.Code).Append('/').Append(why.Why.From);
        return text.ToString();
    }

    private static bool Lattice(Action<string> line, out LatticeWorld world)
    {
        if (LatticeSystem.Current.TryGet(out var system) && system is LatticeWorld found)
        {
            world = found;
            return true;
        }

        world = null!;
        line("FAIL two-player: Lattice is not running in this world");
        return false;
    }

    // The board's revision, then every ghost by id: plan, revision, intent, state and placer, sorted.
    private static string Dump(LatticeWorld world)
    {
        var lines = new List<string>(world.Ghosts.Index.Ghosts.Count + world.Ghosts.Index.Plans.Count);
        foreach (var entry in world.Ghosts.Index.Plans)
            lines.Add($"plan {entry.Key.Value} '{entry.Value.Plan.Name.Value}' ghosts {entry.Value.Ghosts.Count}");
        foreach (var entry in world.Ghosts.Index.Ghosts)
        {
            var g = entry.Value.Ghost;
            lines.Add($"ghost {g.Id.Value} plan {g.Plan.Value} rev {g.Revision.Value} {g.Intent} {g.State} {g.PlacedBy}");
        }

        lines.Sort(StringComparer.Ordinal);
        return $"rev {world.Ghosts.Revision.Value}\n" + string.Join("\n", lines) + "\n";
    }

    private static string Body(string dump) => dump.Substring(dump.IndexOf('\n') + 1);

    private static long Revision(string dump) =>
        dump.StartsWith("rev ", StringComparison.Ordinal) && long.TryParse(dump.Substring(4, dump.IndexOf('\n') - 4), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r) ? r : long.MaxValue;

    private static string Board(LatticeWorld? world) =>
        world == null ? "no board" : $"{world.Ghosts.Index.Ghosts.Count} ghosts in {world.Ghosts.Index.Plans.Count} plans at revision {world.Ghosts.Revision.Value}";

    private static int Lines(string text) => text.Split('\n').Length - 1;

    private static string ReadText(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }

        return "";
    }

    private static string Folder(string outDir)
    {
        var folder = Path.Combine(outDir, "tp");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Check(string what, bool ok, Action<string> line)
    {
        if (!ok)
            _passed = false;
        line($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private static void Note(string text, Action<string> report)
    {
        if (text.StartsWith("FAIL", StringComparison.Ordinal))
            _passed = false;
        report(text);
    }
}
