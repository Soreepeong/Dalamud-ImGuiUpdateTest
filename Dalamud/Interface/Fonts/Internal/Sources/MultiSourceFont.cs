using System.Runtime.InteropServices;

using Dalamud.Utility;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>A font that takes each grapheme from one of its source fonts.</summary>
/// <remarks>A grapheme goes to the source of its first codepoint, unless that source lacks another codepoint of it
/// that shows. Then the first source that has all of them takes it. Graphemes of one source are shaped together, and
/// pairs across sources are kerned with <see cref="Font.KernAcross"/>.</remarks>
internal abstract class MultiSourceFont : Font
{
    // Longer graphemes keep the source of their first codepoint.
    private const int MaxPickedLength = 32;

    private uint[] firstBuffer = new uint[64];
    private int[] sourceBuffer = new int[64];

    /// <summary>Initializes a new instance of the <see cref="MultiSourceFont"/> class.</summary>
    /// <param name="name">Name for debugging.</param>
    protected MultiSourceFont(string name)
        : base(name)
    {
    }

    /// <summary>Tests whether a codepoint is a default ignorable one, which shows nothing even without a glyph.
    /// </summary>
    /// <param name="c">The codepoint.</param>
    /// <returns>Whether it has the Default_Ignorable_Code_Point property.</returns>
    public static bool IsDefaultIgnorable(uint c) =>
        c switch
        {
            < 0xAD => false,
            0xAD or 0x34F or 0x61C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFEFF or 0xFFA0 => true,
            >= 0x180B and <= 0x180F => true,
            >= 0x200B and <= 0x200F => true,
            >= 0x202A and <= 0x202E => true,
            >= 0x2060 and <= 0x206F => true,
            >= 0xFE00 and <= 0xFE0F => true,
            >= 0xFFF0 and <= 0xFFF8 => true,
            >= 0x1BCA0 and <= 0x1BCA3 => true,
            >= 0x1D173 and <= 0x1D17A => true,
            >= 0xE0000 and <= 0xE0FFF => true,
            _ => false,
        };

    /// <summary>Picks the source of each grapheme from its first codepoint.</summary>
    /// <param name="firsts">The first codepoint of each grapheme.</param>
    /// <param name="sources">Receives the source of each, or -1 if no source has it.</param>
    protected abstract void PickSources(ReadOnlySpan<uint> firsts, Span<int> sources);

    /// <summary>Tests whether a source has every codepoint of a grapheme.</summary>
    /// <param name="source">The source.</param>
    /// <param name="codepoints">The codepoints, other than default ignorable ones.</param>
    /// <param name="glyphs">Scratch, as long as <paramref name="codepoints"/>.</param>
    /// <returns>Whether it has all of them.</returns>
    protected abstract bool HasAll(int source, ReadOnlySpan<uint> codepoints, Span<uint> glyphs);

    /// <summary>Gets the number of sources, for <see cref="HasAll"/>.</summary>
    /// <returns>The count.</returns>
    protected abstract int SourceCount();

    /// <summary>Finds a source for a grapheme whose first codepoint's source lacks another of its codepoints, before
    /// the other sources are tried in order.</summary>
    /// <param name="codepoints">The codepoints, other than default ignorable ones.</param>
    /// <returns>The source, which may be new, or -1. The default has none.</returns>
    protected virtual int AddSourceFor(ReadOnlySpan<uint> codepoints) => -1;

    /// <summary>Shapes graphemes of one source, and appends their glyphs with the source in the ids.</summary>
    /// <param name="source">The source, or -1 for graphemes no source has.</param>
    /// <param name="run">The graphemes.</param>
    /// <param name="graphemes">The grapheme starts of <paramref name="run"/>.</param>
    /// <param name="output">Receives the glyphs.</param>
    protected abstract void ShapeWith(int source, ShapeRun run, ReadOnlySpan<int> graphemes, ShapedGlyphList output);

    /// <summary>Gets the font and size to kern a source with across sources.</summary>
    /// <param name="source">The source.</param>
    /// <param name="size">The size of the run.</param>
    /// <param name="sourceSize">Receives the size of the source's glyphs.</param>
    /// <returns>The font, or null to not kern pairs with it.</returns>
    protected abstract Font? KerningFontOf(int source, float size, out float sourceSize);

    /// <summary>Shapes a run grapheme by grapheme, each with its source.</summary>
    /// <param name="run">The run.</param>
    /// <param name="output">Receives the glyphs.</param>
    protected void ShapeBySource(ShapeRun run, ShapedGlyphList output)
    {
        if (run.End <= run.Begin)
            return;
        var graphemeList = this.FindGraphemes(run);
        var graphemes = CollectionsMarshal.AsSpan(graphemeList);
        var count = graphemes.Length;
        var pooledFirsts = PooledSpan<uint>.FromBuffer(ref this.firstBuffer, count);
        var pooledSources = PooledSpan<int>.FromBuffer(ref this.sourceBuffer, count);
        var firsts = pooledFirsts.Span;
        var sources = pooledSources.Span;
        for (var k = 0; k < count; k++)
            firsts[k] = run.Text[graphemes[k]];
        this.PickSources(firsts, sources);
        for (var k = 0; k < count; k++)
        {
            var end = k + 1 < count ? graphemes[k + 1] : run.End;
            if (end - graphemes[k] > 1)
                sources[k] = this.PickSource(run.Text[graphemes[k]..end], sources[k]);
        }

        var previous = -1;
        var previousFirst = -1;
        var previousLast = 0u;
        for (var k = 0; k < count;)
        {
            var source = sources[k];
            var kb = k++;
            while (k < count && sources[k] == source)
                k++;

            var begin = graphemes[kb];
            var end = k < count ? graphemes[k] : run.End;
            var first = output.Count;
            this.ShapeWith(source, run.Slice(begin, end), graphemes[kb..k], output);
            if (previous >= 0 && source >= 0 && first > previousFirst)
                this.KernAt(previous, source, previousLast, run.Text[begin], run, output, first);

            previous = source;
            previousFirst = first;
            previousLast = run.Text[graphemes[k - 1]];
        }

        pooledFirsts.Recycle();
        pooledSources.Recycle();
    }

    // Kerns the last glyph before a group with the first of it, if both sources kern.
    private void KernAt(
        int left, int right, uint first, uint second, ShapeRun run, ShapedGlyphList output, int at)
    {
        if (this.KerningFontOf(left, run.Size, out var leftSize) is not { } leftFont ||
            this.KerningFontOf(right, run.Size, out var rightSize) is not { } rightFont)
        {
            return;
        }

        output.Span[at - 1].Advance += KernAcross(leftFont, leftSize, rightFont, rightSize, first, second, run.Density);
    }

    // The source for a grapheme of several codepoints. Rarely differs from the one of its first codepoint.
    private int PickSource(ReadOnlySpan<uint> grapheme, int source)
    {
        if (grapheme.Length > MaxPickedLength)
            return source;
        Span<uint> needed = stackalloc uint[MaxPickedLength];
        Span<uint> glyphs = stackalloc uint[MaxPickedLength];
        var n = 0;
        foreach (var c in grapheme)
        {
            if (n == 0 || !IsDefaultIgnorable(c))
                needed[n++] = c;
        }

        if (n == 1 || (source >= 0 && this.HasAll(source, needed[..n], glyphs)))
            return source;
        var added = this.AddSourceFor(needed[..n]);
        if (added >= 0 && added != source && this.HasAll(added, needed[..n], glyphs))
            return added;
        for (var s = 0; s < this.SourceCount(); s++)
        {
            if (s != source && this.HasAll(s, needed[..n], glyphs))
                return s;
        }

        return source;
    }
}
