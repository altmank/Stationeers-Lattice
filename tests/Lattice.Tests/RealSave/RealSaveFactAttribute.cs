namespace Lattice.Tests.RealSave;

/// <summary>
/// A measurement over a real save: it runs only when asked (<c>LATTICE_REALSAVE=1</c>), and then fails if the save
/// fixture is missing. Its numbers go to the 51 report.
/// </summary>
public sealed class RealSaveFactAttribute(string save) : FixtureFactAttribute(MissingSave([save]), onlyWithSwitch: true);
