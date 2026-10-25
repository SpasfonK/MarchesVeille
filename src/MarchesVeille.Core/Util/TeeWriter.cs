using System.Text;

namespace MarchesVeille.Core.Util;

/// <summary>Écrit simultanément sur plusieurs TextWriter (console + fichier journal).</summary>
public sealed class TeeWriter : TextWriter
{
    private readonly TextWriter[] _writers;
    public TeeWriter(params TextWriter[] writers) => _writers = writers;
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) { foreach (var w in _writers) w.Write(value); }
    public override void Write(string? value) { foreach (var w in _writers) w.Write(value); }
    public override void Flush() { foreach (var w in _writers) w.Flush(); }
}
