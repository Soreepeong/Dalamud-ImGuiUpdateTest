using System.Collections.Generic;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>
/// Represents a font from the game.
/// </summary>
public sealed class GameFontAndFamilyId : IFontId, IFontFamilyId
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GameFontAndFamilyId"/> class.
    /// </summary>
    /// <param name="gameFontFamily">The game font family.</param>
    [JsonConstructor]
    internal GameFontAndFamilyId(GameFontFamily gameFontFamily) => this.GameFontFamily = gameFontFamily;

    /// <summary>
    /// Gets the game font family.
    /// </summary>
    [JsonProperty]
    public GameFontFamily GameFontFamily { get; init; }

    /// <inheritdoc cref="IFontFamilyId.Name"/>
    [JsonIgnore]
    [field: JsonIgnore]
    public LocalizedName Name =>
        field ??= new(
            Enum.GetName(this.GameFontFamily) is { } name ? $"Game: {name}" : $"Game: {(int)this.GameFontFamily}");

    /// <inheritdoc/>
    [JsonIgnore]
    public IFontFamilyId Family => this;

    /// <inheritdoc/>
    [JsonIgnore]
    public FontStyle Style => FontStyle.Default;

    /// <inheritdoc/>
    [JsonIgnore]
    public IReadOnlyList<IFontId> Fonts => new List<IFontId> { this }.AsReadOnly();

    /// <inheritdoc/>
    bool IFontId.PreviewsName =>
        this.GameFontFamily is not (GameFontFamily.Meidinger or GameFontFamily.MiedingerMid
                                    or GameFontFamily.JupiterNumeric);

    public static bool operator ==(GameFontAndFamilyId? left, GameFontAndFamilyId? right) => Equals(left, right);

    public static bool operator !=(GameFontAndFamilyId? left, GameFontAndFamilyId? right) => !Equals(left, right);

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) || (obj is GameFontAndFamilyId other && this.Equals(other));

    /// <inheritdoc/>
    public override int GetHashCode() => (int)this.GameFontFamily;

    /// <inheritdoc/>
    public int FindBestMatch(FontStyle style) => 0;

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(GameFontAndFamilyId)}:{this.GameFontFamily}";

    private bool Equals(GameFontAndFamilyId other) => this.GameFontFamily == other.GameFontFamily;
}
