using System.Collections.Generic;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>
/// Represents the default Dalamud font.
/// </summary>
public sealed class DalamudDefaultFontAndFamilyId : IFontId, IFontFamilyId
{
    /// <summary>
    /// The shared instance of <see cref="DalamudDefaultFontAndFamilyId"/>.
    /// </summary>
    public static readonly DalamudDefaultFontAndFamilyId Instance = new();

    private DalamudDefaultFontAndFamilyId()
    {
    }

    /// <inheritdoc/>
    [JsonIgnore]
    public string EnglishName => "(Default)";

    /// <inheritdoc/>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? LocaleNames => null;

    /// <inheritdoc/>
    [JsonIgnore]
    public IFontFamilyId Family => this;

    /// <inheritdoc/>
    [JsonIgnore]
    public FontStyle Style => FontStyle.Default;

    /// <inheritdoc/>
    [JsonIgnore]
    public IReadOnlyList<IFontId> Fonts => new List<IFontId> { this }.AsReadOnly();

    public static bool operator ==(DalamudDefaultFontAndFamilyId? left, DalamudDefaultFontAndFamilyId? right) =>
        left is null == right is null;

    public static bool operator !=(DalamudDefaultFontAndFamilyId? left, DalamudDefaultFontAndFamilyId? right) =>
        left is null != right is null;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DalamudDefaultFontAndFamilyId;

    /// <inheritdoc/>
    public override int GetHashCode() => 12345678;

    /// <inheritdoc/>
    public override string ToString() => nameof(DalamudDefaultFontAndFamilyId);

    /// <inheritdoc/>
    public int FindBestMatch(FontStyle style) => 0;
}
