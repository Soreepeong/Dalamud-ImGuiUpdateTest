using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Dalamud.Interface.Fonts;

public sealed partial record FontSpec
{
    /// <summary>Gets the face of an installed font family closest to a style.</summary>
    /// <param name="familyName">The family name in any language and case. Names in different languages make
    /// different specs, even for one family.</param>
    /// <param name="style">The style.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    public static FontSpec From(
        string familyName, FontStyle style = default, float sizePx = 0, bool fallback = true) =>
        From(new SystemFontId(new(familyName), style), sizePx, fallback);

    /// <summary>Gets the face of a font family closest to a style.</summary>
    /// <param name="family">The family.</param>
    /// <param name="style">The style.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    /// <remarks>Installed families simulate the styles they have no face for. Other families are emboldened for
    /// weights above the face's, as <see cref="From(GameFontFamily, FontStyle, float, bool)"/> does. Their slant is
    /// not simulated.</remarks>
    public static FontSpec From(
        IFontFamilyId family, FontStyle style = default, float sizePx = 0, bool fallback = true)
    {
        if (family is SystemFontFamilyId s)
            return From(new SystemFontId(s, style), sizePx, fallback);
        var face = family.Fonts[family.FindBestMatch(style)];
        var embolden = EmboldenOf(style, face.Style, sizePx);
        var spec = From(face, sizePx, fallback);
        return embolden > 0 ? spec with { Embolden = embolden } : spec;
    }

    /// <summary>Gets a font.</summary>
    /// <param name="font">The font.</param>
    /// <param name="sizePx">Size in pixels if positive, a ratio of the current font size if negative (-1.5 =
    /// 150%), or no inherent specified size if 0.</param>
    /// <param name="fallback">Whether characters the font lacks come from the default font. If so,
    /// <see cref="Fallbacks"/> is <see cref="Default"/> alone, else empty.</param>
    /// <returns>The spec.</returns>
    [OverloadResolutionPriority(1)] // Over From(IFontFamilyId). Single-face font ids are their own family too.
    public static FontSpec From(IFontId font, float sizePx = 0, bool fallback = true) =>
        new()
        {
            FontId = font ?? throw new ArgumentNullException(nameof(font)),
            SizePx = sizePx,

            // The default font is the chain of fallback fonts itself.
            Fallbacks = fallback && font is not DalamudDefaultFontAndFamilyId ? [Default] : [],
        };

    /// <summary>Gets a Dalamud Asset font.</summary>
    /// <param name="fontAsset">A font asset.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="fontAsset"/> is not a font.</exception>
    public static FontSpec From(DalamudAsset fontAsset, float sizePx = 0, bool fallback = true) =>
        From(new DalamudAssetFontAndFamilyId(fontAsset), sizePx, fallback);

    /// <summary>Gets a game font.</summary>
    /// <param name="family">The family.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    public static FontSpec From(GameFontFamily family, float sizePx = 0, bool fallback = true) =>
        From(new GameFontAndFamilyId(family), sizePx, fallback);

    /// <summary>Gets a game font, emboldened for weights above regular.</summary>
    /// <param name="family">The family.</param>
    /// <param name="style">The style.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    public static FontSpec From(GameFontFamily family, FontStyle style, float sizePx = 0, bool fallback = true) =>
        From(new GameFontAndFamilyId(family), style, sizePx, fallback);

    /// <summary>Gets a game font at one of its sizes.</summary>
    /// <param name="familyAndSize">The family and size.</param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    public static FontSpec From(GameFontFamilyAndSize familyAndSize, bool fallback = true) =>
        From(familyAndSize.Family, familyAndSize.BaseSizePx, fallback);

    /// <summary>Gets a font from a font file (TTF, OTF, TTC).</summary>
    /// <param name="file">The file.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    /// <exception cref="ArgumentNullException">If <paramref name="file"/> is <see langword="null"/>.</exception>
    public static FontSpec From(FileInfo file, float sizePx = 0, int fontNo = 0, bool fallback = true) =>
        From(new FileFontId((file ?? throw new ArgumentNullException(nameof(file))).FullName), sizePx, fallback)
            with
            {
                FontNo = fontNo,
            };

    /// <summary>Gets a font from font data in a stream, read now from its current position.</summary>
    /// <param name="stream">The stream. Not closed within.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="name">Name of the font, for display.</param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    /// <remarks>The spec cannot be saved, as in a plugin configuration. Saving it throws.</remarks>
    public static FontSpec From(
        Stream stream, float sizePx = 0, int fontNo = 0, string? name = null, bool fallback = true) =>
        From(new StreamFontId(stream, name), sizePx, fallback) with { FontNo = fontNo };

    /// <summary>Gets a font from font data, copied now.</summary>
    /// <param name="data">The data.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="name">Name of the font, for display.</param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    /// <remarks>The spec cannot be saved, as in a plugin configuration. Saving it throws.</remarks>
    public static FontSpec From(
        ReadOnlySpan<byte> data, float sizePx = 0, int fontNo = 0, string? name = null, bool fallback = true) =>
        From(new StreamFontId([.. data], name), sizePx, fallback) with { FontNo = fontNo };

    /// <summary>Gets a font from a resource embedded in an assembly.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="fallback"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='fallback']/text()"/>
    /// </param>
    /// <inheritdoc cref="From(IFontId, float, bool)" path="/returns"/>
    /// <exception cref="FileNotFoundException">If the assembly has no such resource.</exception>
    /// <remarks>The whole resource is read into memory. Later calls share it while a spec of it is alive, but still
    /// make a new spec. Make the spec once. The spec cannot be saved, as in a plugin configuration. Saving it throws.
    /// </remarks>
    public static FontSpec From(
        Assembly assembly, string resourceName, float sizePx = 0, int fontNo = 0, bool fallback = true) =>
        From(StreamFontId.FromManifestResource(assembly, resourceName), sizePx, fallback) with { FontNo = fontNo };

    /// <summary>Gets a font made of several fonts, tried in order.</summary>
    /// <param name="fonts">The fonts, in order of priority. Each is a spec without fallbacks, unless it is a
    /// <see cref="FontSpec"/> with its own.</param>
    /// <param name="sizePx"><inheritdoc cref="From(IFontId, float, bool)" path="/param[@name='sizePx']/text()"/>
    /// </param>
    /// <param name="lineHeight">The line height, as a ratio to the font size.</param>
    /// <param name="fallback">Whether characters no font has come from the default font. If so,
    /// <see cref="Default"/> follows the fonts in <see cref="Fallbacks"/>.</param>
    /// <returns>The spec, without a <see cref="FontId"/> of its own.</returns>
    /// <example><code>FontSpec.From(["Segoe UI", ("Malgun Gothic", FontStyle.Bold)], sizePx: 20);</code></example>
    [OverloadResolutionPriority(1)]
    public static FontSpec From(
        ReadOnlySpan<FontSource> fonts, float sizePx = 0, float lineHeight = 1, bool fallback = true)
    {
        var specs = ImmutableArray.CreateBuilder<FontSpec>(fonts.Length + (fallback ? 1 : 0));
        foreach (var font in fonts)
            specs.Add(font.Spec ?? throw new ArgumentException("A font is empty.", nameof(fonts)));
        if (fallback)
            specs.Add(Default);
        return new() { SizePx = sizePx, LineHeight = lineHeight, Fallbacks = specs.MoveToImmutable() };
    }
}
