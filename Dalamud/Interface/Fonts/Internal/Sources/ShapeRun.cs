using System.Collections.Generic;
using System.Runtime.InteropServices;

using Dalamud.Utility.Unicode;

using Lumina.Text;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>A run of text to shape.</summary>
/// <remarks>One font, size and style, within one line, without inline objects or controls.</remarks>
internal readonly ref struct ShapeRun
{
    /// <summary>Initializes a new instance of the <see cref="ShapeRun"/> struct.</summary>
    /// <param name="text">The run and the text around it that shaping may look at, such as for Arabic joining.</param>
    /// <param name="begin">Start of the run in <paramref name="text"/>, at a grapheme boundary.</param>
    /// <param name="end">End of the run in <paramref name="text"/>, at a grapheme boundary.</param>
    /// <param name="size">Size in logical pixels.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    public ShapeRun(ReadOnlySpan<uint> text, int begin, int end, float size, float density)
    {
        if (begin < 0 || end < begin || end > text.Length)
            throw new ArgumentOutOfRangeException(nameof(end), end, "Not a range of the text.");
        this.Text = text;
        this.Begin = begin;
        this.End = end;
        this.Size = size;
        this.Density = density;
    }

    /// <summary>Gets the codepoints of the run and the text around it.</summary>
    public ReadOnlySpan<uint> Text { get; }

    /// <summary>Gets the start of the run in <see cref="Text"/>.</summary>
    public int Begin { get; }

    /// <summary>Gets the end of the run in <see cref="Text"/>.</summary>
    public int End { get; }

    /// <summary>Gets the size in logical pixels.</summary>
    public float Size { get; }

    /// <summary>Gets the device pixels per logical pixel.</summary>
    public float Density { get; }

    /// <summary>Gets a part of the run, with the same context.</summary>
    /// <param name="begin">Start in <see cref="Text"/>.</param>
    /// <param name="end">End in <see cref="Text"/>.</param>
    /// <returns>The part.</returns>
    public ShapeRun Slice(int begin, int end) => new(this.Text, begin, end, this.Size, this.Density);

    /// <summary>Gets the run at another size.</summary>
    /// <param name="size">Size in logical pixels.</param>
    /// <returns>The run.</returns>
    public ShapeRun WithSize(float size) => new(this.Text, this.Begin, this.End, size, this.Density);

    /// <summary>Finds the grapheme clusters (UAX #29) of the run.</summary>
    /// <param name="starts">Receives the index into <see cref="Text"/> of the start of each grapheme, in order.</param>
    public void FindGraphemes(List<int> starts)
    {
        starts.Clear();
        var bytes = MemoryMarshal.AsBytes(this.Text[this.Begin..this.End]);
        const UtfEnumeratorFlags utf32 = UtfEnumeratorFlags.Utf32 | UtfEnumeratorFlags.LittleEndian;
        foreach (var boundary in new UnicodeGraphemeClusterEnumerator(bytes, utf32))
        {
            if (boundary < bytes.Length)
                starts.Add(this.Begin + (boundary / 4));
        }

        if (starts.Count == 0 || starts[0] != this.Begin)
            starts.Insert(0, this.Begin);
    }
}
