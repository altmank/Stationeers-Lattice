using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Lattice.Core.Modules;
using Lattice.Core.Saving;
using Lattice.Core.World;
using Lattice.Tests.World;

namespace Lattice.Tests.Build;

/// <summary>
/// Harmony injects a patch method's parameter by name: a name other than its specials (<c>__instance</c>, <c>__result</c>,
/// <c>__state</c>, <c>___field</c>, ...) must be a parameter of the target method, or Harmony refuses the patch when it is
/// installed and the feature never starts, while unit tests over the hook bodies still pass. This reads the patches
/// Lattice installs by hand (<c>WorldPatches</c> and <c>SaveHooks</c>), takes each target from the manifest member the
/// patch names, and checks every named injected parameter of its hooks against the real game assembly's metadata.
/// Skipped without the game's Assembly-CSharp.dll, like the manifest tests.
/// </summary>
public sealed class HarmonyBindingTests
{
    private static readonly Dictionary<string, GameMember> Targets = new()
    {
        ["WorldManifest.Register"] = WorldManifest.Register,
        ["WorldManifest.Deregister"] = WorldManifest.Deregister,
        ["WorldManifest.Detatch"] = WorldManifest.Detatch,
        ["WorldManifest.Attach"] = WorldManifest.Attach,
        ["WorldManifest.BuildStateIndex"] = WorldManifest.BuildStateIndex,
        ["WorldManifest.SetCustomColor"] = WorldManifest.SetCustomColor,
        ["WorldManifest.CustomName"] = WorldManifest.CustomName,
        ["SavingManifest.LoadGame"] = SavingManifest.LoadGame,
        ["SavingManifest.Save"] = SavingManifest.Save,
        ["SavingManifest.SavingFinished"] = SavingManifest.SavingFinished,
    };

    private static readonly Regex WorldPatch = new(
        @"\bPatch\(harmony, patched, (?:(?<kind>Method|Setter)\((?<member>\w+\.\w+)\)|setter)(?<hooks>[^;]*)\);");

    private static readonly Regex SavingPatch = new(
        @"harmony\.Patch\(Method\((?<member>\w+\.\w+)\), (?:prefix|postfix): Hook\(nameof\((?<hook>\w+)\)\)\)");

    private static readonly Regex NameOf = new(@"nameof\((\w+)\)");

    private sealed record Patched(string File, string Source, string Member, bool Setter, string Hook);

    [GameFilesFact]
    public void Every_named_injected_parameter_of_a_Lattice_patch_exists_on_its_game_target()
    {
        var patches = WorldPatches().Concat(SavingPatches()).ToList();
        var problems = new List<string>();
        var game = GameTypes(Path.Combine(ManifestTests.ManagedDir()!, "Assembly-CSharp.dll"));

        foreach (var patch in patches)
        {
            var label = $"{patch.File} {patch.Hook} -> {patch.Member}";
            if (!Targets.TryGetValue(patch.Member, out var target))
            {
                problems.Add($"{label}: no target row for {patch.Member}; add it to Targets");
                continue;
            }

            var named = NamedParameters(patch.Source, patch.Hook);
            var (type, method) = target switch
            {
                GameMember.Method m => (m.DeclaringType, m.Name),
                GameMember.Property p when patch.Setter => (p.DeclaringType, "set_" + p.Name),
                _ => (target.TypeName, target.MemberName),
            };
            if (!game.TryGetValue(type, out var definition))
            {
                problems.Add($"{label}: game type {type} not found");
                continue;
            }

            var overloads = ParameterNames(definition.Reader, definition.Type, method).ToList();
            if (overloads.Count == 0)
                problems.Add($"{label}: {type}.{method} not found");
            else if (!overloads.Any(o => named.All(o.Contains)))
                problems.Add($"{label}: parameter(s) {string.Join(", ", named.Where(n => !overloads.Any(o => o.Contains(n))))} not on {type}.{method} (it has {string.Join(" | ", overloads.Select(o => string.Join(",", o)))})");
        }

        Assert.True(problems.Count == 0, "Harmony would refuse these patches:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void The_scan_reads_every_patch_the_sources_install()
    {
        var world = WorldSource();
        var saving = SavingSource();

        Assert.Equal(Occurrences(world, "Patch(harmony, patched, "), WorldPatch.Matches(world).Count);
        Assert.Equal(7, WorldPatch.Matches(world).Cast<Match>().Count(static m => m.Groups["member"].Success));
        Assert.Equal(Occurrences(saving, "harmony.Patch("), SavingPatch.Matches(saving).Count);
        Assert.Equal(3, SavingPatch.Matches(saving).Count);
        Assert.Equal(12, WorldPatches().Concat(SavingPatches()).Count());
    }

    [Fact]
    public void The_hooks_on_the_game_s_setting_setters_inject_nothing_by_name()
    {
        var hooks = SettingHooks().ToList();

        Assert.Equal(2, hooks.Count);
        foreach (var hook in hooks)
            Assert.Empty(NamedParameters(WorldSource(), hook));
    }

    private static int Occurrences(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

    private static IEnumerable<Patched> WorldPatches()
    {
        var source = WorldSource();
        foreach (Match match in WorldPatch.Matches(source))
        {
            if (!match.Groups["member"].Success)
                continue;
            foreach (Match hook in NameOf.Matches(match.Groups["hooks"].Value))
                yield return new Patched("WorldPatches", source, match.Groups["member"].Value, match.Groups["kind"].Value == "Setter", hook.Groups[1].Value);
        }
    }

    private static IEnumerable<string> SettingHooks() =>
        WorldPatch.Matches(WorldSource()).Cast<Match>()
            .Where(static m => !m.Groups["member"].Success)
            .SelectMany(static m => NameOf.Matches(m.Groups["hooks"].Value).Cast<Match>().Select(static h => h.Groups[1].Value));

    private static IEnumerable<Patched> SavingPatches()
    {
        var source = SavingSource();
        return SavingPatch.Matches(source).Cast<Match>()
            .Select(m => new Patched("SaveHooks", source, m.Groups["member"].Value, false, m.Groups["hook"].Value));
    }

    private static string WorldSource() => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Lattice.Game", "World", "WorldPatches.cs"));

    private static string SavingSource() => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Lattice.Game", "Saving", "SaveHooks.cs"));

    /// <summary>The parameter names Harmony must find on the target: all but its underscore-prefixed specials.</summary>
    private static string[] NamedParameters(string source, string hook)
    {
        var declaration = Regex.Match(source, @"static \w+ " + hook + @"\((?<parameters>[^)]*)\)");
        Assert.True(declaration.Success, $"hook {hook} not found in the source");
        return declaration.Groups["parameters"].Value.Split(',')
            .Select(static p => p.Trim())
            .Where(static p => p.Length > 0)
            .Select(static p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last())
            .Where(static n => !n.StartsWith("__", StringComparison.Ordinal))
            .ToArray();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static Dictionary<string, (TypeDefinition Type, MetadataReader Reader)> GameTypes(string path)
    {
        var pe = new PEReader(File.OpenRead(path));
        var reader = pe.GetMetadataReader();
        var types = new Dictionary<string, (TypeDefinition, MetadataReader)>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!type.GetDeclaringType().IsNil)
                continue;
            var ns = reader.GetString(type.Namespace);
            types[(ns.Length > 0 ? ns + "." : "") + reader.GetString(type.Name)] = (type, reader);
        }

        return types;
    }

    /// <summary>The parameter names of every method of that name on the type and the game base types it derives from.</summary>
    private static IEnumerable<HashSet<string>> ParameterNames(MetadataReader reader, TypeDefinition type, string method)
    {
        TypeDefinition? current = type;
        while (current is { } definition)
        {
            foreach (var handle in definition.GetMethods())
            {
                var candidate = reader.GetMethodDefinition(handle);
                if (reader.GetString(candidate.Name) == method)
                    yield return candidate.GetParameters()
                        .Select(p => reader.GetString(reader.GetParameter(p).Name))
                        .Where(static n => n.Length > 0)
                        .ToHashSet();
            }

            var baseType = definition.BaseType;
            current = baseType.Kind == HandleKind.TypeDefinition ? reader.GetTypeDefinition((TypeDefinitionHandle)baseType) : null;
        }
    }
}
