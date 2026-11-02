using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Lattice.Api;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Lattice.Game.Live;

/// <summary>
/// Each frame's GPU time and main-thread time, in a Direct3D 11 player. The shipped player has Frame Timing Stats off
/// (PlayerSettings.enableFrameTimingStats in globalgamemanagers), so <see cref="FrameTimingManager"/> reports nothing.
/// Instead, each frame gets a D3D11 TIMESTAMP_DISJOINT query with a TIMESTAMP at the frame's start (the first step of
/// the player loop's Initialization) and one at its end (end of frame, after every camera and the GUI). Both are
/// issued on Unity's render thread through <see cref="GL.IssuePluginEvent(IntPtr, int)"/>, in the order the frame's
/// own commands reach the device, so the span between them is the GPU's time from the frame's first command to its
/// last. Unity's own gpuFrameTime on D3D11 measures the same span. Results are read without blocking a few frames
/// later. The render-thread callback calls no Unity API and allocates nothing per frame. Every D3D object is
/// released on the render thread when the clock is disposed.
/// The main-thread time runs from the same frame start to end of frame, so it leaves out the wait for the last
/// present.
/// </summary>
internal sealed class FrameClock : IDisposable
{
    private const int KindInit = 0;
    private const int KindStart = 1;
    private const int KindEnd = 2;
    private const int KindRelease = 3;
    private const int FrameMask = 0x0FFFFFFF;
    private const int Sets = 16;
    private const int Ring = 2048;

    private const int StateNone = 0;
    private const int StateReady = 1;
    private const int StateFailed = -1;
    private const int StateReleased = 2;

    private const int SetFree = 0;
    private const int SetBegun = 1;
    private const int SetEnded = 2;

    private const int QueryTimestamp = 2;
    private const int QueryTimestampDisjoint = 3;
    private const uint GetDataDoNotFlush = 1;

    private static readonly Guid DeviceIid = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly RenderEvent Callback = OnRenderEvent;
    private static readonly IntPtr CallbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);
    private static FrameClock? _current;

    private readonly IntPtr[] _disjoint = new IntPtr[Sets];
    private readonly IntPtr[] _begin = new IntPtr[Sets];
    private readonly IntPtr[] _end = new IntPtr[Sets];
    private readonly int[] _setState = new int[Sets];
    private readonly int[] _setFrame = new int[Sets];
    private readonly double[] _gpuMs = new double[Ring];
    private readonly int[] _gpuFrame = new int[Ring];
    private readonly double[] _cpuMs = new double[Ring];
    private readonly int[] _cpuFrame = new int[Ring];
    private readonly IntPtr _data = Marshal.AllocHGlobal(16);
    private readonly Texture2D _probe;
    private readonly IntPtr _resource;
    private readonly GameObject _holder;

    private IntPtr _device;
    private IntPtr _context;
    private Release? _release;
    private QueryCall? _beginCall;
    private QueryCall? _endCall;
    private GetData? _getData;
    private volatile int _state;
    private volatile string _failure = "";
    private int _dropped;
    private int _disjointFrames;
    private int _frame;
    private int _startedFrame = -1;
    private long _frameStart;
    private bool _disposed;

    private FrameClock()
    {
        for (var i = 0; i < Ring; i++)
        {
            _gpuFrame[i] = -1;
            _cpuFrame[i] = -1;
        }

        for (var i = 0; i < Sets; i++)
            _setFrame[i] = -1;
        _probe = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        _probe.Apply(false, true);
        _resource = _probe.GetNativeTexturePtr();
        _holder = new GameObject("Lattice.Live.FrameClock");
        Object.DontDestroyOnLoad(_holder);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RenderEvent(int eventId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterface(IntPtr self, ref Guid iid, out IntPtr found);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetDevice(IntPtr self, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateQuery(IntPtr device, ref QueryDesc desc, out IntPtr query);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFeatureLevel(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetImmediateContext(IntPtr device, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void QueryCall(IntPtr context, IntPtr query);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetData(IntPtr context, IntPtr query, IntPtr data, uint size, uint flags);

    /// <summary>The frame now running, as the clock counts frames.</summary>
    public int Frame => _frame;

    /// <summary>Frames whose queries were still in flight when their set came round again.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>Frames the GPU reported as disjoint (clock changed mid-frame): no time for them.</summary>
    public int DisjointFrames => Volatile.Read(ref _disjointFrames);

    /// <summary>Opens the clock: the D3D11 device and queries are made on the render thread, then the frame hooks start.</summary>
    public static IEnumerator Open(Action<Result<FrameClock, string>> done)
    {
        if (_current != null)
        {
            done(Result<FrameClock, string>.Error("a frame clock is already open"));
            yield break;
        }

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
        {
            done(Result<FrameClock, string>.Error($"the device is {SystemInfo.graphicsDeviceType}, not Direct3D11"));
            yield break;
        }

        var clock = new FrameClock();
        if (clock._resource == IntPtr.Zero)
        {
            clock.Dispose();
            done(Result<FrameClock, string>.Error("the probe texture has no native pointer"));
            yield break;
        }

        _current = clock;
        GL.IssuePluginEvent(CallbackPointer, KindInit);
        var deadline = Time.realtimeSinceStartup + 10f;
        while (clock._state == StateNone && Time.realtimeSinceStartup < deadline)
            yield return null;
        if (clock._state != StateReady)
        {
            var why = clock._state == StateNone ? "the render thread never ran the init event" : clock._failure;
            clock.Dispose();
            done(Result<FrameClock, string>.Error(why));
            yield break;
        }

        Object.Destroy(clock._probe);
        clock.Hook();
        clock._holder.AddComponent<EndOfFrame>().Clock = clock;
        done(Result<FrameClock, string>.Ok(clock));
    }

    /// <summary>A frame's main-thread and GPU times, once both have come back.</summary>
    public bool TryRead(int frame, out double cpuMs, out double gpuMs)
    {
        var slot = frame % Ring;
        cpuMs = _cpuMs[slot];
        gpuMs = Volatile.Read(ref _gpuFrame[slot]) == frame ? _gpuMs[slot] : double.NaN;
        return _cpuFrame[slot] == frame && !double.IsNaN(gpuMs);
    }

    /// <summary>Whether the GPU time for the frame has come back (or never will: dropped or disjoint).</summary>
    public bool Settled(int frame) => Volatile.Read(ref _gpuFrame[frame % Ring]) == frame || _frame - frame > Sets;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unhook();
        if (_holder != null)
            Object.Destroy(_holder);
        if (_probe != null)
            Object.Destroy(_probe);
        if (_current == this && _state == StateReady)
        {
            // The release runs on the render thread after every event already queued; the memory goes once it has run.
            GL.IssuePluginEvent(CallbackPointer, KindRelease);
            ReleaseWatch.Start(this);
            return;
        }

        if (_current == this)
            _current = null;
        Marshal.FreeHGlobal(_data);
    }

    private void Hook()
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        var systems = loop.subSystemList;
        for (var i = 0; i < systems.Length; i++)
        {
            if (systems[i].type != typeof(UnityEngine.PlayerLoop.Initialization))
                continue;
            var inner = systems[i].subSystemList ?? Array.Empty<PlayerLoopSystem>();
            var grown = new PlayerLoopSystem[inner.Length + 1];
            grown[0] = new PlayerLoopSystem { type = typeof(FrameClock), updateDelegate = FrameStart };
            Array.Copy(inner, 0, grown, 1, inner.Length);
            systems[i].subSystemList = grown;
            PlayerLoop.SetPlayerLoop(loop);
            return;
        }

        throw new InvalidOperationException("the player loop has no Initialization step");
    }

    private static void Unhook()
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        var systems = loop.subSystemList;
        for (var i = 0; i < systems.Length; i++)
        {
            var inner = systems[i].subSystemList;
            if (inner == null || inner.Length == 0 || inner[0].type != typeof(FrameClock))
                continue;
            var shrunk = new PlayerLoopSystem[inner.Length - 1];
            Array.Copy(inner, 1, shrunk, 0, shrunk.Length);
            systems[i].subSystemList = shrunk;
            PlayerLoop.SetPlayerLoop(loop);
            return;
        }
    }

    private static void FrameStart()
    {
        if (_current is not { _state: StateReady } clock)
            return;
        clock._frame = (clock._frame + 1) & FrameMask;
        clock._frameStart = Stopwatch.GetTimestamp();
        clock._startedFrame = clock._frame;
        GL.IssuePluginEvent(CallbackPointer, (clock._frame << 2) | KindStart);
    }

    private void FrameEnd()
    {
        if (_state != StateReady || _startedFrame != _frame)
            return;
        var slot = _frame % Ring;
        _cpuMs[slot] = (Stopwatch.GetTimestamp() - _frameStart) * 1000.0 / Stopwatch.Frequency;
        _cpuFrame[slot] = _frame;
        _startedFrame = -1;
        GL.IssuePluginEvent(CallbackPointer, (_frame << 2) | KindEnd);
    }

    // Render thread: no Unity API, no allocation after the init event.
    private static void OnRenderEvent(int eventId)
    {
        var clock = _current;
        if (clock == null)
            return;
        try
        {
            var kind = eventId & 3;
            var frame = (eventId >> 2) & FrameMask;
            switch (kind)
            {
                case KindInit:
                    clock.Init();
                    break;
                case KindStart when clock._state == StateReady:
                    clock.Begin(frame);
                    break;
                case KindEnd when clock._state == StateReady:
                    clock.End(frame);
                    break;
                case KindRelease:
                    clock.ReleaseAll();
                    break;
            }
        }
        catch (Exception e)
        {
            clock._failure = e.Message;
            clock._state = StateFailed;
        }
    }

    private void Init()
    {
        Fn<GetDevice>(_resource, 3)(_resource, out var device);
        if (device == IntPtr.Zero)
        {
            Fail("the texture gave no device");
            return;
        }

        _device = device;
        _release = Fn<Release>(device, 2);
        var iid = DeviceIid;
        if (Fn<QueryInterface>(device, 0)(device, ref iid, out var asDevice) != 0 || asDevice == IntPtr.Zero)
        {
            Fail("the texture's device is not an ID3D11Device");
            return;
        }

        _release(asDevice);
        var level = Fn<GetFeatureLevel>(device, 37)(device);
        if (level < 0x9100 || level > 0xc200)
        {
            Fail($"the device reports feature level 0x{level:x}");
            return;
        }

        Fn<GetImmediateContext>(device, 40)(device, out var context);
        if (context == IntPtr.Zero)
        {
            Fail("the device gave no immediate context");
            return;
        }

        _context = context;
        var create = Fn<CreateQuery>(device, 24);
        var disjoint = new QueryDesc(QueryTimestampDisjoint);
        var stamp = new QueryDesc(QueryTimestamp);
        for (var i = 0; i < Sets; i++)
        {
            if (create(device, ref disjoint, out _disjoint[i]) != 0 || create(device, ref stamp, out _begin[i]) != 0 || create(device, ref stamp, out _end[i]) != 0)
            {
                Fail($"CreateQuery failed for set {i}");
                return;
            }
        }

        _beginCall = Fn<QueryCall>(context, 27);
        _endCall = Fn<QueryCall>(context, 28);
        _getData = Fn<GetData>(context, 29);
        _state = StateReady;
    }

    private void Begin(int frame)
    {
        var set = frame % Sets;
        if (_setState[set] == SetBegun)
        {
            // A frame that started and never ended: close its queries and let the result go.
            _endCall!(_context, _end[set]);
            _endCall(_context, _disjoint[set]);
            _setFrame[set] = -1;
            _setState[set] = SetEnded;
        }

        if (_setState[set] == SetEnded)
            Collect(set);
        if (_setState[set] != SetFree)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _beginCall!(_context, _disjoint[set]);
        _endCall!(_context, _begin[set]);
        _setFrame[set] = frame;
        _setState[set] = SetBegun;
    }

    private void End(int frame)
    {
        var set = frame % Sets;
        if (_setState[set] == SetBegun && _setFrame[set] == frame)
        {
            _endCall!(_context, _end[set]);
            _endCall(_context, _disjoint[set]);
            _setState[set] = SetEnded;
        }

        for (var i = 0; i < Sets; i++)
            if (_setState[i] == SetEnded)
                Collect(i);
    }

    private void Collect(int set)
    {
        if (_getData!(_context, _disjoint[set], _data, 16, GetDataDoNotFlush) != 0)
            return;
        var frequency = Marshal.ReadInt64(_data, 0);
        var disjoint = Marshal.ReadInt32(_data, 8);
        if (_getData(_context, _begin[set], _data, 8, GetDataDoNotFlush) != 0)
            return;
        var t0 = Marshal.ReadInt64(_data, 0);
        if (_getData(_context, _end[set], _data, 8, GetDataDoNotFlush) != 0)
            return;
        var t1 = Marshal.ReadInt64(_data, 0);
        var frame = _setFrame[set];
        _setState[set] = SetFree;
        _setFrame[set] = -1;
        if (frame < 0)
            return;
        if (disjoint != 0 || frequency <= 0)
        {
            Interlocked.Increment(ref _disjointFrames);
            return;
        }

        var slot = frame % Ring;
        _gpuMs[slot] = (t1 - t0) * 1000.0 / frequency;
        Volatile.Write(ref _gpuFrame[slot], frame);
    }

    private void ReleaseAll()
    {
        if (_release != null)
        {
            for (var i = 0; i < Sets; i++)
            {
                ReleaseOne(ref _disjoint[i]);
                ReleaseOne(ref _begin[i]);
                ReleaseOne(ref _end[i]);
            }

            ReleaseOne(ref _context);
            ReleaseOne(ref _device);
        }

        _state = StateReleased;
    }

    private void ReleaseOne(ref IntPtr unknown)
    {
        if (unknown == IntPtr.Zero)
            return;
        _release!(unknown);
        unknown = IntPtr.Zero;
    }

    private void Fail(string why)
    {
        _failure = why;
        ReleaseAll();
        _state = StateFailed;
    }

    private static T Fn<T>(IntPtr unknown, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(unknown), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct QueryDesc(int query)
    {
        public readonly int Query = query;
        public readonly uint MiscFlags = 0;
    }

    private sealed class EndOfFrame : MonoBehaviour
    {
        public FrameClock? Clock { get; set; }

        private IEnumerator Start()
        {
            var end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                Clock?.FrameEnd();
            }
        }
    }

    // Frees the clock's memory once the render thread has run its release, then lets the next clock open.
    private sealed class ReleaseWatch : MonoBehaviour
    {
        private FrameClock? _clock;
        private float _deadline;

        public static void Start(FrameClock clock)
        {
            var holder = new GameObject("Lattice.Live.FrameClock.Release");
            DontDestroyOnLoad(holder);
            var watch = holder.AddComponent<ReleaseWatch>();
            watch._clock = clock;
            watch._deadline = Time.realtimeSinceStartup + 10f;
        }

        private void Update()
        {
            if (_clock == null)
                return;
            if (_clock._state != StateReleased && Time.realtimeSinceStartup < _deadline)
                return;
            if (_clock._state == StateReleased)
                Marshal.FreeHGlobal(_clock._data);
            if (_current == _clock)
                _current = null;
            _clock = null;
            Destroy(gameObject);
        }
    }
}
