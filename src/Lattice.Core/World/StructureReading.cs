using System;
using Lattice.Api;

namespace Lattice.Core.World;

/// <summary>
/// A structure's facts as the game holds them, before Lattice reads them: id, prefab hash, registered position in
/// metres and rotation, build state index and the piece's number of build states, paint index (-1 = unpainted, as the
/// game saves it: <c>Thing.InitialiseSaveData</c>), custom name (empty = none) and the labeller setting (the game's
/// <c>ISetable.Setting</c>; none for a structure that has none).
/// </summary>
internal readonly record struct GameStructure(
    long Id,
    int PrefabHash,
    float X,
    float Y,
    float Z,
    GameRotation Rotation,
    int BuildIndex,
    int BuildStates,
    int ColourIndex,
    string CustomName,
    Option<double> Setting);

/// <summary>
/// Reads a structure the way the save loader reads a save, so the world and a save of it agree: the cell is the game's
/// Grid3 key, round(metres * 10) per axis; a build index
/// below 0 is broken, the last index is built, any other is building at that step.
/// </summary>
internal static class StructureReading
{
    /// <summary>The structure, or a reason when its rotation is not a cube turn.</summary>
    public static Result<StructureInfo, Reason> Read(in GameStructure game)
    {
        if (!TurnReading.FromGame(game.Rotation).TryGet(out var turn))
            return Result<StructureInfo, Reason>.Error(Reason.Of(
                ReasonCodes.InvalidValue,
                WorldReasons.World,
                ValueMap<string, string>.Empty.With("structure", game.Id.ToString()).With("problem", "rotation-not-a-turn")));

        var structure = new StructureRef(new ReferenceId(game.Id), new PrefabKey(game.PrefabHash), CellOf(game.X, game.Y, game.Z));
        var colour = PaintColour.Create(game.ColourIndex).ToOption();
        var name = string.IsNullOrEmpty(game.CustomName) ? Option<ThingName>.None : ThingName.Create(game.CustomName).ToOption();
        var setting = game.Setting.TryGet(out var value) ? Setting.Create(value).ToOption() : Option<Setting>.None;
        return Result<StructureInfo, Reason>.Ok(
            new StructureInfo(structure, turn, StateOf(game.BuildIndex, game.BuildStates), colour, name, setting));
    }

    /// <summary>The cell a position in metres is in: the game's Grid3 key.</summary>
    public static Cell CellOf(float x, float y, float z) => new(Key(x), Key(y), Key(z));

    /// <summary>Broken below 0; built at the last of <paramref name="states"/> (or with none); else building.</summary>
    public static BuildState StateOf(int index, int states) =>
        index < 0 ? new BuildState.Broken()
        : index >= states - 1 ? new BuildState.Built()
        : StepIndex.Create(index).TryGet(out var step) ? new BuildState.Building(step)
        : new BuildState.Broken();

    private static int Key(float metres) => (int)Math.Round(metres * 10.0, MidpointRounding.AwayFromZero);
}
