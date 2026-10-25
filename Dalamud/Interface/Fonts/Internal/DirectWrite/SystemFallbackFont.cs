using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Fonts.Internal.DirectWrite;

/// <summary>Any installed font that has a codepoint, as Windows' system font fallback picks it.</summary>
/// <remarks>Faces are picked per grapheme, for a locale. Glyph ids are (face index &lt;&lt; 16) | the face's glyph id,
/// for at most 256 faces. The metrics and glyph 0 come from the face of a character of the locale's main script.
/// </remarks>
internal sealed unsafe class SystemFallbackFont : MultiSourceFont
{
    private const int MaxFaces = 256;

    private readonly string locale;
    private readonly DirectWriteFontSources? sources;
    private readonly DWRITE_FONT_WEIGHT weight;
    private readonly DWRITE_FONT_STYLE style;
    private readonly Dictionary<uint, uint> glyphsByCodepoint = [];
    private readonly Dictionary<string, int> faceIndices = [];
    private readonly List<DirectWriteFont> faces = [];
    private IDWriteFontFallback* fallback;
    private uint[] leafBuffer = new uint[64];

    // The face of the metrics and .notdef, -1 if none was found, or int.MinValue until looked for.
    private int metricsFace = int.MinValue;

    /// <summary>Initializes a new instance of the <see cref="SystemFallbackFont"/> class.</summary>
    /// <param name="locale">Locale name for the fallback choices and for shaping, such as "ja-JP".</param>
    /// <param name="sources">The sources whose faces to share, or null for faces of its own. Those draw the outlines
    /// of color glyphs, and rasterize all glyphs at once.</param>
    /// <param name="bold">Whether to pick bold faces.</param>
    /// <param name="italic">Whether to pick italic faces.</param>
    /// <remarks>The locale decides between fonts, and between glyph forms of a font, such as Han variants. Windows
    /// simulates the styles of fonts without faces for them.</remarks>
    public SystemFallbackFont(
        string locale, DirectWriteFontSources? sources = null, bool bold = false, bool italic = false)
        : base($"System fallback ({locale}){(bold ? " bold" : null)}{(italic ? " italic" : null)}")
    {
        this.locale = locale;
        this.sources = sources;
        this.weight = bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL;
        this.style = italic ? DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL;
        fixed (IDWriteFontFallback** p = &this.fallback)
            DirectWriteUtilities.Factory->GetSystemFontFallback(p).ThrowOnError();
    }

    /// <summary>Finalizes an instance of the <see cref="SystemFallbackFont"/> class.</summary>
    ~SystemFallbackFont()
    {
        if (this.fallback is not null)
            this.fallback->Release();
        this.fallback = null;
    }

    /// <summary>Makes Windows enumerate the installed fonts now in the background, instead of on first use.</summary>
    public static void Preload() =>
        _ = Task.Run(() =>
        {
            try
            {
                var font = new SystemFallbackFont("en-US");
                Span<uint> glyph = stackalloc uint[1];
                font.GetGlyphIndices([0x3042], glyph);
            }
            catch
            {
                // nothing to warm
            }
        });

    /// <inheritdoc/>
    public override FontMetrics GetMetrics(float size, float density) =>
        this.FaceOf(0) is { } face ? face.GetMetrics(size, density) : FontMetrics.Fallback(size);

    /// <inheritdoc/>
    public override void GetGlyphIndices(ReadOnlySpan<uint> codepoints, Span<uint> glyphs)
    {
        for (var i = 0; i < codepoints.Length; i++)
        {
            if (!this.glyphsByCodepoint.TryGetValue(codepoints[i], out var g))
                this.glyphsByCodepoint.Add(codepoints[i], g = this.Map(codepoints[i]));
            glyphs[i] = g;
        }
    }

    /// <inheritdoc/>
    public override void GetGlyphMetrics(
        ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics)
    {
        var pooledLeaf = this.Leaf(glyphs);
        var leaf = pooledLeaf.Span;
        for (var begin = 0; begin < glyphs.Length;)
        {
            var end = FaceRunEnd(glyphs, begin);
            if (this.FaceOf(glyphs[begin]) is { } face)
                face.GetGlyphMetrics(leaf[begin..end], size, density, metrics[begin..end]);
            else
                metrics[begin..end].Clear();
            begin = end;
        }

        pooledLeaf.Recycle();
    }

    /// <inheritdoc/>
    /// <remarks>Faces are the sources of <see cref="MultiSourceFont"/>.</remarks>
    public override void Shape(ShapeRun run, ShapedGlyphList output) => this.ShapeBySource(run, output);

    /// <inheritdoc/>
    public override void GetGlyphImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, Span<GlyphImage> images)
    {
        var pooledLeaf = this.Leaf(glyphs);
        var leaf = pooledLeaf.Span;
        for (var begin = 0; begin < glyphs.Length;)
        {
            var end = FaceRunEnd(glyphs, begin);
            if (this.FaceOf(glyphs[begin]) is { } face)
            {
                face.GetGlyphImages(
                    leaf[begin..end],
                    subpixelSteps[begin..end],
                    size,
                    density,
                    embolden,
                    subpixel,
                    images[begin..end]);
            }
            else
            {
                images[begin..end].Fill(new() { IsFinal = true });
            }

            begin = end;
        }

        pooledLeaf.Recycle();
    }

    /// <summary>Gets the English name of localized names, or the first.</summary>
    /// <param name="names">The names.</param>
    /// <returns>The name.</returns>
    internal static string EnglishNameOf(IDWriteLocalizedStrings* names)
    {
        uint index;
        fixed (char* en = "en-us")
        {
            BOOL exists;
            if (names->FindLocaleName(en, &index, &exists).FAILED || !exists)
                index = 0;
        }

        uint length;
        if (names->GetStringLength(index, &length).FAILED)
            return "?";
        var buf = new char[length + 1];
        fixed (char* p = buf)
        {
            if (names->GetString(index, p, length + 1).FAILED)
                return "?";
        }

        return new(buf, 0, (int)length);
    }

    /// <inheritdoc/>
    protected override Font CreateStyled(bool bold, bool italic) =>
        new SystemFallbackFont(this.locale, this.sources, bold, italic);

    /// <inheritdoc/>
    protected override void PickSources(ReadOnlySpan<uint> firsts, Span<int> pickedSources)
    {
        Span<uint> glyph = stackalloc uint[1];
        for (var k = 0; k < firsts.Length; k++)
        {
            this.GetGlyphIndices(firsts.Slice(k, 1), glyph);
            pickedSources[k] = glyph[0] == 0 ? -1 : (int)(glyph[0] >> 16);
        }
    }

    /// <inheritdoc/>
    protected override bool HasAll(int source, ReadOnlySpan<uint> codepoints, Span<uint> glyphs)
    {
        this.faces[source].GetGlyphIndices(codepoints, glyphs);
        foreach (var g in glyphs[..codepoints.Length])
        {
            if (g is 0 or >= 1u << 16)
                return false;
        }

        return true;
    }

    /// <inheritdoc/>
    protected override int SourceCount() => this.faces.Count;

    /// <inheritdoc/>
    /// <remarks>The face Windows picks for the whole grapheme.</remarks>
    protected override int AddSourceFor(ReadOnlySpan<uint> codepoints) => this.FaceFor(codepoints);

    /// <inheritdoc/>
    /// <remarks>Missing graphemes are glyph 0, with no advance.</remarks>
    protected override void ShapeWith(int source, ShapeRun run, ReadOnlySpan<int> graphemes, ShapedGlyphList output)
    {
        if (source < 0)
        {
            foreach (var g in graphemes)
                output.Add(0, g, 0);
            return;
        }

        var first = output.Count;
        this.faces[source].Shape(run, output);
        foreach (ref var g in output.Span[first..])
            g.Glyph = g.Glyph is not 0 and < 1u << 16 ? g.Glyph | ((uint)source << 16) : 0;
    }

    /// <inheritdoc/>
    protected override Font KerningFontOf(int source, float size, out float sourceSize)
    {
        sourceSize = size;
        return this.faces[source];
    }

    // The end of the glyphs from one face. Glyph 0 is a run of its own, from the face of the metrics.
    private static int FaceRunEnd(ReadOnlySpan<uint> glyphs, int begin)
    {
        var end = begin + 1;
        if (glyphs[begin] == 0)
        {
            while (end < glyphs.Length && glyphs[end] == 0)
                end++;
            return end;
        }

        while (end < glyphs.Length && glyphs[end] != 0 && glyphs[end] >> 16 == glyphs[begin] >> 16)
            end++;
        return end;
    }

    // The face of a glyph. Glyph 0 is the .notdef glyph of the face of the metrics, null if there is none.
    private DirectWriteFont? FaceOf(uint glyph)
    {
        if (glyph != 0)
            return this.faces[(int)(glyph >> 16)];
        if (this.metricsFace == int.MinValue)
            this.metricsFace = this.FindMetricsFace();
        return this.metricsFace >= 0 ? this.faces[this.metricsFace] : null;
    }

    // The face Windows picks for a character of the locale's main script, or -1.
    private int FindMetricsFace()
    {
        var probe = this.locale.Length >= 2
                        ? (char.ToLowerInvariant(this.locale[0]), char.ToLowerInvariant(this.locale[1])) switch
                        {
                            ('j', 'a') => 0x3042u,
                            ('z', 'h') => 0x4E2Du,
                            ('k', 'o') => 0xAC00u,
                            _ => 'A',
                        }
                        : 'A';
        Span<uint> codepoint = stackalloc uint[1];
        Span<uint> glyph = stackalloc uint[1];
        codepoint[0] = probe;
        this.GetGlyphIndices(codepoint, glyph);
        return glyph[0] == 0 ? -1 : (int)(glyph[0] >> 16);
    }

    private PooledSpan<uint> Leaf(ReadOnlySpan<uint> glyphs)
    {
        var pooled = PooledSpan<uint>.FromBuffer(ref this.leafBuffer, glyphs.Length);
        var leaf = pooled.Span;
        for (var i = 0; i < glyphs.Length; i++)
            leaf[i] = glyphs[i] & 0xFFFF;
        return pooled;
    }

    private uint Map(uint codepoint)
    {
        Span<uint> one = stackalloc uint[1];
        one[0] = codepoint;
        var faceIndex = this.FaceFor(one);
        if (faceIndex < 0)
            return 0;
        Span<uint> glyph = stackalloc uint[1];
        this.faces[faceIndex].GetGlyphIndices(one, glyph);
        return glyph[0] == 0 ? 0 : ((uint)faceIndex << 16) | glyph[0];
    }

    // The face Windows picks for text that has its first codepoint, or -1.
    private int FaceFor(ReadOnlySpan<uint> codepoints)
    {
        if (this.fallback is null || codepoints.IsEmpty || codepoints.Length > 64)
            return -1;
        var text = stackalloc char[codepoints.Length * 2];
        var length = 0;
        foreach (var c in codepoints)
        {
            if (!Rune.TryCreate(c, out var rune))
                return -1;
            length += rune.EncodeToUtf16(new(text + length, 2));
        }

        using var font = default(ComPtr<IDWriteFont>);
        fixed (char* pLocale = this.locale)
        {
            var source = new DirectWriteTextSource(text, (uint)length, pLocale);
            uint mappedLength;
            float scale;
            if (this.fallback->MapCharacters(
                    (IDWriteTextAnalysisSource*)&source,
                    0,
                    (uint)length,
                    null,
                    null,
                    this.weight,
                    this.style,
                    DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                    &mappedLength,
                    font.GetAddressOf(),
                    &scale).FAILED || font.Get() is null)
            {
                return -1;
            }
        }

        BOOL exists;
        if (font.Get()->HasCharacter(codepoints[0], &exists).FAILED || !exists)
            return -1;
        return this.FaceIndexOf(font.Get());
    }

    private int FaceIndexOf(IDWriteFont* font)
    {
        using var family = default(ComPtr<IDWriteFontFamily>);
        using var names = default(ComPtr<IDWriteLocalizedStrings>);
        var familyName = font->GetFontFamily(family.GetAddressOf()).FAILED ||
                         family.Get()->GetFamilyNames(names.GetAddressOf()).FAILED
                             ? "?"
                             : EnglishNameOf(names.Get());

        var key =
            $"{familyName}/{(int)font->GetWeight()}/{(int)font->GetStretch()}/{(int)font->GetStyle()}/" +
            $"{(int)font->GetSimulations()}";
        if (this.faceIndices.TryGetValue(key, out var index))
            return index;
        if (this.faces.Count >= MaxFaces)
            return -1;

        using var face = default(ComPtr<IDWriteFontFace>);
        if (font->CreateFontFace(face.GetAddressOf()).FAILED)
            return -1;
        var sims = face.Get()->GetSimulations();
        var shared = this.sources?.GetFace(key, face.Get(), sims) ?? new DirectWriteFontFace(key, face.Get(), sims);
        index = this.faces.Count;
        this.faces.Add(new(key, shared, this.locale));
        this.faceIndices.Add(key, index);
        return index;
    }
}
