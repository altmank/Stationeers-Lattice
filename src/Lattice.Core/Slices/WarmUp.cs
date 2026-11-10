using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Lattice.Core.Slices;

/// <summary>
/// Makes the runtime compile Lattice's own methods before a big plan needs them: the first call of a method is its
/// compile, and a plan of thousands of ghosts reaches hundreds of cold methods inside one slice. One method is
/// compiled per step, from the types of the given namespaces (nested types, closures and compiler-generated types
/// included; open generics, abstract and extern methods left out), at the lowest priority and only while the frame's other
/// work took next to no time, so a step is never taken from real work. Each set is done once per process: compiled
/// methods stay compiled for later worlds. A method that cannot be compiled is skipped.
/// <para>
/// Both calls are made because the runtimes differ: Unity's Mono compiles on <c>RuntimeMethodHandle.GetFunctionPointer</c>
/// (its <c>RuntimeHelpers.PrepareMethod</c> does nothing), .NET compiles on <c>PrepareMethod</c> (its function pointer
/// is a stub).
/// </para>
/// </summary>
internal sealed class WarmUp : ISlicedWork
{
    /// <summary>The frame's real work below which the world counts as idle.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(0.25);

    /// <summary>Methods with more IL bytes than this are left to compile when first called: one step would pass its slice.</summary>
    private const int LargestMethod = 3000;

    private static readonly HashSet<string> Finished = new();
    private readonly string _set;
    private readonly FrameScheduler _scheduler;
    private readonly Func<bool>? _busy;
    private readonly Queue<Type> _types;
    private readonly Queue<MethodBase> _methods = new();
    private readonly HashSet<Type> _closed = new();

    private WarmUp(string set, FrameScheduler scheduler, Queue<Type> types, Func<bool>? busy)
    {
        _set = set;
        _scheduler = scheduler;
        _types = types;
        _busy = busy;
    }

    /// <summary>Where the warm-up is: types and methods still to go, methods compiled.</summary>
    public override string ToString() => $"{_set}: {_types.Count} types and {_methods.Count} methods to go, {Compiled} compiled";

    /// <summary>Methods this warm-up has compiled so far.</summary>
    public int Compiled { get; private set; }

    /// <summary>Whether the set has been compiled completely by a warm-up of this process.</summary>
    public static bool IsDone(string set)
    {
        lock (Finished)
            return Finished.Contains(set);
    }

    /// <summary>Forgets that a set was compiled, so the next <see cref="Schedule"/> runs it again (tests).</summary>
    internal static void Forget(string set)
    {
        lock (Finished)
            Finished.Remove(set);
    }

    /// <summary>
    /// Queues the warm-up of the types of <paramref name="assembly"/> whose namespace is one of
    /// <paramref name="namespaces"/>, unless the set is done. <paramref name="busy"/> holds the warm-up back while work
    /// outside the scheduler's jobs is going on. Returns the job, none when the set was done or the kind is unknown.
    /// </summary>
    public static SlicedJob? Schedule(FrameScheduler scheduler, string set, Assembly assembly, string[]? namespaces, Func<bool>? busy = null)
    {
        if (IsDone(set))
            return null;
        var types = new Queue<Type>();
        foreach (var type in LoadableTypes(assembly))
            if (type.DeclaringType is null && InNamespaces(type, namespaces))
                types.Enqueue(type);
        return scheduler.Schedule(WorkKind.Drawing, new WarmUp(set, scheduler, types, busy), Priority.Lowest).TryGet(out var job) ? job : null;
    }

    public StepOutcome Step()
    {
        if (_scheduler.WorkThisFrame > Quiet || _busy?.Invoke() == true)
            return StepOutcome.NextFrame;
        if (_methods.Count > 0)
        {
            Compile(_methods.Dequeue());
            return StepOutcome.More;
        }

        if (_types.Count > 0)
        {
            Expand(_types.Dequeue());
            return StepOutcome.More;
        }

        lock (Finished)
            Finished.Add(_set);
        return StepOutcome.Done;
    }

    private void Expand(Type type)
    {
        if (type.ContainsGenericParameters || type.IsInterface || type.IsEnum || typeof(Delegate).IsAssignableFrom(type))
            return;
        var own = !type.IsGenericType;
        try
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all))
                if (!method.IsAbstract && !method.ContainsGenericParameters && (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) == 0)
                {
                    _methods.Enqueue(method);
                    if (own)
                        Note(method.ReturnType);
                    if (own)
                        foreach (var parameter in method.GetParameters())
                            Note(parameter.ParameterType);
                }

            foreach (var constructor in type.GetConstructors(all))
            {
                _methods.Enqueue(constructor);
                if (own)
                    foreach (var parameter in constructor.GetParameters())
                        Note(parameter.ParameterType);
            }

            if (own)
                foreach (var field in type.GetFields(all))
                    Note(field.FieldType);
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                _types.Enqueue(nested);
        }
        catch (Exception error) when (error is TypeLoadException or FileNotFoundException or FileLoadException or NotSupportedException or ReflectionTypeLoadException)
        {
        }
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
        catch (Exception error) when (error is TypeLoadException or FileNotFoundException or FileLoadException or NotSupportedException or ArgumentException or InvalidOperationException or BadImageFormatException)
        {
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
