using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lattice.Core.World;

namespace Lattice.Tests.World;

/// <summary>A type that does not load in another mod skips that type and nothing else.</summary>
public sealed class TypeWalkTests
{
    private sealed class First { public void Set() { } }

    private sealed class Second { public void Set() { } }

    private sealed class Third { public void Set() { } }

    private static IEnumerable<MethodBase> SetOf(Type type) => [type.GetMethod("Set")!];

    private static Func<Type, IEnumerable<MethodBase>> Throwing<T>(Type bad, T exception) where T : Exception =>
        type => type == bad ? throw exception : SetOf(type);

    [Fact]
    public void A_type_that_does_not_load_is_skipped_and_the_others_are_found()
    {
        var result = TypeWalk.Run([typeof(First), typeof(Second), typeof(Third)], Throwing(typeof(Second), new TypeLoadException("bad field")));

        Assert.Equal([typeof(First).GetMethod("Set")!, typeof(Third).GetMethod("Set")!], result.Methods);
        Assert.Equal([typeof(Second)], result.Skipped);
        Assert.Contains(nameof(Second), result.SkippedLine());
    }

    [Fact]
    public void When_every_type_fails_the_result_is_empty_and_lists_them_all()
    {
        var result = TypeWalk.Run([typeof(First), typeof(Second), typeof(Third)], _ => throw new TypeLoadException());

        Assert.Empty(result.Methods);
        Assert.Equal([typeof(First), typeof(Second), typeof(Third)], result.Skipped);
        Assert.StartsWith("Lattice: skipped 3 types that don't load: ", result.SkippedLine());
    }

    [Fact]
    public void The_line_names_five_types_at_most()
    {
        var result = TypeWalk.Run(Enumerable.Repeat(typeof(First), 8), _ => throw new TypeLoadException());

        Assert.StartsWith("Lattice: skipped 8 types", result.SkippedLine());
        Assert.Equal(5, result.SkippedLine()!.Split(["First"], StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Nothing_skipped_has_no_line()
    {
        var result = TypeWalk.Run([typeof(First)], SetOf);

        Assert.Null(result.SkippedLine());
    }

    [Theory]
    [MemberData(nameof(LoadFailures))]
    public void Every_type_loading_exception_is_skipped(Exception failure)
    {
        var result = TypeWalk.Run([typeof(First)], _ => throw failure);

        Assert.Equal([typeof(First)], result.Skipped);
    }

    public static TheoryData<Exception> LoadFailures() =>
    [
        new TypeLoadException(),
        new System.IO.FileNotFoundException(),
        new System.IO.FileLoadException(),
        new BadImageFormatException(),
        new NotSupportedException(),
        new MissingMemberException(),
        new MissingMethodException(),
        new MissingFieldException(),
        new ReflectionTypeLoadException([], []),
    ];

    [Fact]
    public void An_exception_that_is_not_a_load_failure_surfaces()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TypeWalk.Run([typeof(First), typeof(Second)], Throwing(typeof(Second), new InvalidOperationException())));
    }

    [Fact]
    public void An_access_failure_is_not_a_load_failure_and_surfaces()
    {
        Assert.Throws<MethodAccessException>(() =>
            TypeWalk.Run([typeof(First), typeof(Second)], Throwing(typeof(Second), new MethodAccessException())));
    }

    [Fact]
    public void Null_types_are_ignored_and_a_method_found_twice_is_kept_once()
    {
        var result = TypeWalk.Run([null, typeof(First), typeof(First)], SetOf);

        Assert.Single(result.Methods);
    }
}
