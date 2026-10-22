namespace Dalamud.Interface.Fonts;

/// <summary>
/// Represents a user's choice of font(s).<br />
/// Not intended for plugins to implement.
/// </summary>
public interface IFontSpec
{
    /// <summary>Gets the font size in pixels, or font size ratio.</summary>
    /// <value>Pixels if positive, ratio if negative, or 100% if <c>0</c>.</value>
    float SizePx { get; }

    /// <summary>Gets the font size in points, or font size ratio.</summary>
    /// <value>Pixels if positive, ratio if negative, or 100% if <c>0</c>.</value>
    float SizePt { get; }

    /// <summary>
    /// Represents this font specification, preferrably in the requested locale.
    /// </summary>
    /// <param name="localeCode">The locale code. Must be in lowercase(invariant).</param>
    /// <returns>The value.</returns>
    string ToLocalizedString(string localeCode);
}
