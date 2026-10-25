using Lattice.Api;

namespace Lattice.Core.Safety;

/// <summary>The rule build safety's warnings come from, and their codes.</summary>
internal static class SafetyCodes
{
    /// <summary>The rule every build safety warning comes from.</summary>
    public static RuleId Rule => RuleId.Known("lattice.safety");

    /// <summary>A swap takes out a wall, window or door that holds air in a room.</summary>
    public static ReasonCode SwapOnAirRoom => ReasonCodes.SwapOnAirRoom;

    /// <summary>Removals cut the power to a device that keeps air, heating or life support going.</summary>
    public static ReasonCode CutsPower => ReasonCodes.CutsPower;

    /// <summary>A planned vent lets a pipe network's gas into a room.</summary>
    public static ReasonCode GasIndoors => ReasonCodes.GasIndoors;

    /// <summary>Oxidiser would reach the outside.</summary>
    public static ReasonCode OxidiserOutside => ReasonCodes.OxidiserOutside;

    /// <summary>A path opens to hot gas.</summary>
    public static ReasonCode HotGas => ReasonCodes.HotGas;

    /// <summary>A planned piece stands in front of a device's controls.</summary>
    public static ReasonCode ControlsUnreachable => ReasonCodes.ControlsUnreachable;

    /// <summary>A cable, pipe or chute runs through the space in front of a door.</summary>
    public static ReasonCode RunThroughDoor => ReasonCodes.RunThroughDoor;

    /// <summary>A planned piece stands on or in front of a window.</summary>
    public static ReasonCode OnWindow => ReasonCodes.OnWindow;

    /// <summary>Painting in a room adds pollutant to its air.</summary>
    public static ReasonCode PaintIndoors => ReasonCodes.PaintIndoors;

    /// <summary>Terrain fills a cell a planned piece takes; Lattice warns and never digs.</summary>
    public static ReasonCode TerrainInTheWay => ReasonCodes.TerrainInTheWay;
}
