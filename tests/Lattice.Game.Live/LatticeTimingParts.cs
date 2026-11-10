using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Slices;
using UnityEngine;

namespace Lattice.Game.Live;

/// <summary>
/// What each recorded Lattice frame spent where, read by Harmony from the test plugin: every module's frame work
/// (<c>ModuleFaults.Run</c>, called for each module from <c>LatticeWorld.Frame</c>), the scheduler's slices
/// (<c>FrameScheduler.RunFrame</c>) and each sliced work's <c>Step</c>, in main-thread CPU milliseconds
/// (<see cref="ThreadCpuMs"/>). The hooks write into preallocated arrays, so a frame still allocates nothing.
/// </summary>
internal static partial class LatticeTiming
{
    private const int Capacity = 1 << 16;
    private const string SchedulerLabel = "scheduler";

    private readonly record struct Part(object Label, double Ms);

    private static readonly Part[] Parts = new Part[Capacity];
    private static readonly List<int> FrameFirst = new(4096);
    private static readonly List<float> FrameAt = new(4096);
    private static readonly List<bool> FrameBusy = new(4096);
    private static int _partCount;
    private static bool _partsFull;
    private static float _windowStart;

    /// <summary>The world the validation state is read from at the end of each frame.</summary>
    public static LatticeWorld? Watching { get; set; }

    private static void ClearParts()
    {
        _partCount = 0;
        _partsFull = false;
        FrameFirst.Clear();
        FrameAt.Clear();
        FrameBusy.Clear();
        _windowStart = Time.realtimeSinceStartup;
    }

    private static void BeginParts()
    {
        FrameFirst.Add(_partCount);
        FrameAt.Add(Time.realtimeSinceStartup);
    }

    private static void EndParts() => FrameBusy.Add(Watching?.ValidationModule is { Busy: true });

    private static void Add(object label, double ms)
    {
        if (!Recording || FrameFirst.Count == FrameBusy.Count)
            return;
        if (_partCount == Capacity)
        {
            _partsFull = true;
            return;
        }

        Parts[_partCount++] = new Part(label, ms);
    }

    private static void InstallParts(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ModuleFaults), nameof(ModuleFaults.Run)),
            prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(ModuleBegins)), postfix: new HarmonyMethod(typeof(LatticeTiming), nameof(ModuleEnds)));
        harmony.Patch(AccessTools.Method(typeof(FrameScheduler), nameof(FrameScheduler.RunFrame)),
            prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(SchedulerBegins)), postfix: new HarmonyMethod(typeof(LatticeTiming), nameof(SchedulerEnds)));
        var seen = new HashSet<MethodBase>();
        foreach (var type in typeof(LatticeWorld).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || !typeof(ISlicedWork).IsAssignableFrom(type))
                continue;
            var step = type.GetInterfaceMap(typeof(ISlicedWork)).TargetMethods[0];
            if (step.DeclaringType != type || !seen.Add(step))
                continue;
            try
            {
                harmony.Patch(step, prefix: new HarmonyMethod(typeof(LatticeTiming), nameof(StepBegins)), postfix: new HarmonyMethod(typeof(LatticeTiming), nameof(StepEnds)));
            }
            catch (Exception)
            {
                // A work type Harmony cannot patch is left out of the breakdown; its time stays in the frame total.
            }
        }
    }

    private static void ModuleBegins(out double __state) => __state = ThreadCpuMs();

    private static void ModuleEnds(ModuleName module, double __state) => Add(module.Value, ThreadCpuMs() - __state);

    private static void SchedulerBegins(out double __state) => __state = ThreadCpuMs();

    private static void SchedulerEnds(double __state) => Add(SchedulerLabel, ThreadCpuMs() - __state);

    private static void StepBegins(out double __state) => __state = ThreadCpuMs();

    private static void StepEnds(MethodBase __originalMethod, double __state) => Add(__originalMethod, ThreadCpuMs() - __state);

    private static string N(double ms) => ms.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// One line for each recorded frame whose main-thread CPU time is over <paramref name="over"/> ms, the slowest
    /// <paramref name="top"/> of them, slowest first: its index among the window's frames, when it ran from the window's start,
    /// wall and CPU time, whether validation was busy, then module by module (ms, calls) and the sliced works' steps; the rest
    /// of the frame (board events, style callbacks) is <c>other</c>.
    /// </summary>
    public static List<string> Describe(string window, int top, double over, int ordinaryFrames)
    {
        var order = new List<int>();
        for (var i = 0; i < FrameCpuMs.Count; i++)
            if (FrameCpuMs[i] > over)
                order.Add(i);
        order.Sort((a, b) => FrameCpuMs[b].CompareTo(FrameCpuMs[a]));
        var lines = new List<string>();
        foreach (var frame in order.GetRange(0, Math.Min(top, order.Count)))
        {
            var first = FrameFirst[frame];
            var end = frame + 1 < FrameFirst.Count ? FrameFirst[frame + 1] : _partCount;
            var modules = new List<(string Label, double Ms, int Calls)>();
            var steps = new List<(string Label, double Ms, int Calls)>();
            double accounted = 0;
            for (var k = first; k < end; k++)
            {
                var (label, ms) = Parts[k];
                if (label is MethodBase method)
                    Merge(steps, method.DeclaringType?.Name ?? method.Name, ms);
                else
                {
                    Merge(modules, (string)label, ms);
                    accounted += ms;
                }
            }

            string Text(List<(string Label, double Ms, int Calls)> items) =>
                string.Join(", ", items.ConvertAll(static i => $"{i.Label} {N(i.Ms)} ms (x{i.Calls})"));
            var where = frame >= ordinaryFrames ? " (the allocation-check call, not a rendered frame)" : "";
            lines.Add($"bigbase: {window} slow frame {frame}{where} at +{N(FrameAt[frame] - _windowStart)} s: " +
                      $"cpu {N(FrameCpuMs[frame])} ms, wall {N(FrameMs[frame])} ms, validation busy {FrameBusy[frame]}; " +
                      $"modules [{Text(modules)}]; steps [{Text(steps)}]; other {N(FrameCpuMs[frame] - accounted)} ms");
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
