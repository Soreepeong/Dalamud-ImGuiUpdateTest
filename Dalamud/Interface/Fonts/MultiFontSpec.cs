using System.Collections.Immutable;
using System.Linq;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>A font made of several fonts. Each character is drawn with the first part that has it in its ranges, or
/// else with the default font.</summary>
/// <remarks>The size of a part is relative to the size of this spec. It is in pixels if positive (a 30px part of a
/// 20px spec is 1.5 times as large), a ratio if negative, or the same size if 0. Lengths in a part (offsets, spacing)
/// are as on their own. They are in pixels at the part's size if it has a size in pixels, else in ems. Only the line
/// height of this spec is used, not those of the parts.</remarks>
/// <example><code>
/// // Comic Sans MS at 20px, then Malgun Gothic at 110% for Hangul, then the default font.
/// var spec = FontSpec.From(
///     [
///         "Comic Sans MS",
///         FontSpec.From("Malgun Gothic", sizePx: -1.1f) with { CodepointRanges = [0xAC00, 0xD7A3] },
///     ],
///     sizePx: 20);
/// </code></example>
public sealed record MultiFontSpec : IFontSpec
{
    /// <inheritdoc/>
    /// <remarks>Defaults to 0.</remarks>
    [JsonProperty]
    public float SizePx { get; init; }

    /// <inheritdoc/>
    [JsonIgnore]
    public float SizePt
    {
        get => this.SizePx > 0 ? (this.SizePx * 3) / 4 : this.SizePx;
        init => this.SizePx = value > 0 ? (value * 4) / 3 : value;
    }

    /// <summary>Gets the line height ratio to the font size.</summary>
    [JsonProperty]
    public float LineHeight { get; init; } = 1f;

    /// <summary>Gets the fonts, in order of priority. A part may itself be a <see cref="MultiFontSpec"/>.</summary>
    [JsonProperty]
    public ImmutableArray<IFontSpec> Parts { get; init; } = [];

    /// <summary>Gets a value indicating whether characters no part has come from the default font (and its fallbacks,
    /// which have every character).</summary>
    [JsonProperty]
    public bool FallBackToDefaultFont { get; init; } = true;

    /// <summary>Gets the ratio to the current font size if <see cref="SizePx"/> is not positive.</summary>
    [JsonIgnore]
    public float RelativeSize => this.SizePx > 0 ? 1f : this.SizePx == 0 ? 1f : -this.SizePx;

    /// <summary>Gets the parts, or an empty array if <see cref="Parts"/> is default.</summary>
    internal ImmutableArray<IFontSpec> PartsOrEmpty => this.Parts.IsDefault ? [] : this.Parts;

    /// <inheritdoc/>
    public bool Equals(MultiFontSpec? other) =>
        other is not null
        && this.SizePx.Equals(other.SizePx)
        && this.LineHeight.Equals(other.LineHeight)
        && this.FallBackToDefaultFont == other.FallBackToDefaultFont
        && this.PartsOrEmpty.SequenceEqual(other.PartsOrEmpty);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hc = default(HashCode);
        hc.Add(this.SizePx);
        hc.Add(this.LineHeight);
        hc.Add(this.FallBackToDefaultFont);
        foreach (var p in this.PartsOrEmpty)
            hc.Add(p);
        return hc.ToHashCode();
    }

    /// <inheritdoc/>
    public string ToLocalizedString(string localeCode)
    {
        var size = this.SizePx > 0 ? $"{this.SizePt}pt" : $"{this.RelativeSize:0.##}x";
        if (Math.Abs(this.LineHeight - 1f) > 0.000001f)
            size += $", LH={this.LineHeight:0.##}";
        var parts = this.PartsOrEmpty.Select(x => x.ToLocalizedString(localeCode));
        if (this.FallBackToDefaultFont)
            parts = parts.Append("(Default)");
        return $"{string.Join(" + ", parts)} @ {size}";
    }

    /// <inheritdoc/>
    public override string ToString() => this.ToLocalizedString("en");
}
