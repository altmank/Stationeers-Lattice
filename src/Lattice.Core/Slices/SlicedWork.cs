namespace Lattice.Core.Slices;

/// <summary>The kinds of main-thread work that each get their own time budget per frame.</summary>
internal enum WorkKind
{
    Checking = 1,
    Sending = 2,
    Saving = 3,
    Drawing = 4,
}

/// <summary>What a step of sliced work says about the work after it.</summary>
internal enum StepOutcome
{
    /// <summary>More steps remain.</summary>
    More = 1,

    /// <summary>The work is finished; it is not stepped again.</summary>
    Done = 2,

    /// <summary>
    /// More steps remain, none before the next frame: background work that takes a small share of each frame however
    /// much budget is left. The jobs after it in the frame still run.
    /// </summary>
    NextFrame = 3,
}

/// <summary>
/// Resumable work: it keeps its own place and does one small piece per <see cref="Step"/>. The scheduler calls
/// <see cref="Step"/> on the main thread as many times as the frame's budget allows and resumes next frame. Steps
/// should be of similar, small cost: the scheduler stops a slice when the next step is expected to pass the budget,
/// judged from the steps seen so far.
/// </summary>
internal interface ISlicedWork
{
    StepOutcome Step();
}
