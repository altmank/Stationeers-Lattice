using System.Text;
using Lattice.Core.Written;

namespace Lattice.Core.Saving;

/// <summary>
/// One plan's change line written a few operations at a time: the text <see cref="PlanFileForm.Append"/> gives, its
/// checksum carried along, and each operation applied to the plan's replay as it is written. Main thread; linear in
/// the operations written.
/// </summary>
internal sealed class LineInParts
{
    private readonly ChangeLine _line;
    private readonly PlanReplay _state;
    private readonly StringBuilder _text;
    private readonly LineChecksum.Running _checksum = new();
    private int _next;

    public LineInParts(ChangeLine line, PlanReplay state)
    {
        _line = line;
        _state = state;
        _text = new StringBuilder(160 + (line.Ops.Count * 120));
        ChangeLineForm.WriteHeader(_text, line);
        _checksum.Add(_text, 0);
    }

    public ChangeLine Line => _line;

    public bool Done => _next == _line.Ops.Count;

    /// <summary>Writes and applies up to <paramref name="units"/> operations; returns how many it did.</summary>
    public int Step(int units)
    {
        var start = _text.Length;
        var did = 0;
        for (; did < units && !Done; did++)
        {
            var op = _line.Ops[_next++];
            ChangeLineForm.WriteOp(_text, _line, op);
            _state.Apply(op);
        }

        _checksum.Add(_text, start);
        if (Done)
            _state.Reached(_line);
        return did;
    }

    /// <summary>Ends the line once <see cref="Done"/>: its text as appended to the file, line break included. Called once.</summary>
    public string End() => _text.Append(" #").Append(_checksum.Value).Append('\n').ToString();
}
