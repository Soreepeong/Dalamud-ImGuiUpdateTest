using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Utility;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>An OpenType/TrueType font file (or one face of a collection), rasterized with DirectWrite.</summary>
/// <remarks>The size is the line height from the hhea table, line gap included, as with the game's fonts. Fonts that
/// keep their spacing in the line gap (AXIS Basic ProN) are then as large as those that keep it in the ascent and
/// descent (Noto Sans CJK).</remarks>
internal sealed unsafe partial class DirectWriteFont : ManagedFont
{
    private static readonly Lazy<nint> FactoryLazy = new(CreateFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<nint> LoaderLazy = new(CreateLoader, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly char* EmptyLocale = (char*)NativeMemory.AllocZeroed(sizeof(char));

    private readonly ColorGlyphRasterizer? colorGlyphs;
    private readonly float slant;
    private float designAscent;
    private float designDescent;
    private float designLineGap;
    private float unitsPerEm;
    private float underlinePosition;
    private float underlineThickness;
    private float strikethroughPosition;
    private float strikethroughThickness;

    private IDWriteFontFace* face;
    private bool hasColorGlyphs;
    private ushort[] indexBuffer = new ushort[64];
    private DWRITE_GLYPH_METRICS[] metricsBuffer = new DWRITE_GLYPH_METRICS[64];

    /// <summary>Initializes a new instance of the <see cref="DirectWriteFont"/> class.</summary>
    /// <param name="name">Name for debugging.</param>
    /// <param name="data">The font file, copied.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="colorGlyphs">Draws color glyphs, or null to draw their outlines.</param>
    public DirectWriteFont(string name, ReadOnlySpan<byte> data, int fontNo, ColorGlyphRasterizer? colorGlyphs = null)
        : base(name)
    {
        this.colorGlyphs = colorGlyphs;
        try
        {
            var factory = Factory;
            using var file = default(ComPtr<IDWriteFontFile>);

            fixed (byte* p = data)
            {
                ((IDWriteInMemoryFontFileLoader*)LoaderLazy.Value)->CreateInMemoryFontFileReference(
                    (IDWriteFactory*)factory,
                    p,
                    (uint)data.Length,
                    null,
                    file.GetAddressOf()).ThrowOnError();
            }

            BOOL supported;
            DWRITE_FONT_FILE_TYPE fileType;
            DWRITE_FONT_FACE_TYPE faceType;
            uint faceCount;
            file.Get()->Analyze(&supported, &fileType, &faceType, &faceCount).ThrowOnError();
            if (!supported)
                throw new NotSupportedException($"{name}: not a font file DirectWrite supports.");
            if (fontNo >= faceCount)
                throw new ArgumentOutOfRangeException(nameof(fontNo), fontNo, $"{name} has {faceCount} faces.");

            fixed (IDWriteFontFace** pFace = &this.face)
            {
                var pFile = file.Get();
                factory->CreateFontFace(
                        faceType,
                        1,
                        &pFile,
                        (uint)fontNo,
                        DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE,
                        pFace)
                    .ThrowOnError();
            }

            this.ReadMetrics();
        }
        catch
        {
            this.ReleaseResources();
            throw;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="DirectWriteFont"/> class.</summary>
    /// <param name="name">Name for debugging.</param>
    /// <param name="face">A font face, such as one of the system fonts.</param>
    /// <param name="colorGlyphs">Draws color glyphs, or null to draw their outlines.</param>
    /// <param name="slant">Horizontal shift per pixel above the baseline to slant the outlines by, for synthetic
    /// italic.</param>
    /// <remarks>The new instance adds a reference to <paramref name="face"/>.</remarks>
    public DirectWriteFont(
        string name, IDWriteFontFace* face, ColorGlyphRasterizer? colorGlyphs = null, float slant = 0)
        : base(name)
    {
        this.colorGlyphs = colorGlyphs;
        this.slant = slant;
        this.face = face;
        face->AddRef();
        try
        {
            this.ReadMetrics();
        }
        catch
        {
            this.ReleaseResources();
            throw;
        }
    }

    /// <summary>Finalizes an instance of the <see cref="DirectWriteFont"/> class.</summary>
    ~DirectWriteFont() => this.ReleaseResources();

    /// <inheritdoc/>
    public override bool CanShape => true;

    /// <summary>Gets the shared DirectWrite factory.</summary>
    internal static IDWriteFactory5* Factory => (IDWriteFactory5*)FactoryLazy.Value;

    /// <inheritdoc/>
    public override FontMetrics GetMetrics(float size, float density)
    {
        var scale = this.Scale(size);
        return new(
            MathF.Round(this.designAscent * scale),
            MathF.Round(this.designDescent * scale),
            size, // The line gap takes up the rounding.
            Math.Max(1f, MathF.Round(this.underlinePosition * scale)),
            Math.Max(1f, MathF.Round(this.underlineThickness * scale)),
            MathF.Round(this.strikethroughPosition * scale),
            Math.Max(1f, MathF.Round(this.strikethroughThickness * scale)));
    }

    /// <inheritdoc/>
    public override void GetGlyphIndices(ReadOnlySpan<uint> codepoints, Span<uint> glyphs)
    {
        var indices = Rent(ref this.indexBuffer, codepoints.Length);
        fixed (uint* pCodepoints = codepoints)
        {
            fixed (ushort* pIndices = indices)
                this.face->GetGlyphIndices(pCodepoints, (uint)codepoints.Length, pIndices).ThrowOnError();
        }

        for (var i = 0; i < codepoints.Length; i++)
            glyphs[i] = indices[i];
    }

    /// <inheritdoc/>
    public override void GetGlyphMetrics(
        ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics)
    {
        var indices = Rent(ref this.indexBuffer, glyphs.Length);
        for (var i = 0; i < glyphs.Length; i++)
            indices[i] = (ushort)glyphs[i];
        var design = Rent(ref this.metricsBuffer, glyphs.Length);
        fixed (ushort* pIndices = indices)
        {
            fixed (DWRITE_GLYPH_METRICS* pDesign = design)
                this.face->GetDesignGlyphMetrics(pIndices, (uint)glyphs.Length, pDesign, false).ThrowOnError();
        }

        var scale = this.Scale(size);
        for (var i = 0; i < glyphs.Length; i++)
        {
            ref readonly var m = ref design[i];
            var advance = m.advanceWidth * scale;
            var x0 = m.leftSideBearing * scale;
            var x1 = ((int)m.advanceWidth - m.rightSideBearing) * scale;
            var y0 = (m.topSideBearing - m.verticalOriginY) * scale;
            var y1 = ((int)m.advanceHeight - m.bottomSideBearing - m.verticalOriginY) * scale;
            // Slanting moves what is below the baseline left, and what is above it right.
            metrics[i] = x1 > x0 && y1 > y0
                             ? new()
                             {
                                 Advance = advance,
                                 Bounds = new(
                                     x0 - (Math.Max(y1, 0) * this.slant),
                                     y0,
                                     x1 - (Math.Min(y0, 0) * this.slant),
                                     y1),
                             }
                             : new() { Advance = advance };
        }
    }

    /// <inheritdoc/>
    /// <remarks>Takes the faces of the installed family of the same name if it has them, and has DirectWrite simulate
    /// the rest.</remarks>
    protected override ManagedFont CreateStyled(bool bold, bool italic)
    {
        var sims = (bold ? DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD : 0) |
                   (italic ? DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE : 0);
        using var styled = this.FindInstalledFace(bold, italic, ref sims);

        // Without an italic face, the outlines are slanted as the game slants italic text. DirectWrite's oblique has
        // an angle of its own.
        var slant = (sims & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) != 0
                        ? SyntheticStyleFont.Slant
                        : this.slant;
        sims &= ~DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE;
        using var simulated = default(ComPtr<IDWriteFontFace>);
        if (CreateFace(styled.Get() is not null ? styled.Get() : this.face, sims, simulated.GetAddressOf()).FAILED)
            return base.CreateStyled(bold, italic);
        return new DirectWriteFont(
            $"{this.Name}{(bold ? " bold" : null)}{(italic ? " italic" : null)}",
            simulated.Get(),
            this.colorGlyphs,
            slant);
    }

    // The same face as another, with simulations.
    private static HRESULT CreateFace(IDWriteFontFace* from, DWRITE_FONT_SIMULATIONS sims, IDWriteFontFace** face)
    {
        uint fileCount = 0;
        var hr = from->GetFiles(&fileCount, null);
        if (hr.FAILED || fileCount == 0)
            return hr.FAILED ? hr : E.E_FAIL;
        var files = stackalloc IDWriteFontFile*[(int)fileCount];
        hr = from->GetFiles(&fileCount, files);
        if (hr.FAILED)
            return hr;
        hr = ((IDWriteFactory*)Factory)->CreateFontFace(
            from->GetType(),
            fileCount,
            files,
            from->GetIndex(),
            sims,
            face);
        for (var i = 0; i < fileCount; i++)
            files[i]->Release();
        return hr;
    }

    private static nint CreateLoader()
    {
        IDWriteInMemoryFontFileLoader* loader;
        Factory->CreateInMemoryFontFileLoader(&loader).ThrowOnError();
        Factory->RegisterFontFileLoader((IDWriteFontFileLoader*)loader).ThrowOnError();
        return (nint)loader;
    }

    private static nint CreateFactory()
    {
        IDWriteFactory5* factory;
        fixed (Guid* iid = &IID.IID_IDWriteFactory5)
        {
            DirectX.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, iid, (IUnknown**)&factory)
                   .ThrowOnError();
        }

        return (nint)factory;
    }

    // The installed face of the family of the same name for a style, if it is a real one, with the simulations it does
    // away with cleared. Null if the family is not installed, or has other glyphs than this face.
    private ComPtr<IDWriteFontFace> FindInstalledFace(bool bold, bool italic, ref DWRITE_FONT_SIMULATIONS sims)
    {
        var result = default(ComPtr<IDWriteFontFace>);
        using var face3 = default(ComPtr<IDWriteFontFace3>);
        using var names = default(ComPtr<IDWriteLocalizedStrings>);
        using var collection = default(ComPtr<IDWriteFontCollection>);
        using var family = default(ComPtr<IDWriteFontFamily>);
        using var regular = default(ComPtr<IDWriteFont>);
        using var regularFace = default(ComPtr<IDWriteFontFace>);
        using var font = default(ComPtr<IDWriteFont>);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return result;
        fixed (Guid* iid = &IID.IID_IDWriteFontFace3)
        {
            if (this.face->QueryInterface(iid, (void**)face3.GetAddressOf()).FAILED)
                return result;
        }

        if (face3.Get()->GetFamilyNames(names.GetAddressOf()).FAILED ||
            ((IDWriteFactory*)Factory)->GetSystemFontCollection(collection.GetAddressOf(), false).FAILED)
        {
            return result;
        }

        fixed (char* name = SystemFallbackFont.EnglishNameOf(names.Get()))
        {
            uint index;
            BOOL exists;
            if (collection.Get()->FindFamilyName(name, &index, &exists).FAILED || !exists ||
                collection.Get()->GetFontFamily(index, family.GetAddressOf()).FAILED)
            {
                return result;
            }
        }

        var weight = face3.Get()->GetWeight();
        var stretch = face3.Get()->GetStretch();
        var style = face3.Get()->GetStyle();
        if (family.Get()->GetFirstMatchingFont(weight, stretch, style, regular.GetAddressOf()).FAILED ||
            regular.Get()->CreateFontFace(regularFace.GetAddressOf()).FAILED ||
            regularFace.Get()->GetGlyphCount() != this.face->GetGlyphCount())
        {
            return result;
        }

        var boldWeight = (int)weight >= 600
                             ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BLACK
                             : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD;
        if (family.Get()->GetFirstMatchingFont(
                bold ? boldWeight : weight,
                stretch,
                italic ? DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC : style,
                font.GetAddressOf()).FAILED)
        {
            return result;
        }

        var found = font.Get()->GetSimulations();
        var realBold = bold && (int)font.Get()->GetWeight() >= (int)weight + 200 &&
                       (found & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD) == 0;
        var realItalic = italic && style == DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL &&
                         font.Get()->GetStyle() != DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL &&
                         (found & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) == 0;
        if ((!realBold && !realItalic) || font.Get()->CreateFontFace(result.GetAddressOf()).FAILED)
            return result;
        if (realBold)
            sims &= ~DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD;
        if (realItalic)
            sims &= ~DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE;
        return result;
    }

    private void ReadMetrics()
    {
        this.hasColorGlyphs = ColorGlyphRasterizer.HasColorGlyphs(this.face);

        DWRITE_FONT_METRICS fm;
        this.face->GetMetrics(&fm);
        this.unitsPerEm = fm.designUnitsPerEm;
        this.underlinePosition = -fm.underlinePosition;
        this.underlineThickness = fm.underlineThickness;
        this.strikethroughThickness = fm.strikethroughThickness > 0 ? fm.strikethroughThickness : fm.underlineThickness;
        this.strikethroughPosition = fm.strikethroughPosition > 0
                                         ? fm.strikethroughPosition
                                         : (fm.designUnitsPerEm * 0.28f) + (this.strikethroughThickness * 0.5f);

        // Measured with hhea, as stb_truetype (and so Dear ImGui) does. DirectWrite reports the OS/2 metrics.
        void* hhea = null;
        uint hheaLength = 0;
        void* context = null;
        BOOL exists = false;
        if (this.face->TryGetFontTable(
                BinaryPrimitives.ReverseEndianness(0x68686561u /* hhea */), &hhea, &hheaLength, &context, &exists)
            .SUCCEEDED && exists)
        {
            if (hheaLength >= 10)
            {
                var span = new ReadOnlySpan<byte>(hhea, (int)hheaLength);
                this.designAscent = BinaryPrimitives.ReadInt16BigEndian(span[4..]);
                this.designDescent = -BinaryPrimitives.ReadInt16BigEndian(span[6..]);
                this.designLineGap = BinaryPrimitives.ReadInt16BigEndian(span[8..]);
            }

            this.face->ReleaseFontTable(context);
        }

        if (this.designAscent + this.designDescent <= 0)
        {
            this.designAscent = fm.ascent;
            this.designDescent = fm.descent;
            this.designLineGap = fm.lineGap;
        }

        this.designLineGap = Math.Max(this.designLineGap, 0);
    }

    // Pixels per design unit. See the class remarks.
    private float Scale(float size) => size / (this.designAscent + this.designDescent + this.designLineGap);

    private void ReleaseResources()
    {
        if (this.face is not null)
        {
            this.face->Release();
            this.face = null;
        }
    }
}
