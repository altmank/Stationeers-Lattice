using System;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>
/// How gas splits over several atmospheres: taken from each in proportion to what it holds, and given to a room's
/// cells in proportion to their volume.
/// </summary>
internal static class GasShares
{
    /// <summary>
    /// The moles each member gives when the given amount (none: all of it) is taken in proportion to what each holds.
    /// The amount is capped at the total, and no member gives more than it holds. Linear in the members.
    /// </summary>
    public static double[] Proportional(double[] held, Option<double> amount)
    {
        var total = 0.0;
        foreach (var moles in held)
            total += Math.Max(0.0, moles);
        var taken = new double[held.Length];
        if (!(total > 0.0))
            return taken;

        var wanted = amount.TryGet(out var asked) ? Math.Min(total, asked) : total;
        for (var index = 0; index < held.Length; index++)
        {
            var have = Math.Max(0.0, held[index]);
            taken[index] = Math.Min(have, wanted * have / total);
        }

        return taken;
    }

    /// <summary>Each volume's share of the whole, adding up to 1; equal shares when no volume is positive. Empty for no volumes.</summary>
    public static double[] ByVolume(double[] volumesL)
    {
        var total = 0.0;
        foreach (var volume in volumesL)
            total += Math.Max(0.0, volume);
        var shares = new double[volumesL.Length];
        for (var index = 0; index < volumesL.Length; index++)
            shares[index] = total > 0.0 ? Math.Max(0.0, volumesL[index]) / total : 1.0 / volumesL.Length;
        return shares;
    }
}

/// <summary>The ideal gas law as the game's readings are combined.</summary>
internal static class GasLaw
{
    /// <summary>The gas constant, J/(mol K).</summary>
    public const double GasConstant = 8.314462618;

    /// <summary>Pressure in kPa of moles at a temperature in a gas volume in litres; 0 without a volume.</summary>
    public static double PressureKpa(double moles, double kelvin, double gasVolumeL) =>
        gasVolumeL > 0.0 ? moles * GasConstant * kelvin / gasVolumeL : 0.0;
}
