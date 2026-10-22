using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Dalamud.Interface.Fonts;

/// <summary>Font specs from Dalamud and helpers methods.</summary>
public static class FontSpec
{
    private static readonly uint IconMin = (uint)Enum.GetValues<FontAwesomeIcon>().Where(x => x > 0).Min();
    private static readonly uint IconMax = (uint)Enum.GetValues<FontAwesomeIcon>().Where(x => x > 0).Max();
    private static readonly DalamudAssetFontAndFamilyId FontAwesome = new(DalamudAsset.FontAwesomeFreeSolid);

    /// <summary>Gets the Dalmaud default font from settings, with all the system fallback fonts.</summary>
    public static SingleFontSpec Default { get; } = From(DalamudDefaultFontAndFamilyId.Instance);

    /// <summary>Gets the <see cref="FontAwesomeIcon"/> font.</summary>
    public static SingleFontSpec Icon { get; } =
        From(FontAwesome) with { CodepointRanges = [IconMin, IconMax], GlyphMinAdvanceX = 1, GlyphMaxAdvanceX = 1 };

    /// <summary>Gets the <see cref="FontAwesomeIcon"/> font, with each icon fit into a square.</summary>
    public static SingleFontSpec IconFixedWidth { get; } =
        From(FontAwesome) with { CodepointRanges = [0x20, 0x20, IconMin, IconMax], FitToSquare = true };

    /// <summary>Gets the Dalamud default monospaced font, with all the system fallback fonts.</summary>
    public static MultiFontSpec Mono { get; } =
        From([From(new DalamudAssetFontAndFamilyId(DalamudAsset.InconsolataRegular))]);

    /// <summary>Gets the face of an installed font family closest to a style.</summary>
    /// <param name="familyName">The family name in any language.</param>
    /// <param name="style">The style.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <inheritdoc cref="From(IFontId, float)" path="/returns"/>
    public static SingleFontSpec From(string familyName, FontStyle style = default, float sizePx = 0) =>
        From(new SystemFontId(new(familyName), style), sizePx);

    /// <summary>Gets the face of a font family closest to a style.</summary>
    /// <param name="family">The family.</param>
    /// <param name="style">The style.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <inheritdoc cref="From(IFontId, float)" path="/returns"/>
    public static SingleFontSpec From(IFontFamilyId family, FontStyle style = default, float sizePx = 0) =>
        From(
            family is SystemFontFamilyId s ? new SystemFontId(s, style) : family.Fonts[family.FindBestMatch(style)],
            sizePx);

    /// <summary>Gets a font.</summary>
    /// <param name="font">The font.</param>
    /// <param name="sizePx">Size in pixels if positive, a ratio of the current font size if negative (-1.5 =
    /// 150%), or no inherent specified size if 0.</param>
    /// <returns>The spec, without the default font after it.</returns>
    [OverloadResolutionPriority(1)] // Over From(IFontFamilyId). Single-face font ids are their own family too.
    public static SingleFontSpec From(IFontId font, float sizePx = 0) =>
        new() { FontId = font ?? throw new ArgumentNullException(nameof(font)), SizePx = sizePx };

    /// <summary>Gets a Dalamud Asset font.</summary>
    /// <param name="fontAsset">A font asset.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <inheritdoc cref="From(IFontId, float)" path="/returns"/>
    public static SingleFontSpec From(DalamudAsset fontAsset, float sizePx = 0) =>
        From(new DalamudAssetFontAndFamilyId(fontAsset), sizePx);

    /// <summary>Gets a game font.</summary>
    /// <param name="family">The family.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <inheritdoc cref="From(IFontId, float)" path="/returns"/>
    public static SingleFontSpec From(GameFontFamily family, float sizePx = 0) =>
        From(new GameFontAndFamilyId(family), sizePx);

    /// <summary>Gets a game font.</summary>
    /// <param name="family">The family.</param>
    /// <param name="style">The style.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <inheritdoc cref="From(IFontId, float)" path="/returns"/>
    public static SingleFontSpec From(GameFontFamily family, FontStyle style, float sizePx = 0) =>
        // Synthetic bold is in ems for specs following the current size, else in pixels at the size.
        From(family, sizePx) with
        {
            Weight = Math.Max(0, style.EffectiveWeight - 400) / 7200f * (sizePx > 0 ? sizePx : 1),
        };

    /// <summary>Gets a game font at one of its sizes.</summary>
    /// <param name="familyAndSize">The family and size.</param>
    /// <inheritdoc cref="From(IFontId, float)" path="/returns"/>
    public static SingleFontSpec From(GameFontFamilyAndSize familyAndSize) =>
        From(familyAndSize.Family, familyAndSize.BaseSizePx);

    /// <summary>Gets a font from a font file (TTF, OTF, TTC).</summary>
    /// <param name="file">The file.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <inheritdoc cref="From(IFontId, float)" path="/returns"/>
    public static SingleFontSpec From(FileInfo file, int fontNo = 0, float sizePx = 0) =>
        From(new FileFontId(file.FullName), sizePx) with { FontNo = fontNo };

    /// <summary>Gets a font from font data in a stream, read now.</summary>
    /// <param name="stream">The stream. Not closed within.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="name">Name of the font, for display.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <inheritdoc cref="From(IFontId, float)" path="/returns"/>
    public static SingleFontSpec From(Stream stream, int fontNo = 0, float sizePx = 0, string? name = null) =>
        From(new StreamFontId(stream, name), sizePx) with { FontNo = fontNo };

    /// <summary>Gets a font from font data, copied now.</summary>
    /// <param name="data">The data.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <param name="name">Name of the font, for display.</param>
    /// <inheritdoc cref="From(Stream, int, float, string?)" path="/returns|/remarks"/>
    public static SingleFontSpec From(ReadOnlySpan<byte> data, int fontNo = 0, float sizePx = 0, string? name = null) =>
        From(new StreamFontId([.. data], name), sizePx) with { FontNo = fontNo };

    /// <summary>Gets a font from a resource embedded in an assembly.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <inheritdoc cref="From(IFontId, float)" path="/returns"/>
    /// <exception cref="FileNotFoundException">If the assembly has no such resource.</exception>
    public static SingleFontSpec From(Assembly assembly, string resourceName, int fontNo = 0, float sizePx = 0) =>
        From(StreamFontId.FromManifestResource(assembly, resourceName), sizePx) with { FontNo = fontNo };

    /// <summary>Gets a font made of several fonts.</summary>
    /// <param name="parts">The fonts, in order of priority.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float)" path="/param[@name='sizePx']/text()"/></param>
    /// <param name="lineHeight">The line height, as a ratio to the font size.</param>
    /// <param name="fallback">Whether characters no part has come from the default font.</param>
    /// <returns>The spec.</returns>
    /// <example><code>FontSpec.From(["Segoe UI", ("Malgun Gothic", FontStyle.Bold)], sizePx: 20);</code></example>
    [OverloadResolutionPriority(1)]
    public static MultiFontSpec From(
        ReadOnlySpan<FontSource> parts, float sizePx = 0, float lineHeight = 1, bool fallback = true)
    {
        var specs = ImmutableArray.CreateBuilder<IFontSpec>(parts.Length);
        foreach (var part in parts)
            specs.Add(part.Spec ?? throw new ArgumentException("A part is empty.", nameof(parts)));
        return new()
        {
            SizePx = sizePx,
            LineHeight = lineHeight,
            FallBackToDefaultFont = fallback,
            Parts = specs.MoveToImmutable(),
        };
    }

    /// <summary>Gets a copy of a spec at another size in pixels.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="sizePx">The size in pixels.</param>
    /// <returns>The copy.</returns>
    public static SingleFontSpec WithSize(this SingleFontSpec spec, float sizePx) =>
        spec with { SizePx = CheckSizePx(sizePx) };

    /// <inheritdoc cref="WithSize(SingleFontSpec, float)" path="/summary|/param|/returns"/>
    public static MultiFontSpec WithSize(this MultiFontSpec spec, float sizePx) =>
        spec with { SizePx = CheckSizePx(sizePx) };

    /// <summary>Gets a copy of a spec that follows the current font size.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="ratio">The ratio to the current font size (1.5 = 150%).</param>
    /// <returns>The copy.</returns>
    public static SingleFontSpec WithRelativeSize(this SingleFontSpec spec, float ratio = 1f) =>
        spec with { SizePx = ToRelativeSizePx(ratio) };

    /// <inheritdoc cref="WithRelativeSize(SingleFontSpec, float)" path="/summary|/param|/returns"/>
    public static MultiFontSpec WithRelativeSize(this MultiFontSpec spec, float ratio = 1f) =>
        spec with { SizePx = ToRelativeSizePx(ratio) };

    private static float CheckSizePx(float sizePx) =>
        sizePx > 0 ? sizePx : throw new ArgumentOutOfRangeException(nameof(sizePx), sizePx, "Must be positive.");

    private static float ToRelativeSizePx(float ratio) =>
        ratio > 0
            ? (Math.Abs(ratio - 1f) < 1e-6 ? 0 : -ratio)
            : throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Must be positive.");

    /// <summary>Helper for <see cref="FontSpec.From(ReadOnlySpan{FontSource}, float, float, bool)"/>.</summary>
    /// <example><code>
    /// FontSpec.From([
    ///     "Comic Sans MS",
    ///     ("Times New Roman", FontStyle.Bold),
    ///     new FileInfo(path),
    ///     (bytes, 1),
    /// ], sizePx: 20);
    /// </code></example>
    public readonly struct FontSource
    {
        /// <summary>Initializes a new instance of the <see cref="FontSource"/> struct.</summary>
        /// <param name="spec">The spec.</param>
        public FontSource(IFontSpec spec) => this.Spec = spec ?? throw new ArgumentNullException(nameof(spec));

        /// <summary>Initializes a new instance of the <see cref="FontSource"/> struct.</summary>
        /// <param name="font">The font.</param>
        [OverloadResolutionPriority(1)]
        public FontSource(IFontId font)
            : this(From(font))
        {
        }

        /// <summary>Initializes a new instance of the <see cref="FontSource"/> struct.</summary>
        /// <param name="family">The family.</param>
        /// <param name="style">The style.</param>
        public FontSource(IFontFamilyId family, FontStyle style = default)
            : this(From(family, style))
        {
        }

        /// <summary>Gets the spec, or <see langword="null"/> for the default value.</summary>
        internal IFontSpec? Spec { get; }

        /// <summary>An installed font family by name, in its regular face.</summary>
        /// <param name="familyName">The family name, in any language the font has it in.</param>
        public static implicit operator FontSource(string familyName) => new(From(familyName));

        /// <summary>The face of an installed font family closest to a style.</summary>
        /// <param name="value">The family name and the style.</param>
        public static implicit operator FontSource((string FamilyName, FontStyle Style) value) =>
            new(From(value.FamilyName, value.Style));

        /// <summary>The face of a font family closest to a style.</summary>
        /// <param name="value">The family and the style.</param>
        public static implicit operator FontSource((IFontFamilyId Family, FontStyle Style) value) =>
            new(value.Family, value.Style);

        /// <summary>A game font.</summary>
        /// <param name="family">The family.</param>
        public static implicit operator FontSource(GameFontFamily family) => new(From(family));

        /// <summary>A game font, emboldened for weights above regular.</summary>
        /// <param name="value">The family and the style.</param>
        public static implicit operator FontSource((GameFontFamily Family, FontStyle Style) value) =>
            new(From(value.Family, value.Style));

        /// <summary>A game font at its own size.</summary>
        /// <param name="familyAndSize">The family and size.</param>
        public static implicit operator FontSource(GameFontFamilyAndSize familyAndSize) =>
            new(From(familyAndSize));

        /// <summary>A font file.</summary>
        /// <param name="file">The file.</param>
        public static implicit operator FontSource(FileInfo file) => new(From(file));

        /// <summary>A face of a font file.</summary>
        /// <param name="value">The file and the index of the face in the collection.</param>
        public static implicit operator FontSource((FileInfo File, int FontNo) value) =>
            new(From(value.File, value.FontNo));

        /// <summary>Font data in a stream, read now.</summary>
        /// <param name="stream">The stream.</param>
        public static implicit operator FontSource(Stream stream) => new(From(stream));

        /// <summary>A face of font data in a stream, read now.</summary>
        /// <param name="value">The stream and the index of the face in the collection.</param>
        public static implicit operator FontSource((Stream Stream, int FontNo) value) =>
            new(From(value.Stream, value.FontNo));

        /// <summary>Font data, copied now.</summary>
        /// <param name="data">The data.</param>
        public static implicit operator FontSource(byte[] data) => new(From(data));

        /// <summary>A face of font data, copied now.</summary>
        /// <param name="value">The data and the index of the face in the collection.</param>
        public static implicit operator FontSource((byte[] Data, int FontNo) value) =>
            new(From(value.Data, value.FontNo));

        /// <inheritdoc cref="implicit operator FontSource(byte[])"/>
        public static implicit operator FontSource(ReadOnlyMemory<byte> data) => new(From(data.Span));

        /// <inheritdoc cref="implicit operator FontSource(ValueTuple{byte[], int})"/>
        public static implicit operator FontSource((ReadOnlyMemory<byte> Data, int FontNo) value) =>
            new(From(value.Data.Span, value.FontNo));

        /// <inheritdoc cref="implicit operator FontSource(byte[])"/>
        public static implicit operator FontSource(Memory<byte> data) => new(From(data.Span));

        /// <inheritdoc cref="implicit operator FontSource(ValueTuple{byte[], int})"/>
        public static implicit operator FontSource((Memory<byte> Data, int FontNo) value) =>
            new(From(value.Data.Span, value.FontNo));

        /// <summary>A spec.</summary>
        /// <param name="spec">The spec.</param>
        public static implicit operator FontSource(SingleFontSpec spec) => new(spec);

        /// <inheritdoc cref="implicit operator FontSource(SingleFontSpec)"/>
        public static implicit operator FontSource(MultiFontSpec spec) => new(spec);
    }
}
