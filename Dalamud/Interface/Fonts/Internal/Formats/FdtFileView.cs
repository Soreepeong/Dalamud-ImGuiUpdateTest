using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using CommunityToolkit.HighPerformance;

namespace Dalamud.Interface.Fonts.Internal.Formats;

/// <summary>View of a game font file (.fdt).</summary>
internal readonly struct FdtFileView
{
    private readonly ReadOnlyMemory<byte> data;
    private readonly int safeGlyphCount;
    private readonly int safeKerningPairCount;

    /// <summary>Initializes a new instance of the <see cref="FdtFileView"/> struct.</summary>
    /// <param name="data">Contents of the file.</param>
    /// <exception cref="InvalidDataException">If a table does not fit in the data.</exception>
    public FdtFileView(ReadOnlyMemory<byte> data)
    {
        this.data = data;
        try
        {
            if (!this.FileHeader.ValidateSignature()
                || !this.GlyphTableHeader.ValidateSignature()
                || !this.KerningPairTableHeader.ValidateSignature())
                throw new InvalidDataException("Invalid signature");
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("Truncated file", e);
        }

        this.safeGlyphCount = Math.Clamp(
            this.GlyphTableHeader.GlyphCount,
            0,
            (data.Length - this.GlyphsOffset) / Unsafe.SizeOf<FdtGlyph>());
        this.safeKerningPairCount = Math.Clamp(
            Math.Min(this.GlyphTableHeader.KerningPairCount, this.KerningPairTableHeader.Count),
            0,
            (data.Length - this.KerningPairsOffset) / Unsafe.SizeOf<FdtKerningPair>());
    }

    /// <summary>Gets the font header.</summary>
    public ref readonly FdtGlyphTableHeader GlyphTableHeader =>
        ref MemoryMarshal.AsRef<FdtGlyphTableHeader>(this.data.Span[this.FileHeader.GlyphTableHeaderOffset..]);

    /// <summary>Gets the glyphs, sorted by character.</summary>
    public ReadOnlyMemory<FdtGlyph> Glyphs =>
        this.data[this.GlyphsOffset..].Cast<byte, FdtGlyph>()[..this.safeGlyphCount];

    /// <summary>Gets the kerning header.</summary>
    public ref readonly FdtKerningPairTableHeader KerningPairTableHeader =>
        ref MemoryMarshal.AsRef<FdtKerningPairTableHeader>(this.data.Span[this.FileHeader.KerningTableHeaderOffset..]);

    /// <summary>Gets the kerning pairs, sorted by left then right character.</summary>
    public ReadOnlyMemory<FdtKerningPair> PairAdjustments =>
        this.data[this.KerningPairsOffset..].Cast<byte, FdtKerningPair>()[..this.safeKerningPairCount];

    /// <summary>Gets the largest texture index the glyphs use.</summary>
    public int MaxTextureIndex
    {
        get
        {
            var i = 0;
            foreach (ref readonly var g in this.Glyphs.Span)
                i = Math.Max(i, g.TextureIndex);
            return i;
        }
    }

    private ref readonly FdtFileHeader FileHeader => ref MemoryMarshal.AsRef<FdtFileHeader>(this.data.Span);

    private int GlyphsOffset => this.FileHeader.GlyphTableHeaderOffset + Unsafe.SizeOf<FdtGlyphTableHeader>();

    private int KerningPairsOffset =>
        this.FileHeader.KerningTableHeaderOffset + Unsafe.SizeOf<FdtKerningPairTableHeader>();

    /// <summary>Finds the glyph of a character.</summary>
    /// <param name="codepoint">The codepoint.</param>
    /// <param name="glyph">The glyph, if found.</param>
    /// <returns>Whether the font has the character.</returns>
    public bool TryGetGlyph(FdtRune codepoint, out FdtGlyph glyph)
    {
        var glyphs = this.Glyphs.Span;
        var i = glyphs.BinarySearch(codepoint);
        glyph = i >= 0 ? glyphs[i] : default;
        return i >= 0;
    }

    /// <summary>Gets the kerning between two characters.</summary>
    /// <param name="left">The codepoint of the left character.</param>
    /// <param name="right">The codepoint of the right character.</param>
    /// <returns>The offset of the right character in pixels, or 0 if the pair has none.</returns>
    public int GetKerning(FdtRune left, FdtRune right)
    {
        var pairs = this.PairAdjustments.Span;
        var i = pairs.BinarySearch(new PairKey(left, right));
        return i >= 0 ? pairs[i].RightOffset : 0;
    }

    private readonly record struct PairKey(FdtRune Left, FdtRune Right) : IComparable<FdtKerningPair>
    {
        public int CompareTo(FdtKerningPair other) =>
            this.Left == other.Left ? this.Right.CompareTo(other.Right) : this.Left.CompareTo(other.Left);
    }
}
