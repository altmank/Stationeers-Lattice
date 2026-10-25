using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.TestKit.World;

/// <summary>The build state of a structure at a step: built at its piece's last step, else building.</summary>
internal static class BuildStates
{
    /// <summary>Building when the piece is unknown, since only the catalogue knows a piece's last step.</summary>
    public static BuildState At(StepIndex step, Option<PieceFacts> piece) =>
        piece.TryGet(out var facts) && step.Value == facts.Steps.Count - 1
            ? new BuildState.Built()
            : new BuildState.Building(step);
}
