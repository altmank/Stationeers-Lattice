namespace Lattice.Core.Consequences;

/// <summary>
/// One room's air added up cell by cell, as <c>Room.CacheRoomData</c> pools it: moles per gas, energy, heat capacity,
/// gas moles and volume summed; temperature is the pooled energy over the pooled heat capacity
/// (<c>IdealGas.Temperature</c>) and pressure the gas moles at that temperature in the summed volume. Also the box of
/// the cell centres. An accumulator: fill it, then read it.
/// </summary>
internal sealed class RoomAir(int gasCount)
{
    private readonly double[] _moles = new double[gasCount];

    private bool _hasCell;

    /// <summary>Cells added.</summary>
    public int CellCount { get; private set; }

    /// <summary>Summed volume in litres.</summary>
    public double VolumeL { get; private set; }

    /// <summary>Summed energy in joules.</summary>
    public double EnergyJ { get; private set; }

    /// <summary>Summed heat capacity in J/K.</summary>
    public double HeatCapacityJPerK { get; private set; }

    /// <summary>Moles in the gas state only; liquids are left out of pressure, as <c>GetTotalMolesGasses</c> does.</summary>
    public double GasMol { get; private set; }

    /// <summary>Every mole, gas and liquid.</summary>
    public double TotalMol { get; private set; }

    /// <summary>The lowest cell centre X, in metres.</summary>
    public double MinX { get; private set; }

    /// <summary>The lowest cell centre Y, in metres.</summary>
    public double MinY { get; private set; }

    /// <summary>The lowest cell centre Z, in metres.</summary>
    public double MinZ { get; private set; }

    /// <summary>The highest cell centre X, in metres.</summary>
    public double MaxX { get; private set; }

    /// <summary>The highest cell centre Y, in metres.</summary>
    public double MaxY { get; private set; }

    /// <summary>The highest cell centre Z, in metres.</summary>
    public double MaxZ { get; private set; }

    /// <summary>Pooled energy over pooled heat capacity; 0 for no heat capacity.</summary>
    public double TemperatureK => HeatCapacityJPerK > 0.0 ? EnergyJ / HeatCapacityJPerK : 0.0;

    /// <summary>The gas moles at <see cref="TemperatureK"/> in <see cref="VolumeL"/>.</summary>
    public double PressureKpa => GasLaw.PressureKpa(GasMol, TemperatureK, VolumeL);

    /// <summary>How many gases it sums.</summary>
    public int GasCount => _moles.Length;

    /// <summary>The summed moles of one gas.</summary>
    public double MolesOf(int gasIndex) => _moles[gasIndex];

    /// <summary>A cell's centre in metres, counted once per cell whether or not it has air.</summary>
    public void AddCell(double x, double y, double z)
    {
        CellCount++;
        if (!_hasCell)
        {
            MinX = MaxX = x;
            MinY = MaxY = y;
            MinZ = MaxZ = z;
            _hasCell = true;
            return;
        }

        MinX = x < MinX ? x : MinX;
        MinY = y < MinY ? y : MinY;
        MinZ = z < MinZ ? z : MinZ;
        MaxX = x > MaxX ? x : MaxX;
        MaxY = y > MaxY ? y : MaxY;
        MaxZ = z > MaxZ ? z : MaxZ;
    }

    /// <summary>A cell's volume in litres.</summary>
    public void AddVolume(double litres) => VolumeL += litres;

    /// <summary>One gas of one cell: its moles, energy and heat capacity, and whether it is in the gas state.</summary>
    public void AddGas(int gasIndex, double moles, double energyJ, double heatCapacityJPerK, bool isGas)
    {
        _moles[gasIndex] += moles;
        TotalMol += moles;
        EnergyJ += energyJ;
        HeatCapacityJPerK += heatCapacityJPerK;
        if (isGas)
            GasMol += moles;
    }
}
