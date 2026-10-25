using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Lattice.Api;
using Lattice.Core.World;
using UnityEngine;
using BuildState = Lattice.Api.BuildState;
using Cell = Lattice.Api.Cell;
using GameCell = Assets.Scripts.GridSystem.Cell;

namespace Lattice.Game.World;

/// <summary>A game structure's facts, read the one way every part of the bridge reads them. Main thread only.</summary>
internal static class GameReads
{
    /// <summary>The structure as Lattice sees it, or a reason when its rotation is not a cube turn.</summary>
    public static Result<StructureInfo, Reason> Info(Structure structure)
    {
        var position = structure.ThingTransformPosition;
        var game = new GameStructure(
            structure.ReferenceId,
            structure.PrefabHash,
            position.x,
            position.y,
            position.z,
            Rotation(structure.ThingTransformRotation),
            structure.CurrentBuildStateIndex,
            structure.BuildStates?.Count ?? 0,
            ColourIndex(structure),
            structure.CustomName ?? "",
            Setting(structure));
        return StructureReading.Read(game);
    }

    /// <summary>The structure's id, piece and cell; cheaper than <see cref="Info"/>.</summary>
    public static StructureRef Ref(Structure structure)
    {
        var position = structure.ThingTransformPosition;
        return new StructureRef(
            new ReferenceId(structure.ReferenceId),
            new PrefabKey(structure.PrefabHash),
            StructureReading.CellOf(position.x, position.y, position.z));
    }

    /// <summary>The labeller setting of a structure that has one (the game's <c>ISetable</c>), else none.</summary>
    public static Option<double> Setting(Structure structure) =>
        structure is ISetable setable ? Option.Some(setable.Setting) : Option<double>.None;

    /// <summary>The structure's build state now.</summary>
    public static BuildState State(Structure structure) =>
        StructureReading.StateOf(structure.CurrentBuildStateIndex, structure.BuildStates?.Count ?? 0);

    /// <summary>The paint index as the game saves it: -1 when unpainted (<c>Thing.InitialiseSaveData</c>).</summary>
    public static int ColourIndex(Thing thing) =>
        thing.CustomColor == null || thing.CustomColor.Normal == thing.PaintableMaterial ? -1 : GameManager.GetColorIndex(thing.CustomColor);

    /// <summary>The game rotation of a Unity quaternion.</summary>
    public static GameRotation Rotation(Quaternion q) => new(q.x, q.y, q.z, q.w);

    /// <summary>The Unity quaternion of a game rotation.</summary>
    public static Quaternion Quaternion(GameRotation q) => new(q.X, q.Y, q.Z, q.W);

    /// <summary>The game's Grid3 key of a cell.</summary>
    public static Grid3 Grid(Cell cell) => new(cell.X, cell.Y, cell.Z);

    /// <summary>The cell of a game Grid3 key.</summary>
    public static Cell Cell(Grid3 grid) => new(grid.x, grid.y, grid.z);

    /// <summary>The position of a cell's key in metres (the game's <c>GridController.LocalToWorld</c>: key * 0.1).</summary>
    public static Vector3 Position(Cell cell) => new(cell.X * 0.1f, cell.Y * 0.1f, cell.Z * 0.1f);

    /// <summary>The large cell at a key, if the game has one.</summary>
    public static GameCell? LargeCell(Cell cell) => GridController.World?.GetCell(Grid(cell));
}
