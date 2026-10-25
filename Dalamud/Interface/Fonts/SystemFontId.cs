using System.Collections.Generic;

using Dalamud.Interface.Fonts.Internal;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>
/// Represents a font installed in the system.
/// </summary>
public sealed class SystemFontId : IFontId
{
    /// <summary>Initializes a new instance of the <see cref="SystemFontId"/> class.</summary>
    /// <param name="family">The parent font family.</param>
    /// <param name="style">The style of the face.</param>
    /// <param name="isSymbolFont">Whether the face has symbols for characters.</param>
    /// <param name="name">The name of the face.</param>
    internal SystemFontId(SystemFontFamilyId family, FontStyle style, bool isSymbolFont, LocalizedName name)
    {
        this.Family = family;
        this.Style = style;
        this.IsSymbolFont = isSymbolFont;
        this.Name = name;
    }

    /// <summary>Initializes a new instance of the <see cref="SystemFontId"/> class.</summary>
    /// <param name="family">The family.</param>
    /// <param name="style">The style.</param>
    internal SystemFontId(SystemFontFamilyId family, FontStyle style)
    {
        this.Family = family;
        this.Style = style;
        this.Name = new(style.ToString());
    }

    [JsonConstructor]
    private SystemFontId(string? englishName, IReadOnlyDictionary<string, string>? localeNames, IFontFamilyId family)
    {
        this.Name = new(englishName ?? string.Empty, localeNames);
        this.Family = family;
        this.Style = default;
    }

    /// <inheritdoc/>
    [JsonIgnore]
    public LocalizedName Name { get; }

    /// <inheritdoc/>
    [JsonProperty]
    public IFontFamilyId Family { get; init; }

    /// <inheritdoc/>
    [JsonProperty("FaceStyle")]
    public FontStyle Style
    {
        get;
        init => field = new(value.EffectiveWeight, value.Italicized, value.EffectiveStretch);
    }

    /// <inheritdoc/>
    /// <remarks>A symbol font draws dingbats for the letters of its name.</remarks>
    bool IFontId.PreviewsName => !this.IsSymbolFont;

    /// <summary>Gets a value indicating whether the font has symbols for characters.</summary>
    /// <remarks>Best-effort basis.</remarks>
    internal bool IsSymbolFont { get; }

    /// <summary>Gets the English name of <see cref="Name"/>, as saved.</summary>
    [JsonProperty("EnglishName")]
    private string SavedEnglishName => this.Name.English;

    /// <summary>Gets the translations of <see cref="Name"/>, as saved.</summary>
    [JsonProperty("LocaleNames")]
    private IReadOnlyDictionary<string, string>? SavedLocaleNames => this.Name.ByLocale;

    /// <summary>Sets the weight of <see cref="Style"/>, from specs saved before it.</summary>
    [JsonProperty("Weight")]
    private int SavedWeight
    {
        init => this.Style = this.Style with { Weight = value };
    }

    /// <summary>Sets the stretch of <see cref="Style"/>, from specs saved before it.</summary>
    [JsonProperty("Stretch")]
    private int SavedStretch
    {
        init => this.Style = this.Style with { Stretch = value };
    }

    /// <summary>Sets whether <see cref="Style"/> is italic, from specs saved before it (DirectWrite's style values).
    /// </summary>
    [JsonProperty("Style")]
    private int SavedStyle
    {
        init => this.Style = this.Style with { Italicized = value != 0 };
    }

    public static bool operator ==(SystemFontId? left, SystemFontId? right) => Equals(left, right);

    public static bool operator !=(SystemFontId? left, SystemFontId? right) => !Equals(left, right);

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) || (obj is SystemFontId other && this.Equals(other));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.Family, this.Style);

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(SystemFontId)}:{this.Style}:{this.Family}";

    /// <summary>
    /// Gets the file containing this font, and the font index within.
    /// </summary>
    /// <returns>The path and index.</returns>
    public (string Path, int Index) GetFileAndIndex() => DirectWriteUtilities.GetFileAndIndex(this);

    private bool Equals(SystemFontId other) => this.Family.Equals(other.Family) && this.Style == other.Style;
}
