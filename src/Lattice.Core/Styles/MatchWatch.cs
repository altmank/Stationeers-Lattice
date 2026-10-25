using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>
/// A match judged per batch, on the batch's signature (a renderer's claim). While it is held its leaves join every
/// generation; disposing it gives them back, and the next generation leaves them out.
/// </summary>
internal interface IMatchWatch : IDisposable
{
    /// <summary>
    /// Rises whenever the compiled form changes (a generation begun or finished); the same from frame to frame
    /// otherwise, so a reader works out its batches again only when this or the batches change.
    /// </summary>
    long Generation { get; }

    /// <summary>Whether the match holds for the batch's things; false once disposed. Allocates nothing.</summary>
    bool Holds(Batch batch);
}

/// <summary>
/// One watched match, compiled once per live generation: a batch is judged by the form compiled against its signature's
/// generation. A signature older than one of the match's leaves reads that leaf as false.
/// </summary>
internal sealed class MatchWatch(StyleMatch match, Action<MatchWatch> release) : IMatchWatch
{
    private readonly List<(Generation Of, Compiled Match)> _forms = new(2);
    private bool _disposed;

    public StyleMatch Match => match;

    public long Generation { get; private set; }

    public bool Holds(Batch batch)
    {
        var signature = batch.Key.Signature;
        foreach (var (of, compiled) in _forms)
            if (ReferenceEquals(of, signature.Generation))
                return compiled.Holds(signature);
        return false;
    }

    /// <summary>Takes the match's form for a generation.</summary>
    public void Put(Generation generation, Compiled compiled)
    {
        _forms.Add((generation, compiled));
        Generation++;
    }

    /// <summary>Keeps only the form for the generation that now stands alone.</summary>
    public void Keep(Generation generation)
    {
        for (var i = _forms.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(_forms[i].Of, generation))
                _forms.RemoveAt(i);
        Generation++;
    }

    /// <summary>Holds for nothing from now on.</summary>
    public void Drop()
    {
        _forms.Clear();
        Generation++;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Drop();
        release(this);
    }
}
