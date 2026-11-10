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

    /// <summary>A method expected to take longer than this (0.5 ms) to compile is not compiled: nothing could keep it from overrunning the frame.</summary>
    private static readonly TimeSpan MethodLimit = TimeSpan.FromTicks(5000);

    /// <summary>Compile cost expected per IL byte before any has been measured (2 µs, in ticks of 100 ns): the runtime's real cost is learned from the methods compiled.</summary>
    private const double PriorTicksPerByte = 20;

    /// <summary>The margin on the learned cost per byte: size predicts cost only roughly, and one overrun is a hitch.</summary>
    private const double Margin = 2;

    /// <summary>IL bytes added to every method's size when predicting: a compile has a cost of its own.</summary>
    private const int FixedBytes = 16;

    private readonly WarmedSets _sets;
    private readonly string _set;
    private readonly FrameScheduler _scheduler;
    private readonly Queue<Type> _types;
    private readonly Queue<MethodBase> _methods = new();
    private readonly Queue<MethodBase> _deferred = new();
    private readonly IClock _clock;
    private readonly Action<MethodBase, int> _compile;
    private bool _retried;
    private MethodBase? _last;
    private double _learnedTicks = 1000 * PriorTicksPerByte;
    private double _learnedBytes = 1000;
    private readonly HashSet<Type> _closed = new();
    private readonly Queue<Type> _counting = new();
    private readonly HashSet<Type> _shared = new();
    private Type? _listing;
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
    public override string ToString() => $"{_set}: {_types.Count} types and {_methods.Count} methods to go, {Compiled} compiled, {Skipped} too dear, {ValueGenerics} on value-type generics left out, last {_last?.DeclaringType?.FullName}.{_last?.Name}";

    /// <summary>Methods this warm-up has compiled so far.</summary>
    public int Compiled { get; private set; }

    /// <summary>Methods left to compile when first called, because compiling them was expected to take longer than a frame can spare.</summary>
    public int Skipped { get; private set; }

    /// <summary>Methods of generic types closed over value types, left to compile when first called: the runtime compiles each instantiation slowly and unpredictably.</summary>
    public int ValueGenerics { get; private set; }

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

        if (_counting.Count > 0)
        {
            for (var taken = 0; taken < PerStep && _counting.Count > 0; taken++)
                ValueGenerics += CountMethods(_counting.Dequeue());
            return StepOutcome.More;
        }

        if (_deferred.Count > 0 && !_retried)
        {
            _retried = true;
            while (_deferred.Count > 0)
                _methods.Enqueue(_deferred.Dequeue());
            return StepOutcome.More;
        }

        _sets.Add(_set);
        _sets.Log($"Lattice warm-up {_set}: {Compiled} methods compiled; left to first call: {Skipped} expected to compile too slowly, {ValueGenerics} on {_closed.Count} generic types closed over value types.");
        return StepOutcome.Done;
    }

    // The methods a closed generic type would have had compiled: the declared methods and constructors that are not abstract or extern.
    private static int CountMethods(Type type)
    {
        try
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var count = type.GetConstructors(all).Length;
            foreach (var method in type.GetMethods(all))
                if (!method.IsAbstract && !method.ContainsGenericParameters && (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) == 0)
                    count++;
            return count;
        }
        catch (Exception)
        {
            // A type that cannot be listed (a missing dependency) counts for nothing.
            return 0;
        }
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
            var expected = Expected(size);
            if (taken > 0 && spent + expected > StepLimit)
                return;
            _methods.Dequeue();
            if (size < 0)
                continue;
            if (expected > MethodLimit)
            {
                if (_retried)
                    Skipped++;
                else
                    _deferred.Enqueue(method);
                continue;
            }

            var before = _clock.Now;
            _last = method;
            _compile(method, size);
            Learn(size, _clock.Now - before);
        }
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

    private TimeSpan Expected(int size) =>
        TimeSpan.FromTicks((long)((size + FixedBytes) * _learnedTicks / _learnedBytes * Margin));

    private void Learn(int size, TimeSpan cost)
    {
        _learnedTicks = _learnedTicks * 0.98 + cost.Ticks;
        _learnedBytes = _learnedBytes * 0.98 + size + FixedBytes;
    }

    // A type is taken in steps: its members are listed in one, then at most PerStep of them are queued and noted each step.
    private void Expand(Type type)
    {
        if (type.ContainsGenericParameters || type.IsInterface || type.IsEnum || typeof(Delegate).IsAssignableFrom(type))
            return;
        try
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var members = new List<MethodBase>();
            foreach (var method in type.GetMethods(all))
                if (!method.IsAbstract && !method.ContainsGenericParameters && (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) == 0)
                    members.Add(method);
            members.AddRange(type.GetConstructors(all));
            _members = members.ToArray();
            _member = 0;
            _listing = type;
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                _types.Enqueue(nested);
            if (!type.IsGenericType)
                foreach (var field in type.GetFields(all))
                    Note(field.FieldType);
        }
        catch (Exception)
        {
            // One type that cannot be listed (a missing dependency, a load failure) is skipped; the rest of the set goes on.
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

    // A generic type closed over a value type is compiled per instantiation, and Mono compiles each slowly and unpredictably, so the ones Lattice's own signatures and fields
    // name (a dictionary keyed by an id, a list of cells) are not compiled: they are counted, their methods left to first call.
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

        if (valued)
        {
            if (_closed.Add(type))
                _counting.Enqueue(type);
        }
        else if (_shared.Add(type))
        {
            _types.Enqueue(type);
        }
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
