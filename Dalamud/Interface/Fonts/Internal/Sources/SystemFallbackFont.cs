using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Utility;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>Any installed font that has a codepoint, as Windows' system font fallback picks it.</summary>
/// <remarks>The last resort of the default font chain. Fonts are picked per codepoint, for a locale. Glyph ids are
/// (face index &lt;&lt; 16) | the face's glyph id, for at most 256 faces.</remarks>
internal sealed unsafe class SystemFallbackFont : ManagedFont
{
    private const int MaxFaces = 256;

    private readonly string locale;
    private readonly ColorGlyphRasterizer? colorGlyphs;
    private readonly DWRITE_FONT_WEIGHT weight;
    private readonly DWRITE_FONT_STYLE style;
    private readonly Dictionary<uint, uint> glyphs = [];
    private readonly Dictionary<string, int> faceIndices = [];
    private readonly List<DirectWriteFont> faces = [];
    private IDWriteFontFallback* fallback;
    private uint[] leafBuffer = new uint[64];

    /// <summary>Initializes a new instance of the <see cref="SystemFallbackFont"/> class.</summary>
    /// <param name="locale">Locale name for the fallback choices, such as "ja-JP".</param>
    /// <param name="colorGlyphs">Draws color glyphs, or null to draw their outlines.</param>
    /// <param name="bold">Whether to pick bold faces.</param>
    /// <param name="italic">Whether to pick italic faces.</param>
    /// <remarks>The locale decides between Han glyph variants. Windows simulates the styles of fonts without faces for
    /// them.</remarks>
    public SystemFallbackFont(
        string locale, ColorGlyphRasterizer? colorGlyphs = null, bool bold = false, bool italic = false)
        : base($"System fallback ({locale}){(bold ? " bold" : null)}{(italic ? " italic" : null)}")
    {
        this.locale = locale;
        this.colorGlyphs = colorGlyphs;
        this.weight = bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL;
        this.style = italic ? DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL;
        fixed (IDWriteFontFallback** p = &this.fallback)
            ((IDWriteFactory2*)DirectWriteFont.Factory)->GetSystemFontFallback(p).ThrowOnError();
    }

    /// <summary>Finalizes an instance of the <see cref="SystemFallbackFont"/> class.</summary>
    ~SystemFallbackFont()
    {
        if (this.fallback is not null)
            this.fallback->Release();
        this.fallback = null;
    }

    /// <inheritdoc/>
    public override bool CanShape => true;

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
        this.faces.Count > 0
            ? this.faces[0].GetMetrics(size, density)
            : FontMetrics.Fallback(size);

    /// <inheritdoc/>
    public override void GetGlyphIndices(ReadOnlySpan<uint> codepoints, Span<uint> glyphs)
    {
        for (var i = 0; i < codepoints.Length; i++)
        {
            if (!this.glyphs.TryGetValue(codepoints[i], out var g))
                this.glyphs.Add(codepoints[i], g = this.Map(codepoints[i]));
            glyphs[i] = g;
        }
    }

    /// <inheritdoc/>
    public override void GetGlyphMetrics(
        ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics)
    {
        var leaf = this.Leaf(glyphs);
        for (var begin = 0; begin < glyphs.Length;)
        {
            var end = RunEnd(glyphs, begin, 16);
            this.GetFace(glyphs[begin]).GetGlyphMetrics(leaf[begin..end], size, density, metrics[begin..end]);
            begin = end;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Each grapheme is shaped with the face of its first codepoint.</remarks>
    public override void Shape(ShapeRun run, ShapedGlyphList output)
    {
        if (run.End <= run.Begin)
            return;
        var graphemes = this.FindGraphemes(run);
        var previousFace = -1;
        var previousLast = 0u;
        var previousEnd = -1;
        Span<uint> glyph = stackalloc uint[1];
        for (var k = 0; k < graphemes.Count;)
        {
            var begin = graphemes[k];
            var face = this.FaceOf(run.Text[begin], glyph);
            var kb = k++;
            while (k < graphemes.Count && this.FaceOf(run.Text[graphemes[k]], glyph) == face)
                k++;

            var end = k < graphemes.Count ? graphemes[k] : run.End;
            var first = output.Count;
            if (face < 0)
            {
                for (var g = kb; g < k; g++)
                    output.Add(0, graphemes[g], 0);
            }
            else
            {
                this.faces[face].Shape(run.Slice(begin, end), output);
                var added = output.Span[first..];
                for (var i = 0; i < added.Length; i++)
                {
                    if (added[i].Glyph is not 0 and < 1u << 16)
                        added[i].Glyph |= (uint)face << 16;
                    else
                        added[i].Glyph = 0;
                }

                if (previousFace >= 0 && previousFace != face && previousEnd == first && first > 0)
                {
                    output.Span[first - 1].Advance += KernAcross(
                        this.faces[previousFace],
                        run.Size,
                        this.faces[face],
                        run.Size,
                        previousLast,
                        run.Text[begin],
                        run.Density);
                }
            }

            previousFace = face;
            previousLast = run.Text[graphemes[k - 1]];
            previousEnd = output.Count;
        }
    }

    /// <summary>Gets the installed font a glyph comes from.</summary>
    /// <param name="glyph">The glyph.</param>
    /// <returns>The font.</returns>
    public DirectWriteFont GetFace(uint glyph) => this.faces[(int)(glyph >> 16)];

    /// <inheritdoc/>
    public override void GetGlyphImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, Span<GlyphImage> images)
    {
        var leaf = this.Leaf(glyphs);
        for (var begin = 0; begin < glyphs.Length;)
        {
            var end = RunEnd(glyphs, begin, 16);
            this.GetFace(glyphs[begin]).GetGlyphImages(
                leaf[begin..end],
                subpixelSteps[begin..end],
                size,
                density,
                embolden,
                subpixel,
                images[begin..end]);
            begin = end;
        }
    }

    /// <summary>Gets the English name of localized names, or the first.</summary>
    /// <param name="names">The names.</param>
    /// <returns>The name.</returns>
    internal static string EnglishNameOf(IDWriteLocalizedStrings* names)
    {
        uint index;
        BOOL exists;
        fixed (char* en = "en-us")
        {
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
    protected override ManagedFont CreateStyled(bool bold, bool italic) =>
        new SystemFallbackFont(this.locale, this.colorGlyphs, bold, italic);

    private int FaceOf(uint codepoint, Span<uint> glyph)
    {
        this.GetGlyphIndices([codepoint], glyph);
        return glyph[0] == 0 ? -1 : (int)(glyph[0] >> 16);
    }

    private Span<uint> Leaf(ReadOnlySpan<uint> glyphs)
    {
        var leaf = Rent(ref this.leafBuffer, glyphs.Length);
        for (var i = 0; i < glyphs.Length; i++)
            leaf[i] = glyphs[i] & 0xFFFF;
        return leaf;
    }

    private uint Map(uint codepoint)
    {
        if (!Rune.TryCreate(codepoint, out var rune) || this.fallback is null)
            return 0;

        var pText = stackalloc char[2];
        var length = rune.EncodeToUtf16(new(pText, 2));
        using var font = default(ComPtr<IDWriteFont>);
        fixed (char* pLocale = this.locale)
        {
            var source = new DirectWriteTextSource(pText, (uint)length, pLocale);
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
                return 0;
            }
        }

        BOOL exists;
        if (font.Get()->HasCharacter(codepoint, &exists).FAILED || !exists)
            return 0;

        var faceIndex = this.FaceIndexOf(font.Get());
        if (faceIndex < 0)
            return 0;

        Span<uint> glyph = stackalloc uint[1];
        this.faces[faceIndex].GetGlyphIndices([codepoint], glyph);
        return glyph[0] == 0 ? 0 : ((uint)faceIndex << 16) | glyph[0];
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
            $"{familyName}/{(int)font->GetWeight()}/{(int)font->GetStretch()}/{(int)font->GetStyle()}/{(int)font->GetSimulations()}";
        if (this.faceIndices.TryGetValue(key, out var index))
            return index;
        if (this.faces.Count >= MaxFaces)
            return -1;

        using var face = default(ComPtr<IDWriteFontFace>);
        if (font->CreateFontFace(face.GetAddressOf()).FAILED)
            return -1;
        index = this.faces.Count;
        this.faces.Add(new(key, face.Get(), this.colorGlyphs));
        this.faceIndices.Add(key, index);
        return index;
    }
}
