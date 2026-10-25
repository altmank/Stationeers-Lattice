using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Support;

/// <summary>What the game's own placement check said about a placement on the built world.</summary>
internal abstract record GameAnswer
{
    private GameAnswer() { }

    /// <summary>The game's cursor accepts it.</summary>
    public sealed record Accepts : GameAnswer;

    /// <summary>The one acceptance, so answering a large plan's asks makes no answer object for each.</summary>
    public static GameAnswer Accepted { get; } = new Accepts();

    /// <summary>The game's cursor refuses it, with its message when it gives one.</summary>
    /// <param name="Message">The game's message.</param>
    public sealed record Refuses(Option<string> Message) : GameAnswer;
}

/// <summary>
/// The game's answers for placements, asked on the main thread (the cursor check is main-thread only) and handed to
/// support as plain data, so support itself runs on any thread. A placement with no answer makes the report pending.
/// </summary>
internal sealed class GameChecks
{
    private readonly Dictionary<Placement, GameAnswer> _answers;

    public GameChecks(IReadOnlyDictionary<Placement, GameAnswer> answers)
    {
        _answers = new Dictionary<Placement, GameAnswer>(answers.Count);
        foreach (var pair in answers)
            _answers.Add(pair.Key, pair.Value);
    }

    /// <summary>No answers yet.</summary>
    public static GameChecks None => new(new Dictionary<Placement, GameAnswer>());

    public Option<GameAnswer> For(Placement placement) =>
        _answers.TryGetValue(placement, out var answer) ? Option.Some(answer) : Option<GameAnswer>.None;
}
