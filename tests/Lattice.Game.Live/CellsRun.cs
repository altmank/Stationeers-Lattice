using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Util;
using Lattice.Api;
using UnityEngine;

namespace Lattice.Game.Live;

/// <summary>
/// The contract's <see cref="Lattice.Api.Cell.FromWorld"/> against the game's own snap in a running world (mode <c>cells</c>): for
/// every grid size and offset the structure prefabs declare, <c>new Grid3(point.GridCenter(GridSize, GridOffset))</c>
/// (decompile Assets.Scripts.Objects/Structure.cs <c>GetGrid</c>) beside <see cref="Lattice.Api.Cell.FromWorld"/> on the grid
/// Lattice reads for that pair, over points that include every tie and both signs.
/// </summary>
internal static class CellsRun
{
    private const int Steps = 4_000;

    public static IEnumerator Run(Action<string> line)
    {
        yield return new WaitForSecondsRealtime(2f);
        var grids = new Dictionary<(float Size, float Offset), (string Prefab, int Count)>();
        foreach (var thing in Prefab.AllPrefabs)
            if (thing is Structure structure)
            {
                var pair = (structure.GridSize, structure.GridOffset);
                grids[pair] = grids.TryGetValue(pair, out var seen) ? (seen.Prefab, seen.Count + 1) : (structure.PrefabName, 1);
            }

        var checkedPoints = 0;
        var differs = 0;
        var unmapped = 0;
        foreach (var pair in grids)
        {
            var (size, offset) = pair.Key;
            line($"cells grid {size} offset {offset}: {pair.Value.Count} prefabs, e.g. {pair.Value.Prefab}");
            if (!GridOf(size, offset).TryGet(out var grid))
            {
                unmapped++;
                line($"cells grid {size} offset {offset}: not a grid Lattice reads");
                continue;
            }

            for (var i = -Steps; i <= Steps; i++)
            {
                var metres = i * size / 8f + (i % 3) * 0.001f;
                var point = new Vector3(metres, -metres * 0.5f, metres * 1.5f);
                var game = new Grid3(point.GridCenter(size, offset));
                var lattice = Lattice.Api.Cell.FromWorld(new WorldPoint(point.x, point.y, point.z), grid);
                checkedPoints++;
                if (game.x == lattice.X && game.y == lattice.Y && game.z == lattice.Z)
                    continue;
                differs++;
                if (differs <= 20)
                    line($"cells differ at ({point.x:R}, {point.y:R}, {point.z:R}) on {grid}: game ({game.x}, {game.y}, {game.z}), Lattice {lattice}");
            }
        }

        line($"cells counts: {grids.Count} grids, {unmapped} not read, {checkedPoints} points, {differs} differ");
        line($"{(differs == 0 && checkedPoints > 0 ? "PASS" : "FAIL")} Cell.FromWorld keys every point as the game snaps it");
    }

    private static Option<PieceGrid> GridOf(float size, float offset) =>
        Mathf.Approximately(size, SmallGrid.SmallGridSize) && Mathf.Approximately(offset, SmallGrid.SmallGridOffset) ? Option.Some(PieceGrid.Small)
        : Mathf.Approximately(size, 2f) && Mathf.Approximately(offset, 0f) ? Option.Some(PieceGrid.Large)
        : Option<PieceGrid>.None;
}
