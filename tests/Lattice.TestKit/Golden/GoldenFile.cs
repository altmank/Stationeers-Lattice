using System;
using System.IO;
using Lattice.Api;

namespace Lattice.TestKit.Golden;

/// <summary>What a golden-file check found.</summary>
internal abstract record GoldenOutcome
{
    private GoldenOutcome() { }

    /// <summary>No golden file existed: the actual text was written as the golden.</summary>
    public sealed record Written(string Path) : GoldenOutcome;

    /// <summary>The actual text equals the golden.</summary>
    public sealed record Matches(string Path) : GoldenOutcome;

    /// <summary>The texts differ: the first differing line (from 1) and both versions of it.</summary>
    public sealed record Differs(string Path, int Line, string Expected, string Actual) : GoldenOutcome
    {
        public override string ToString() =>
            $"{Path}:{Line} differs.\n expected: {Expected}\n   actual: {Actual}\nDelete the golden file to accept the new text.";
    }
}

/// <summary>
/// Golden files: the first run writes the actual text, later runs compare against it. Line endings are normalised to
/// '\n'. To accept a change, delete the golden file and run again.
/// </summary>
internal static class GoldenFile
{
    public static GoldenOutcome Check(string path, string actual)
    {
        var text = Normalise(actual);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, text);
            return new GoldenOutcome.Written(path);
        }

        var expected = Normalise(File.ReadAllText(path));
        if (expected == text)
            return new GoldenOutcome.Matches(path);
        var e = expected.Split('\n');
        var a = text.Split('\n');
        var line = 0;
        while (line < e.Length && line < a.Length && e[line] == a[line])
            line++;
        return new GoldenOutcome.Differs(path, line + 1, line < e.Length ? e[line] : "<end>", line < a.Length ? a[line] : "<end>");
    }

    /// <summary>Checks and fails the test on a difference; a first run that writes passes.</summary>
    public static void Verify(string path, string actual)
    {
        if (Check(path, actual) is GoldenOutcome.Differs differs)
            throw new KitCheckFailed(differs.ToString());
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n");
}
