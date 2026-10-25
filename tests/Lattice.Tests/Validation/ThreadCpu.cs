using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lattice.Tests.Validation;

/// <summary>
/// The calling thread's CPU time, from its cycle count (Windows) at the rate the thread runs when busy, measured by
/// spinning (median of a few spins, so a spin that was descheduled does not set it). None elsewhere: frames are
/// then judged on wall-clock time.
/// </summary>
internal sealed class ThreadCpu
{
    private static readonly IntPtr CurrentThread = new(-2);
    private readonly double _cyclesPerTick;

    private ThreadCpu(double cyclesPerTick) => _cyclesPerTick = cyclesPerTick;

    public bool Measures => _cyclesPerTick > 0;

    public static ThreadCpu Calibrate()
    {
        if (!OperatingSystem.IsWindows())
            return new ThreadCpu(0);
        var rates = new double[5];
        for (var i = 0; i < rates.Length; i++)
        {
            var cycles = Cycles();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMilliseconds(20))
            {
            }

            rates[i] = (Cycles() - cycles) / (double)watch.Elapsed.Ticks;
        }

        Array.Sort(rates);
        return new ThreadCpu(rates[rates.Length / 2]);
    }

    public ulong Now() => Measures ? Cycles() : 0;

    public TimeSpan Between(ulong start, ulong end) => TimeSpan.FromTicks((long)((end - start) / _cyclesPerTick));

    public override string ToString() =>
        Measures ? $"thread CPU from cycle counts at {_cyclesPerTick * TimeSpan.TicksPerMillisecond / 1_000_000:F2}M cycles a ms" : "no thread CPU measure; frames judged on wall-clock time";

    private static ulong Cycles() => QueryThreadCycleTime(CurrentThread, out var cycles) ? cycles : 0;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
}
