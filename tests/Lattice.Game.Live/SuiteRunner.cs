using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Lattice.Tests.Contracts;

namespace Lattice.Game.Live;

/// <summary>
/// Runs the port suites in this assembly the way xunit would: a new instance per case, every [Fact] once, every
/// [Theory] once per row of its [MemberData], an IDisposable instance disposed after its case. One line per case.
/// </summary>
internal static class SuiteRunner
{
    private static readonly Type[] Suites = [typeof(WorldViewContract), typeof(PlacementCheckContract), typeof(WorldChangesContract)];

    public static (int Passed, int Failed) RunAll(Action<string> log)
    {
        var passed = 0;
        var failed = 0;
        foreach (var type in typeof(SuiteRunner).Assembly.GetTypes())
        {
            if (type.IsAbstract || !IsSuite(type))
                continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<Xunit.FactAttribute>() != null)
                    Count(Run(type, method, Array.Empty<object?>(), log), ref passed, ref failed);
                if (method.GetCustomAttribute<Xunit.TheoryAttribute>() == null)
                    continue;
                foreach (var data in method.GetCustomAttributes<Xunit.MemberDataAttribute>())
                {
                    var source = type.GetProperty(data.MemberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (source?.GetValue(null) is IEnumerable rows)
                        foreach (object?[] row in rows)
                            Count(Run(type, method, row, log), ref passed, ref failed);
                }
            }
        }

        log($"SUITES passed {passed} failed {failed}");
        return (passed, failed);
    }

    private static bool IsSuite(Type type)
    {
        foreach (var suite in Suites)
            if (suite.IsAssignableFrom(type))
                return true;
        return false;
    }

    private static void Count(bool ok, ref int passed, ref int failed)
    {
        if (ok)
            passed++;
        else
            failed++;
    }

    private static bool Run(Type type, MethodInfo method, object?[] arguments, Action<string> log)
    {
        var name = $"{type.Name}.{method.Name}({string.Join(", ", arguments)})";
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            method.Invoke(instance, arguments);
            log("PASS " + name);
            return true;
        }
        catch (Exception e)
        {
            var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            log($"FAIL {name}: {cause.GetType().Name}: {cause.Message}");
            return false;
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }
}
