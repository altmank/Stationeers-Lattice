using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Plans;
using CoreSafetyRun = Lattice.Core.Safety.SafetyRun;
using Lattice.Core.Slices;
using Lattice.Core.Steps;
using Lattice.Core.Validation;
using Lattice.Render;
using UnityEngine;

namespace Lattice.Game.Live;

/// <summary>
/// What each recorded Lattice frame spent where, read by Harmony from the test plugin: every module's frame work
/// (<c>ModuleFaults.Run</c>, called for each module from <c>LatticeWorld.Frame</c>), the scheduler's slices
/// (<c>FrameScheduler.RunFrame</c>) and every scheduled step of any assembly (the watched scheduler's step observer), in
/// main-thread CPU milliseconds (<see cref="ThreadCpuMs"/>). A step of plan state's work or Render's rebuilds also carries
/// the phase it ran (read by a Harmony prefix before the step) and the pieces Render read in it. The hooks write into
/// preallocated arrays, and a work type's label is made the first time it is seen, so a frame allocates nothing after that.
/// </summary>
internal static partial class LatticeTiming
{
    private const int Capacity = 1 << 16;
    private const string SchedulerLabel = "scheduler";

    private readonly record struct Part(string Label, double Ms, bool Step, string? Phase, int NewPieces);

    private static readonly Part[] Parts = new Part[Capacity];
    private static readonly List<int> FrameFirst = new(4096);
    private static readonly List<float> FrameAt = new(4096);
    private static readonly List<bool> FrameBusy = new(4096);
    private static readonly List<int> FrameGc0 = new(4096);
    private static readonly Dictionary<Type, string> Labels = new();
    private static readonly Action<ISlicedWork, TimeSpan> Observer = StepObserved;
    private static int _partCount;
    private static bool _partsFull;
    private static float _windowStart;
    private static double _mark;
    private static int _gcAtFrame;

    private static object? _phaseWork;
    private static string? _phase;
    private static DrawState? _phaseDraw;
    private static int _piecesBefore;
    private static long _sortsBefore;

    private static AccessTools.FieldRef<Rebuilds, bool>? _sorting;
    private static AccessTools.FieldRef<Rebuilds, bool>? _paging;
    private static AccessTools.FieldRef<Rebuilds, DrawGroup?>? _group;
    private static AccessTools.FieldRef<Rebuilds, long>? _sorts;
    private static AccessTools.FieldRef<Rebuilds, DrawState>? _drawState;
    private static AccessTools.FieldRef<DrawState, Dictionary<PrefabKey, PieceDraw?>>? _pieces;
    private static FieldInfo? _owner;
    private static FieldInfo? _job;
    private static FieldInfo? _jobFindings;
    private static FieldInfo? _jobGhosts;
    private static FieldInfo? _jobBill;
    private static FieldInfo? _jobSafety;

    private static LatticeWorld? _watching;

    /// <summary>
    /// The world the validation state is read from at the end of each frame. Setting a world puts the step observer on its
    /// scheduler, so every step it takes from then on is timed.
    /// </summary>
    public static LatticeWorld? Watching
    {
        get => _watching;
        set
        {
            _watching = value;
            if (value is not null)
                value.Scheduler.StepObserver = Observer;
        }
    }

    /// <summary>The warm-up sets the world's process has finished, as one text for the log.</summary>
    public static string WarmSetsText(LatticeWorld world) =>
        world.WarmSets is { } sets
            ? string.Join(", ", new[] { "core", "api", "render" }.Select(set => $"{set} {(sets.IsDone(set) ? "done" : "not done")}"))
            : "no warm-up";

    private static void ClearParts()
    {
        _partCount = 0;
        _partsFull = false;
        FrameFirst.Clear();
        FrameAt.Clear();
        FrameBusy.Clear();
        FrameGc0.Clear();
        _windowStart = Time.realtimeSinceStartup;
    }

    private static void BeginParts()
    {
        FrameFirst.Add(_partCount);
        FrameAt.Add(Time.realtimeSinceStartup);
        _gcAtFrame = GC.CollectionCount(0);
    }

    private static void EndParts()
    {
        FrameBusy.Add(Watching?.ValidationModule is { Busy: true });
        FrameGc0.Add(GC.CollectionCount(0) - _gcAtFrame);
    }

    private static void Add(string label, double ms, bool step = false, string? phase = null, int newPieces = 0)
    {
        if (!Recording || FrameFirst.Count == FrameBusy.Count)
            return;
        if (_partCount == Capacity)
        {
            _partsFull = true;
            return;
        }

        Parts[_partCount++] = new Part(label, ms, step, phase, newPieces);
    }

    private static void InstallParts(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ModuleFaults), nameof(ModuleFaults.Run)),
            prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(ModuleBegins)), postfix: new HarmonyMethod(typeof(LatticeTiming), nameof(ModuleEnds)));
        harmony.Patch(AccessTools.Method(typeof(FrameScheduler), nameof(FrameScheduler.RunFrame)),
            prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(SchedulerBegins)), postfix: new HarmonyMethod(typeof(LatticeTiming), nameof(SchedulerEnds)));
        InstallPhases(harmony);
    }

    private static void InstallPhases(Harmony harmony)
    {
        try
        {
            var rebuilds = typeof(Rebuilds);
            _sorting = AccessTools.FieldRefAccess<Rebuilds, bool>("_sorting");
            _paging = AccessTools.FieldRefAccess<Rebuilds, bool>("_paging");
            _group = AccessTools.FieldRefAccess<Rebuilds, DrawGroup?>("_group");
            _sorts = AccessTools.FieldRefAccess<Rebuilds, long>("_sorts");
            var drawField = rebuilds.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).First(f => f.FieldType == typeof(DrawState));
            _drawState = AccessTools.FieldRefAccess<Rebuilds, DrawState>(drawField);
            _pieces = AccessTools.FieldRefAccess<DrawState, Dictionary<PrefabKey, PieceDraw?>>("_pieces");

            var work = AccessTools.Inner(typeof(PlanStateModule), "Work");
            var job = AccessTools.Inner(typeof(PlanStateModule), "PlanJob");
            _owner = work.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).First(f => f.FieldType == typeof(PlanStateModule));
            _job = AccessTools.Field(typeof(PlanStateModule), "_job");
            _jobFindings = AccessTools.Field(job, "_findings");
            _jobGhosts = AccessTools.Field(job, "_ghosts");
            _jobBill = AccessTools.Field(job, "_bill");
            _jobSafety = AccessTools.Field(job, "_safety");

            harmony.Patch(AccessTools.Method(rebuilds, nameof(Rebuilds.Step)), prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(RebuildsBegin)));
            harmony.Patch(AccessTools.Method(work, nameof(ISlicedWork.Step)), prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(PlanWorkBegin)));
        }
        catch (Exception)
        {
            // A phase that cannot be read leaves its steps without one; they are still timed and labelled by their type.
        }
    }

    // What the rebuild is about to do, from the state before the step; a step that starts a sort is relabelled after it.
    private static void RebuildsBegin(Rebuilds __instance)
    {
        var draw = _drawState!(__instance);
        _phaseWork = __instance;
        _phaseDraw = draw;
        _piecesBefore = _pieces!(draw).Count;
        _sortsBefore = _sorts!(__instance);
        _phase = _group!(__instance) is not null ? (_paging!(__instance) ? "page" : "copy")
            : _sorting!(__instance) ? "sort"
            : draw.Dirty.Count > 0 ? "pick" : "idle";
    }

    private static void PlanWorkBegin(object __instance)
    {
        _phaseWork = __instance;
        _phaseDraw = null;
        _phase = PlanPhase(_job!.GetValue(_owner!.GetValue(__instance)));
    }

    // The part of the plan job the next step works, in the order PlanJob.Step takes them.
    private static string PlanPhase(object? job)
    {
        if (job is null)
            return "job-start";
        if (_jobFindings!.GetValue(job) is not FindingsRun findings)
            return "findings-start";
        if (_jobGhosts!.GetValue(job) is null || _jobBill!.GetValue(job) is not BillMaker.BillRun bill)
            return "select";
        if (!findings.Done)
            return "findings";
        if (!bill.Done)
            return "bill";
        return _jobSafety!.GetValue(job) switch
        {
            null => "safety-start",
            CoreSafetyRun { Done: false } run => StepPhase.Safety(run.Check),
            CoreSafetyRun => "facts",
            _ => "safety:?",
        };
    }

    private static string LabelOf(Type type)
    {
        if (!Labels.TryGetValue(type, out var label))
            Labels[type] = label = StepLabel.Of(type);
        return label;
    }

    private static void StepObserved(ISlicedWork work, TimeSpan time)
    {
        var now = ThreadCpuMs();
        var ms = now - _mark;
        _mark = now;
        string? phase = null;
        var pieces = 0;
        if (ReferenceEquals(work, _phaseWork))
        {
            phase = _phase;
            if (_phaseDraw is { } draw && work is Rebuilds rebuilds)
            {
                pieces = Math.Max(0, _pieces!(draw).Count - _piecesBefore);
                if (_sorts!(rebuilds) != _sortsBefore)
                    phase = "sort";
            }
        }

        _phaseWork = null;
        _phase = null;
        _phaseDraw = null;
        Add(LabelOf(work.GetType()), ms, true, phase, pieces);
    }

    private static void ModuleBegins(out double __state) => __state = ThreadCpuMs();

    private static void ModuleEnds(ModuleName module, double __state) => Add(module.Value, ThreadCpuMs() - __state);

    private static void SchedulerBegins(out double __state) => __state = _mark = ThreadCpuMs();

    private static void SchedulerEnds(double __state) => Add(SchedulerLabel, ThreadCpuMs() - __state);

    private static string N(double ms) => ms.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// One line for each recorded frame whose main-thread CPU time is over <paramref name="over"/> ms, the slowest
    /// <paramref name="top"/> of them, slowest first: its index among the window's frames, when it ran from the window's start,
    /// wall and CPU time, whether validation was busy, then module by module (ms, calls) and the steps by work type and phase
    /// (ms, calls, pieces Render read); the rest of the frame (board events, style callbacks) is <c>other</c>. The line ends
    /// with the frame's longest single step and its gen0 collections.
    /// </summary>
    public static List<string> Describe(string window, int top, double over, int ordinaryFrames)
    {
        var lines = new List<string>();
        foreach (var frame in SlowFrames.Pick(FrameCpuMs, top, over))
        {
            var first = FrameFirst[frame];
            var end = frame + 1 < FrameFirst.Count ? FrameFirst[frame + 1] : _partCount;
            var modules = new List<(string Label, double Ms, int Calls)>();
            var steps = new List<(string Label, double Ms, int Calls)>();
            double accounted = 0;
            var longest = -1;
            for (var k = first; k < end; k++)
            {
                var part = Parts[k];
                if (part.Step)
                {
                    var label = part.Phase is null ? part.Label : $"{part.Label} [{part.Phase}]";
                    Merge(steps, label, part.Ms);
                    if (longest < 0 || part.Ms > Parts[longest].Ms)
                        longest = k;
                }
                else
                {
                    Merge(modules, part.Label, part.Ms);
                    accounted += part.Ms;
                }
            }

            string Text(List<(string Label, double Ms, int Calls)> items) =>
                string.Join(", ", items.ConvertAll(static i => $"{i.Label} {N(i.Ms)} ms (x{i.Calls})"));
            var pieces = 0;
            for (var k = first; k < end; k++)
                pieces += Parts[k].NewPieces;
            var where = frame >= ordinaryFrames ? " (the allocation-check call, not a rendered frame)" : "";
            var longestText = longest < 0
                ? "none"
                : $"{Parts[longest].Label}{(Parts[longest].Phase is { } p ? $" [{p}]" : "")} {N(Parts[longest].Ms)} ms{(Parts[longest].NewPieces > 0 ? $", new pieces {Parts[longest].NewPieces}" : "")}";
            lines.Add($"bigbase: {window} slow frame {frame}{where} at +{N(FrameAt[frame] - _windowStart)} s: " +
                      $"cpu {N(FrameCpuMs[frame])} ms, wall {N(FrameMs[frame])} ms, validation busy {FrameBusy[frame]}; " +
                      $"modules [{Text(modules)}]; steps [{Text(steps)}]; other {N(FrameCpuMs[frame] - accounted)} ms; " +
                      $"longest step {longestText}; new pieces {pieces}; gen0 collections {(frame < FrameGc0.Count ? FrameGc0[frame] : 0)}");
        }

        if (_partsFull)
            lines.Add($"bigbase: {window}: the breakdown buffer filled ({Capacity} entries); later frames have no breakdown");
        return lines;
    }

    private static void Merge(List<(string Label, double Ms, int Calls)> items, string label, double ms)
    {
        var at = items.FindIndex(i => i.Label == label);
        if (at < 0)
            items.Add((label, ms, 1));
        else
            items[at] = (label, items[at].Ms + ms, items[at].Calls + 1);
    }
}
