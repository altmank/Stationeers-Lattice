using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lattice.Api;

namespace Lattice.Core.Slices;

/// <summary>
/// Makes the runtime compile Lattice's own methods before a big plan needs them: the first call of a method is its
/// compile, and a plan of thousands of ghosts reaches hundreds of cold methods inside one slice. One method is
/// compiled per step, from the types of the given namespaces (nested types, closures and compiler-generated types
/// included; open generics, abstract and extern methods left out), at the lowest priority and only while the frame's other
/// work took next to no time, so a step is never taken from real work. Each set is done once per <see cref="WarmedSets"/>,
/// which the host keeps for the whole process: compiled methods stay compiled for later worlds. A method that cannot be compiled is skipped.
/// <para>
/// Both calls are made because the runtimes differ: Unity's Mono compiles on <c>RuntimeMethodHandle.GetFunctionPointer</c>
/// (its <c>RuntimeHelpers.PrepareMethod</c> does nothing), .NET compiles on <c>PrepareMethod</c> (its function pointer
/// is a stub).
/// </para>
/// </summary>
internal sealed class WarmUp : ISlicedWork
{
    /// <summary>The frame's real work below which the world counts as idle.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromTicks(10000);

    /// <summary>Methods with more IL bytes than this are left to compile when first called: one step would pass its slice.</summary>
    private const int LargestMethod = 1000;

    /// <summary>The most methods one step takes on, listing or compiling.</summary>
    private const int PerStep = 8;

    /// <summary>A step ends once it has taken this long (0.25 ms), even before <see cref="PerStep"/> methods.</summary>
    private static readonly TimeSpan StepLimit = TimeSpan.FromTicks(2500);

    /// <summary>A method expected to take longer than this (3 ms, margin included) to compile is left to first call: a deferred method compiles inside the plan's arrival, which costs more than an idle frame up to the frame budgets' sum.</summary>
    private static readonly TimeSpan MethodLimit = TimeSpan.FromTicks(30000);

    /// <summary>The most one compile teaches the cost model: a cold start or a collection can stretch a compile far past its cost, and one such sample must not make every method look too dear.</summary>
    private static readonly TimeSpan MaxSample = MethodLimit;

    /// <summary>Compile cost expected per IL byte before any has been measured (2 µs, in ticks of 100 ns): the runtime's real cost is learned from the methods compiled.</summary>
    private const double PriorTicksPerByte = 20;

    /// <summary>A compile shorter than this (5 Âµs) says nothing about the cost of compiling by size.</summary>
    private static readonly TimeSpan Measurable = TimeSpan.FromTicks(50);

    /// <summary>The margin on the learned cost per byte: size predicts cost only roughly, and one overrun is a hitch.</summary>
    private const double Margin = 2;

    /// <summary>IL bytes added to every method's size when predicting: a compile has a cost of its own.</summary>
    private const int FixedBytes = 16;

    private readonly WarmedSets _sets;
    private readonly string _set;
    private readonly FrameScheduler _scheduler;
    private readonly Queue<Type> _types;
    private readonly Queue<MethodBase> _methods = new();
    private readonly List<(MethodBase Method, int Size, bool Valued)> _deferred = new();
    private readonly IClock _clock;
    private readonly Action<MethodBase, int> _compile;
    private bool _retried;
    private MethodBase? _last;
    private readonly CostModel _plain = new();
    private readonly CostModel _valued = new();
    private readonly HashSet<Type> _closed = new();
    private readonly HashSet<Type> _shared = new();
    private Type? _listing;
    private Type? _expanding;
    private int _stage;
    private List<MethodBase> _building = new();
    private MethodBase[] _members = [];
    private int _member;

    private WarmUp(WarmedSets sets, string set, FrameScheduler scheduler, Queue<Type> types, Action<MethodBase, int>? compile)
    {
        _sets = sets;
        _set = set;
        _scheduler = scheduler;
        _types = types;
        _clock = scheduler.Clock;
        _compile = compile ?? Compile;
    }

    /// <summary>Where the warm-up is: types and methods still to go, methods compiled.</summary>
    public override string ToString() => $"{_set}: {_types.Count} types and {_methods.Count} methods to go, {Compiled} compiled, {Skipped} too dear ({SkippedValued} on value-type generics), last {_last?.DeclaringType?.FullName}.{_last?.Name}";

    /// <summary>Methods this warm-up has compiled so far.</summary>
    public int Compiled { get; private set; }

    /// <summary>Methods left to compile when first called, because compiling them was expected to take longer than a frame can spare.</summary>
    public int Skipped { get; private set; }

    /// <summary>Of <see cref="Skipped"/>, the methods of generic types closed over value types, which the runtime may compile far slower than the rest.</summary>
    public int SkippedValued { get; private set; }

    /// <summary>
    /// Queues the warm-up of the types of <paramref name="assembly"/> whose namespace is one of
    /// <paramref name="namespaces"/>, unless the set is done. Returns the job, none when the set was done, the kind is unknown or the types could not be listed; it never throws.
    /// </summary>
    public static Option<SlicedJob> Schedule(WarmedSets sets, FrameScheduler scheduler, string set, Assembly assembly, string[]? namespaces) =>
        Schedule(sets, scheduler, set, assembly, namespaces, null);

    /// <summary>As above, compiling each method (given with its IL size) by <paramref name="compile"/> instead of by the runtime.</summary>
    internal static Option<SlicedJob> Schedule(WarmedSets sets, FrameScheduler scheduler, string set, Assembly assembly, string[]? namespaces, Action<MethodBase, int>? compile)
    {
        try
        {
            if (sets.IsDone(set))
                return Option<SlicedJob>.None;
            var types = new Queue<Type>();
            foreach (var type in LoadableTypes(assembly))
                if (type.DeclaringType is null && InNamespaces(type, namespaces))
                    types.Enqueue(type);
            return scheduler.Schedule(WorkKind.Drawing, new WarmUp(sets, set, scheduler, types, compile), Priority.Lowest).TryGet(out var job)
                ? Option.Some(job)
                : Option<SlicedJob>.None;
        }
        catch (Exception)
        {
            // Warm-up is an optimisation: whatever reflection or the runtime throws here (a missing file, a bad image, a runtime quirk), the world must still start and publish, so it is not scheduled.
            return Option<SlicedJob>.None;
        }
    }

    public StepOutcome Step()
    {
        if (_scheduler.WorkThisFrame > Quiet)
            return StepOutcome.NextFrame;
        if (_expanding is not null)
        {
            ExpandStage();
            return StepOutcome.More;
        }

        if (_listing is not null)
        {
            List();
            return StepOutcome.More;
        }

        if (_methods.Count > 0)
        {
            CompileSome();
            return StepOutcome.More;
        }

        if (_types.Count > 0)
        {
            Expand(_types.Dequeue());
            return StepOutcome.More;
        }

        if (_deferred.Count > 0 && !_retried)
        {
            _retried = true;
            foreach (var deferred in _deferred)
                _methods.Enqueue(deferred.Method);
            _deferred.Clear();
            return StepOutcome.More;
        }

        _sets.Add(_set);
        _sets.Log($"Lattice warm-up {_set}: {Compiled} methods compiled; left to first call: {Skipped} expected to compile too slowly, {SkippedValued} of them on generic types closed over value types.");
        return StepOutcome.Done;
    }

    // A step compiles methods while the time it has taken, plus the cost expected of the next, fits the step limit. A method
    // expected to cost more than a frame can spare is deferred once, judged again at the end by what has been learned, then left out.
    private void CompileSome()
    {
        var start = _clock.Now;
        for (var taken = 0; taken < PerStep && _methods.Count > 0; taken++)
        {
            var spent = _clock.Now - start;
            if (spent >= StepLimit)
                return;
            var method = _methods.Peek();
            var size = SizeOf(method);
            var valued = IsValued(method);
            var model = valued ? _valued : _plain;
            var expected = model.Expected(size);
            if (taken > 0 && spent + expected > StepLimit)
                return;
            _methods.Dequeue();
            if (size < 0)
                continue;
            if (expected <= MethodLimit)
            {
                CompileOne(method, size, model);
                continue;
            }

            model.Deferred();
            if (_retried)
            {
                Skipped++;
                if (valued)
                    SkippedValued++;
            }
            else
            {
                _deferred.Add((method, size, valued));
            }

            if (model.ShouldProbe() && SmallestDeferred(valued) is var at && at >= 0 && model.Expected(_deferred[at].Size) <= MethodLimit)
            {
                var probe = _deferred[at];
                _deferred.RemoveAt(at);
                model.Probed();
                CompileOne(probe.Method, probe.Size, model);
            }
        }
    }

    private void CompileOne(MethodBase method, int size, CostModel model)
    {
        var before = _clock.Now;
        _last = method;
        _compile(method, size);
        if (size <= LargestMethod)
            model.Learn(size, _clock.Now - before);
    }

    // The deferred method of one kind with the smallest IL size, so the lowest cost expected; -1 when none.
    private int SmallestDeferred(bool valued)
    {
        var at = -1;
        for (var i = 0; i < _deferred.Count; i++)
            if (_deferred[i].Valued == valued && (at < 0 || _deferred[i].Size < _deferred[at].Size))
                at = i;
        return at;
    }

    private static int SizeOf(MethodBase method)
    {
        try
        {
            return method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    // Methods of a generic type closed over a value type get their own compile cost: the runtime compiles each such instantiation in full, and may do it far slower than the rest.
    private static bool IsValued(MethodBase method)
    {
        if (method.DeclaringType is not { IsGenericType: true } type)
            return false;
        foreach (var argument in type.GetGenericArguments())
            if (argument.IsValueType)
                return true;
        return false;
    }

    /// <summary>
    /// What compiling costs for one kind of method, learned from the compiles that took measurable time (a method already compiled,
    /// or left uncompiled for its size, costs next to nothing and would pull the cost to zero): a decayed fit of the cost by IL size
    /// (a fixed cost per method plus a cost per byte), by cost per byte alone until enough have been seen or when the fit is not sound.
    /// </summary>
    internal sealed class CostModel
    {
        private const int Enough = 8;

        /// <summary>Deferrals in a row after which one method is compiled anyway: deferred methods teach nothing, so a model gone pessimistic would never recover.</summary>
        private const int ProbeAfter = 4;

        /// <summary>The most deferrals in a row between probes once probes keep finding the kind slow (4, 8, then 16): a kind that is really slow is probed ever more rarely.</summary>
        private const int MostProbeAfter = 16;

        /// <summary>The least spread of IL sizes (a standard deviation of 20 bytes, squared) at which a fitted slope means anything.</summary>
        private const double LeastVariance = 400;

        private int _streak;
        private int _probeAfter = ProbeAfter;
        private double _n, _sx, _sy, _sxx, _sxy;
        private double _ticks = 1000 * PriorTicksPerByte;
        private double _bytes = 1000;

        /// <summary>The cost expected of a method of this IL size, the margin included.</summary>
        public TimeSpan Expected(int size)
        {
            var proportional = (size + FixedBytes) * _ticks / _bytes * Margin;
            if (_n >= Enough)
            {
                var mx = _sx / _n;
                var my = _sy / _n;
                var variance = _sxx / _n - mx * mx;
                if (variance >= LeastVariance)
                {
                    var perByte = (_sxy / _n - mx * my) / variance;
                    var fixedCost = my - perByte * mx;
                    if (perByte > 0 && fixedCost >= 0)
                        return TimeSpan.FromTicks((long)Math.Min((fixedCost + perByte * size) * Margin, 2 * proportional));
                }
            }

            return TimeSpan.FromTicks((long)proportional);
        }

        /// <summary>Whether enough methods of this kind have been deferred in a row for one of them to be compiled, to learn from it.</summary>
        public bool ShouldProbe() => _streak >= _probeAfter;

        /// <summary>A probe was taken: the deferrals in a row start again, and the next probe waits twice as long, up to <see cref="MostProbeAfter"/>.</summary>
        public void Probed()
        {
            _streak = 0;
            _probeAfter = Math.Min(2 * _probeAfter, MostProbeAfter);
        }

        public void Deferred() => _streak++;

        public void Learn(int size, TimeSpan cost)
        {
            if (cost < Measurable)
                return;
            const double decay = 0.98;
            if (cost <= MethodLimit)
            {
                _streak = 0;
                _probeAfter = ProbeAfter;
            }

            var ticks = (double)Math.Min(cost.Ticks, MaxSample.Ticks);
            _n = _n * decay + 1;
            _sx = _sx * decay + size;
            _sy = _sy * decay + ticks;
            _sxx = _sxx * decay + (double)size * size;
            _sxy = _sxy * decay + size * ticks;
            _ticks = _ticks * decay + ticks;
            _bytes = _bytes * decay + size + FixedBytes;
        }
    }

    // A type is taken in steps: one reflection call each (its methods, its constructors, its nested types, its fields), then its members are queued and noted at most PerStep each step.
    private void Expand(Type type)
    {
        if (type.ContainsGenericParameters || type.IsInterface || type.IsEnum || typeof(Delegate).IsAssignableFrom(type))
            return;
        _expanding = type;
        _stage = 0;
        _building = new List<MethodBase>();
    }

    private void ExpandStage()
    {
        var type = _expanding!;
        try
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            switch (_stage++)
            {
                case 0:
                    foreach (var method in type.GetMethods(all))
                        if (!method.IsAbstract && !method.ContainsGenericParameters && (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) == 0)
                            _building.Add(method);
                    return;
                case 1:
                    _building.AddRange(type.GetConstructors(all));
                    return;
                case 2:
                    foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                        _types.Enqueue(nested);
                    return;
                default:
                    if (!type.IsGenericType)
                        foreach (var field in type.GetFields(all))
                            Note(field.FieldType);
                    _members = _building.ToArray();
                    _member = 0;
                    _listing = type;
                    _expanding = null;
                    return;
            }
        }
        catch (Exception)
        {
            // One type that cannot be listed (a missing dependency, a load failure) is skipped; the rest of the set goes on.
            _expanding = null;
            _listing = null;
        }
    }

    private void List()
    {
        var own = !_listing!.IsGenericType;
        var start = _clock.Now;
        for (var taken = 0; taken < PerStep && _member < _members.Length && (taken == 0 || _clock.Now - start < StepLimit); taken++)
        {
            var member = _members[_member++];
            _methods.Enqueue(member);
            if (!own)
                continue;
            try
            {
                if (member is MethodInfo info)
                    Note(info.ReturnType);
                foreach (var parameter in member.GetParameters())
                    Note(parameter.ParameterType);
                if (member.GetMethodBody() is { } body)
                    foreach (var local in body.LocalVariables)
                        Note(local.LocalType);
            }
            catch (Exception)
            {
                // A signature or body that cannot be read (a missing dependency) only costs its generic instantiations; the method itself is still queued.
            }
        }

        if (_member >= _members.Length)
            _listing = null;
    }

    // A generic type closed over a value type is compiled per instantiation, so the ones Lattice's own signatures and fields
    // name (a dictionary keyed by an id, a list of cells) are queued to be compiled as types, their methods one by one.
    private void Note(Type type)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            Note(type.GetElementType()!);
            return;
        }

        if (!type.IsGenericType || type.ContainsGenericParameters)
            return;
        var arguments = type.GetGenericArguments();
        var valued = false;
        foreach (var argument in arguments)
        {
            valued |= argument.IsValueType;
            Note(argument);
        }

        if (Warmed(type) && (valued ? _closed : _shared).Add(type))
            _types.Enqueue(type);
    }

    // A generic type is warmed only when its definition is Lattice's or the base library's; the game's, Unity's and other mods' are not Lattice's to compile.
    // A base-library generic with an enum among its type arguments is left to its first call: on Mono one such compile can pull in a cascade of other
    // instantiations (HashSet<GasKind>.TryGetValue took 28 ms in game), and neither IL size nor a learned cost predicts it.
    private static bool Warmed(Type type)
    {
        try
        {
            var name = type.GetGenericTypeDefinition().Assembly.GetName().Name ?? "";
            if (name.StartsWith("Lattice", StringComparison.Ordinal))
                return true;
            return (name.StartsWith("System", StringComparison.Ordinal) || name is "mscorlib" or "netstandard") && !OverEnum(type);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool OverEnum(Type type)
    {
        if (type.IsEnum)
            return true;
        if (type.HasElementType)
            return OverEnum(type.GetElementType()!);
        if (!type.IsGenericType)
            return false;
        foreach (var argument in type.GetGenericArguments())
            if (OverEnum(argument))
                return true;
        return false;
    }

    private void Compile(MethodBase method, int size)
    {
        try
        {
            if (size > LargestMethod)
                return;
            var handle = method.MethodHandle;
            RuntimeHelpers.PrepareMethod(handle);
            handle.GetFunctionPointer();
            Compiled++;
        }
        catch (Exception)
        {
            // One method that cannot be compiled (invalid program, missing method, access or type-initialisation failure) is skipped; the rest of the set goes on.
        }
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            var loaded = new List<Type>(error.Types.Length);
            foreach (var type in error.Types)
                if (type is not null)
                    loaded.Add(type);
            return loaded;
        }
    }

    private static bool InNamespaces(Type type, string[]? namespaces)
    {
        if (namespaces is null)
            return type.Namespace?.StartsWith("Lattice", StringComparison.Ordinal) == true;
        var name = type.Namespace ?? "";
        foreach (var space in namespaces)
            if (name == space)
                return true;
        return false;
    }
}

/// <summary>The warm-up sets already compiled; the host holds one for its whole process, because compiled methods outlive worlds.</summary>
internal sealed class WarmedSets
{
    private readonly HashSet<string> _done = new();

    /// <summary>Whether a warm-up completed the set.</summary>
    public bool IsDone(string set)
    {
        lock (_done)
            return _done.Contains(set);
    }

    internal void Add(string set)
    {
        lock (_done)
            _done.Add(set);
    }

    /// <summary>Where a finished set's summary goes; set once by the host.</summary>
    internal Action<string> Log { get; set; } = static _ => { };
}
