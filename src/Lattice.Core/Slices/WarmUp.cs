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

    /// <summary>A compiling step ends once it has taken this long (0.25 ms), even before <see cref="PerStep"/> methods.</summary>
    private static readonly long StepTicks = System.Diagnostics.Stopwatch.Frequency / 4000;

    private readonly WarmedSets _sets;
    private readonly string _set;
    private readonly FrameScheduler _scheduler;
    private readonly Queue<Type> _types;
    private readonly Queue<MethodBase> _methods = new();
    private readonly HashSet<Type> _closed = new();
    private Type? _listing;
    private MethodBase[] _members = [];
    private int _member;

    private WarmUp(WarmedSets sets, string set, FrameScheduler scheduler, Queue<Type> types)
    {
        _sets = sets;
        _set = set;
        _scheduler = scheduler;
        _types = types;
    }

    /// <summary>Where the warm-up is: types and methods still to go, methods compiled.</summary>
    public override string ToString() => $"{_set}: {_types.Count} types and {_methods.Count} methods to go, {Compiled} compiled";

    /// <summary>Methods this warm-up has compiled so far.</summary>
    public int Compiled { get; private set; }

    /// <summary>
    /// Queues the warm-up of the types of <paramref name="assembly"/> whose namespace is one of
    /// <paramref name="namespaces"/>, unless the set is done. Returns the job, none when the set was done, the kind is unknown or the types could not be listed; it never throws.
    /// </summary>
    public static Option<SlicedJob> Schedule(WarmedSets sets, FrameScheduler scheduler, string set, Assembly assembly, string[]? namespaces)
    {
        try
        {
            if (sets.IsDone(set))
                return Option<SlicedJob>.None;
            var types = new Queue<Type>();
            foreach (var type in LoadableTypes(assembly))
                if (type.DeclaringType is null && InNamespaces(type, namespaces))
                    types.Enqueue(type);
            return scheduler.Schedule(WorkKind.Drawing, new WarmUp(sets, set, scheduler, types), Priority.Lowest).TryGet(out var job)
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
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var taken = 0; taken < PerStep && _methods.Count > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - start < StepTicks; taken++)
                Compile(_methods.Dequeue());
            return StepOutcome.More;
        }

        if (_types.Count > 0)
        {
            Expand(_types.Dequeue());
            return StepOutcome.More;
        }

        _sets.Add(_set);
        return StepOutcome.Done;
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
        for (var taken = 0; taken < PerStep && _member < _members.Length; taken++)
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

        if (valued && _closed.Add(type))
            _types.Enqueue(type);
    }

    private void Compile(MethodBase method)
    {
        try
        {
            if (method.GetMethodBody()?.GetILAsByteArray()?.Length > LargestMethod)
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
}
