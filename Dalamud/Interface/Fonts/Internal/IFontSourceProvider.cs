using System.Diagnostics.CodeAnalysis;

using Dalamud.Interface.Fonts.Internal.Sources;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Gives the fonts of font ids.</summary>
/// <remarks>It caches and shares its fonts. It serves ids that no other source serves.</remarks>
/// <seealso cref="FontRegistry"/>
internal interface IFontSourceProvider
{
    /// <summary>Gets the font of a font id, unless this declines the id.</summary>
    /// <param name="id">The font id.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="locale">The locale to draw text in, such as "ja-JP".</param>
    /// <param name="font">The font, or null if declined.</param>
    /// <returns>Whether this serves the id.</returns>
    /// <remarks><ul>
    /// <li>A font that fails to load throws, instead of being declined.</li>
    /// <li>Sources whose fonts have no locale-specific forms ignore <paramref name="locale"/>. The others keep a font
    /// per locale.</li>
    /// </ul></remarks>
    bool TryGetFont(IFontId id, int fontNo, string locale, [NotNullWhen(true)] out Font? font);
}
