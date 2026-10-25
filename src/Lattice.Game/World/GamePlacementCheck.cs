using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.World;

namespace Lattice.Game.World;

/// <summary>
/// The game's own placement check: the piece's construction cursor, which the game builds for every structure prefab
/// at world load, modded ones included (02 live checks: 781 of 781), so a modded piece's own <c>CanConstruct</c>
/// override decides. The cursor is moved to the placement, asked <c>CanConstruct</c> and, for a wall-mounted piece,
/// <c>CanMountOnWall</c>, then put back; both only read the world (<c>Structure.cs:1252</c>, <c>:806</c>).
/// A refusal is <see cref="ReasonCodes.GameRefused"/> with the game's message, tags removed; a piece with no cursor is
/// <see cref="ReasonCodes.UnknownPiece"/>.
/// <para>
/// Main thread only: it moves a game object, so a call from another thread is a Lattice bug and throws. Validation
/// builds the support answer map from it in frame slices. Cost per call: constant, one cursor check;
/// measured on a dedicated server at about 1.2 us for a frame and 2.1 us for a pipe (200 calls each), so a slice of a
/// few thousand checks fits a frame.
/// </para>
/// <para>
/// Answers do not depend on a player being near: the game's ground checks read the voxel terrain, which a server
/// holds for the whole world (<c>SmallGrid.HasVoxelBelow</c>, <c>VoxelTerrain.GetDensityWorldSpace</c>), not physics
/// colliders. The live run found no collider under the probed spot, and a small flag on the terrain there was
/// accepted while one in free air was refused, so no answer here is "world not loaded".
/// </para>
/// <para>
/// The server builds what it is told without checking
/// (<c>MultiConstructor.Construct</c> checks nothing), so builders ask this first.
/// </para>
/// </summary>
internal sealed class GamePlacementCheck : IPlacementCheck
{
    /// <summary>The game's table of construction cursors by prefab name (private).</summary>
    public static readonly FieldInfo? CursorTable =
        typeof(InventoryManager).GetField("_constructionCursors", BindingFlags.NonPublic | BindingFlags.Static);

    private readonly int _mainThread = Thread.CurrentThread.ManagedThreadId;
    private Dictionary<int, Structure>? _byHash;

    public Result<Unit, Reason> Check(Placement placement)
    {
        if (Thread.CurrentThread.ManagedThreadId != _mainThread)
            throw new InvalidOperationException("The game's placement check runs on the main thread only.");
        if (!Cursors().TryGetValue(placement.Piece.Hash, out var cursor) || cursor == null)
            return Result<Unit, Reason>.Error(WorldReasons.NoCursor(placement));

        var transform = cursor.ThingTransform;
        var oldPosition = transform.position;
        var oldRotation = transform.rotation;
        var oldThingPosition = cursor.Position;
        var oldThingRotation = cursor.Rotation;
        try
        {
            var position = GameReads.Position(placement.At);
            var rotation = GameReads.Quaternion(TurnReading.ToGame(placement.Turn));
            transform.SetPositionAndRotation(position, rotation);
            cursor.Position = position;
            cursor.Rotation = rotation;

            var answer = cursor.CanConstruct();
            if (!answer.CanConstruct)
                return Result<Unit, Reason>.Error(WorldReasons.Refused(placement, answer.ErrorMessage ?? ""));
            if (cursor.PlacementType == PlacementSnap.FaceMount)
            {
                var mount = cursor.CanMountOnWall();
                if (!mount)
                    return Result<Unit, Reason>.Error(WorldReasons.Refused(placement, mount.ResultMessage() ?? ""));
            }

            return Result<Unit, Reason>.Ok(Unit.Value);
        }
        finally
        {
            transform.SetPositionAndRotation(oldPosition, oldRotation);
            cursor.Position = oldThingPosition;
            cursor.Rotation = oldThingRotation;
        }
    }

    private Dictionary<int, Structure> Cursors()
    {
        if (_byHash != null)
            return _byHash;
        var byHash = new Dictionary<int, Structure>();
        if (CursorTable?.GetValue(null) is Dictionary<string, Structure> byName)
            foreach (var cursor in byName.Values)
                if (cursor != null)
                    byHash[cursor.PrefabHash] = cursor;
        if (byHash.Count > 0)
            _byHash = byHash;
        return byHash;
    }
}
