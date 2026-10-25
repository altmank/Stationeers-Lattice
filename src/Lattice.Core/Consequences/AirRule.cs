namespace Lattice.Core.Consequences;

/// <summary>What a swap does to the air and gravity a piece blocks.</summary>
internal enum AirChange
{
    /// <summary>Blocks exactly what the old piece blocked.</summary>
    Keeps = 1,

    /// <summary>Blocks air or gravity the old piece let through (a frame finished, a leaky wall sealed).</summary>
    Seals = 2,

    /// <summary>Would let through air or gravity the old piece blocked.</summary>
    WouldOpen = 3,
}

/// <summary>What one build state of a piece blocks (<c>BuildState.BlockAir</c>, <c>BuildState.BlockGravity</c>).</summary>
internal readonly record struct PieceBlocking(bool Air, bool Gravity);

/// <summary>
/// Never open: a swap may keep or close what the old piece blocks. Air is <c>BuildState.BlockAir</c>
/// (<c>Structure.CanAirPass</c>); rooms are walled by <c>BlockGravity</c> (<c>Structure.CanGravityPass</c>,
/// <c>RoomEvaluator</c>'s <c>IsGridBlockedByStructure</c>), so a piece that stopped blocking gravity would merge or
/// break rooms.
/// </summary>
internal static class AirRule
{
    /// <summary>What replacing a piece blocking <paramref name="before"/> with one blocking <paramref name="after"/> does.</summary>
    public static AirChange Judge(PieceBlocking before, PieceBlocking after)
    {
        if ((before.Air && !after.Air) || (before.Gravity && !after.Gravity))
            return AirChange.WouldOpen;
        return (!before.Air && after.Air) || (!before.Gravity && after.Gravity) ? AirChange.Seals : AirChange.Keeps;
    }
}

/// <summary>How a face would bear its pressure difference with a new wall.</summary>
internal enum StressVerdict
{
    /// <summary>Neither stressed nor damaged.</summary>
    Ok = 1,

    /// <summary>Above the new wall's <c>MaxPressureDelta</c> times <c>Thing.StressedRatio</c>: the game marks it stressed.</summary>
    Stressed = 2,

    /// <summary>At or above the face's summed <c>MaxPressureDelta</c> with the new wall: the game would damage it.</summary>
    Overstressed = 3,
}

/// <summary>
/// A face's load: the pressure difference across it now, the summed <c>MaxPressureDelta</c> of the other air-blocking
/// structures on it (the piece being replaced left out), and whether the game skips the face (a frame beside it).
/// </summary>
internal readonly record struct FaceLoad(double DifferenceKpa, double OtherMaxDeltaKpa, bool Shielded);

/// <summary>
/// The game's wall stress rule (<c>Atmosphere.ReactWithStructures</c>): per face, the absolute difference of the two
/// cells' <c>PressureGassesAndLiquids</c> against the summed <c>MaxPressureDelta</c> of the face's air-blocking
/// structures with a positive <c>MaxPressureDelta</c>; above the sum, each such structure whose own
/// <c>MaxPressureDelta</c> is below the difference is damaged; above <c>MaxPressureDelta</c> times
/// <c>StressedRatio</c> it is marked stressed. A structure with <c>MaxPressureDelta</c> 0 or less is never damaged or
/// stressed. A face next to a cell whose centre structure blocks the grid (a frame) is skipped by the game, so it is
/// never stressed.
/// </summary>
internal static class WallStress
{
    /// <summary>The verdict for a new wall rated <paramref name="targetMaxDeltaKpa"/> on a face under <paramref name="load"/>.</summary>
    public static StressVerdict Judge(FaceLoad load, double targetMaxDeltaKpa, double stressedRatio)
    {
        if (load.Shielded || targetMaxDeltaKpa <= 0.0)
            return StressVerdict.Ok;
        var faceSum = load.OtherMaxDeltaKpa + targetMaxDeltaKpa;
        if (load.DifferenceKpa >= faceSum)
            return StressVerdict.Overstressed;
        return load.DifferenceKpa > targetMaxDeltaKpa * stressedRatio ? StressVerdict.Stressed : StressVerdict.Ok;
    }
}
