using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Objects;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.EntryPoint;
using Lattice.Core.Ports;
using Lattice.Game.Catalogue;
using Lattice.Game.Looks;
using UnityEngine;

namespace Lattice.Game.Live;

/// <summary>
/// Piece looks read back in a running world (mode <c>looks</c>): every structure prefab's look source, mesh and
/// per-step counts, readable meshes and family, as the catalogue and the mesh reader each see them; one MatterLink
/// prefab in full, with a family registered on it through the registries; and a cached look lookup allocating nothing.
/// </summary>
internal static class LooksRun
{
    private const string MatterLinkPrefix = "StructureMatterlink";

    public static IEnumerator Run(Action<string> line)
    {
        var waited = 0f;
        while (!(LatticeSystem.Current.TryGet(out var running) && running is LatticeWorld) && waited < 60f)
        {
            yield return new WaitForSecondsRealtime(1f);
            waited += 1f;
        }

        if (!LatticeSystem.Current.TryGet(out var system) || system is not LatticeWorld world)
        {
            line("FAIL looks: Lattice is not running in this world");
            yield break;
        }

        var source = new GamePrefabSource();
        var catalogue = new PrefabCatalogue(source);
        var meshes = new PieceMeshes();
        var families = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int pieces = 0, noLook = 0, disagree = 0;
        var modded = Option<PrefabKey>.None;
        foreach (var key in source.Structures)
        {
            if (!catalogue.Piece(key).TryGet(out var facts) || !catalogue.NameOf(key).TryGet(out var name))
                continue;
            pieces++;
            families[facts.Family.Value] = families.TryGetValue(facts.Family.Value, out var n) ? n + 1 : 1;
            if (!facts.Look.TryGet(out var look) || !meshes.Of(key).TryGet(out var drawing))
            {
                noLook++;
                continue;
            }

            if (look.Source.GetType() != drawing.Source.GetType() || drawing.Parts.Count == 0)
            {
                disagree++;
                line($"looks disagree {name}: catalogue {look.Source}, meshes {drawing.Source} with {drawing.Parts.Count} parts");
            }

            if (modded.IsNone && name.StartsWith(MatterLinkPrefix, StringComparison.Ordinal))
                modded = Option.Some(key);
        }

        line($"looks pieces {pieces}; {meshes.Counts}");
        line($"looks families: {Join(families)}");
        line($"{(noLook == 0 ? "PASS" : "FAIL")} every piece has a look: without {noLook}");
        line($"{(disagree == 0 ? "PASS" : "FAIL")} catalogue and mesh reader agree on every piece's source: disagree {disagree}");
        line($"{(meshes.Counts.Meshes > 0 && meshes.Counts.ReadableMeshes >= 0 ? "PASS" : "FAIL")} the server holds prefab meshes: {meshes.Counts.Meshes} pieces drawn from meshes");
        yield return null;

        Allocation(source.Structures, meshes, line);
        yield return null;

        if (modded.TryGet(out var matterlink))
            ModdedPiece(matterlink, catalogue, meshes, world, line);
        else
            line("FAIL looks: no MatterLink prefab in this world");
    }

    // D1: a cached lookup allocates nothing. Every piece is read above; a second pass only finds them.
    private static void Allocation(IReadOnlyList<PrefabKey> keys, PieceMeshes meshes, Action<string> line)
    {
        var parts = 0;
        foreach (var key in keys)
            parts += meshes.Of(key).TryGet(out var warm) ? warm.Parts.Count : 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < keys.Count; i++)
            parts += meshes.Of(keys[i]).TryGet(out var drawing) ? drawing.Parts.Count : 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        line($"{(allocated == 0 ? "PASS" : "FAIL")} {keys.Count} cached look lookups allocate {allocated} B ({parts} parts seen)");
    }

    private static void ModdedPiece(PrefabKey key, PrefabCatalogue catalogue, PieceMeshes meshes, LatticeWorld world, Action<string> line)
    {
        var name = catalogue.NameOf(key).Reduce("?");
        if (!catalogue.Piece(key).TryGet(out var facts) || !meshes.Of(key).TryGet(out var drawing))
        {
            line($"FAIL looks modded {name}: not read");
            return;
        }

        var prefab = Prefab.Find(key.Hash);
        line($"looks modded {name}: class {(prefab != null ? prefab.GetType().FullName : "?")}, family {facts.Family.Value}, look {facts.Look.Map(l => $"{l.Source} bounds {l.Bounds}").Reduce("none")}, {drawing.Parts.Count} parts, inactive left out {drawing.Skipped}");
        foreach (var part in drawing.Parts)
        {
            var at = part.Local.GetColumn(3);
            line($"looks modded part {part.Mesh.name}: vertices {part.Mesh.vertexCount}, readable {part.Mesh.isReadable}, shows {part.Shows}, at ({at.x:F2}, {at.y:F2}, {at.z:F2})");
        }

        var byType = world.Families.FamilyOf(key);
        if (!ModId.Create("lattice-live").TryGet(out var owner) || !PieceFamily.Create(owner, "links").TryGet(out var links))
        {
            line("FAIL looks: the test family key was refused");
            return;
        }

        var joined = world.Registries.Register(new CatalogueAddition.InFamily(key, links));
        if (!joined.TryGet(out var handle))
        {
            line($"FAIL looks: InFamily on {name} refused: {joined}");
            return;
        }

        var during = world.Families.FamilyOf(key);
        var again = world.Registries.Register(new CatalogueAddition.InFamily(key, PieceFamilies.Device));
        handle.Dispose();
        var after = world.Families.FamilyOf(key);
        var refusedAgain = again.Match(accepted => { accepted.Dispose(); return "accepted"; }, r => r.Code.Value);
        line($"{(during == links && after == byType && refusedAgain == ReasonCodes.AlreadyRegistered.Value ? "PASS" : "FAIL")} InFamily on {name}: by type {byType.Value}, registered {during.Value}, second registration {refusedAgain}, after dispose {after.Value}");
    }

    private static string Join(SortedDictionary<string, int> counts)
    {
        var text = new StringBuilder(128);
        foreach (var pair in counts)
            text.Append(text.Length == 0 ? "" : ", ").Append(pair.Key).Append(' ').Append(pair.Value);
        return text.ToString();
    }
}
