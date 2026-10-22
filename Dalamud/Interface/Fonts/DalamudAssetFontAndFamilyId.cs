using System.Collections.Generic;

using Dalamud.Storage.Assets;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>
/// Represents a font from Dalamud assets.
/// </summary>
public sealed class DalamudAssetFontAndFamilyId : IFontFamilyId, IFontId
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DalamudAssetFontAndFamilyId"/> class.
    /// </summary>
    /// <param name="asset">The font asset.</param>
    [JsonConstructor]
    internal DalamudAssetFontAndFamilyId(DalamudAsset asset)
    {
        if (asset.GetPurpose() != DalamudAssetPurpose.Font)
            throw new ArgumentOutOfRangeException(nameof(asset), asset, "The specified asset is not a font asset.");
        this.Asset = asset;
    }

    /// <summary>
    /// Gets the font asset.
    /// </summary>
    [JsonProperty]
    public DalamudAsset Asset { get; init; }

    /// <inheritdoc/>
    [JsonIgnore]
    public string EnglishName => $"Dalamud: {this.Asset}";

    /// <inheritdoc/>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? LocaleNames => null;

    /// <inheritdoc/>
    [JsonIgnore]
    public IReadOnlyList<IFontId> Fonts => new List<IFontId> { this }.AsReadOnly();

    /// <inheritdoc/>
    [JsonIgnore]
    public IFontFamilyId Family => this;

    /// <inheritdoc/>
    [JsonIgnore]
    public FontStyle Style => FontStyle.Default;

    public static bool operator ==(DalamudAssetFontAndFamilyId? left, DalamudAssetFontAndFamilyId? right) =>
        Equals(left, right);

    public static bool operator !=(DalamudAssetFontAndFamilyId? left, DalamudAssetFontAndFamilyId? right) =>
        !Equals(left, right);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DalamudAssetFontAndFamilyId other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (int)this.Asset;

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(DalamudAssetFontAndFamilyId)}:{this.Asset}";

    /// <inheritdoc/>
    public int FindBestMatch(FontStyle style) => 0;

    private bool Equals(DalamudAssetFontAndFamilyId other) => this.Asset == other.Asset;
}
