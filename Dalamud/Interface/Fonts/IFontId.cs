namespace Dalamud.Interface.Fonts;

/// <summary>
/// Represents a font identifier.<br />
/// Not intended for plugins to implement.
/// </summary>
public interface IFontId : IObjectWithLocalizableName
{
    /// <summary>
    /// Gets the associated font family.
    /// </summary>
    IFontFamilyId Family { get; }

    /// <summary>Gets the weight, slant and stretch of the face.</summary>
    /// <remarks>For an installed font asked for by style, the style asked for. <see cref="FontStyle.Default"/> for
    /// fonts with a single face.</remarks>
    FontStyle Style { get; }
}
