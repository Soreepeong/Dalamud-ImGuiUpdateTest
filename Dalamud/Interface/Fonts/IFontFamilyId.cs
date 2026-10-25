using System.Collections.Generic;

using Dalamud.Interface.Fonts.Internal;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>
/// Represents a font family identifier.<br />
/// Not intended for plugins to implement.
/// </summary>
public interface IFontFamilyId
{
    /// <summary>Gets the name of the family.</summary>
    LocalizedName Name { get; }

    /// <summary>
    /// Gets the list of fonts under this family.
    /// </summary>
    /// <exception cref="System.IO.FileNotFoundException">If the family is a system family that is not installed.
    /// </exception>
    [JsonIgnore]
    IReadOnlyList<IFontId> Fonts { get; }

    /// <summary>Finds the index of the font inside <see cref="Fonts"/> closest to a style.</summary>
    /// <param name="style">The style.</param>
    /// <returns>The index of the font. A valid index if <see cref="Fonts"/> is not empty, else 0.</returns>
    int FindBestMatch(FontStyle style);

    /// <summary>
    /// Gets the list of Dalamud-provided fonts.
    /// </summary>
    /// <returns>The list of fonts.</returns>
    static List<IFontFamilyId> ListDalamudFonts() =>
    [
        new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansCjkMedium),
        new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansCjkRegular),
        new DalamudAssetFontAndFamilyId(DalamudAsset.InconsolataRegular),
        new DalamudAssetFontAndFamilyId(DalamudAsset.FontAwesomeFreeSolid),
    ];

    /// <summary>
    /// Gets the list of Game-provided fonts.
    /// </summary>
    /// <returns>The list of fonts.</returns>
    static List<IFontFamilyId> ListGameFonts() =>
    [
        new GameFontAndFamilyId(GameFontFamily.Axis),
        new GameFontAndFamilyId(GameFontFamily.Jupiter),
        new GameFontAndFamilyId(GameFontFamily.JupiterNumeric),
        new GameFontAndFamilyId(GameFontFamily.Meidinger),
        new GameFontAndFamilyId(GameFontFamily.MiedingerMid),
        new GameFontAndFamilyId(GameFontFamily.TrumpGothic),
    ];

    /// <summary>
    /// Gets the list of System-provided fonts.
    /// </summary>
    /// <param name="refresh">If <c>true</c>, try to refresh the list.</param>
    /// <returns>The list of fonts.</returns>
    static List<IFontFamilyId> ListSystemFonts(bool refresh) => DirectWriteUtilities.ListFamilies(refresh);
}
