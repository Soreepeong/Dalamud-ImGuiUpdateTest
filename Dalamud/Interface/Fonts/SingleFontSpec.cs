using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>Represents a user's choice of a single font. See <see cref="MultiFontSpec"/> for several fonts.</summary>
public record SingleFontSpec : IFontSpec
{
    /// <summary>Gets the font id.</summary>
    [JsonProperty]
    public required IFontId FontId { get; init; }

    /// <inheritdoc/>
    [JsonProperty]
    public float SizePx { get; init; } = 16;

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

    /// <summary>Gets the glyph offset, in pixels or ems depending on the sign of <see cref="SizePx"/>.</summary>
    [JsonProperty]
    public Vector2 GlyphOffset { get; init; }

    /// <summary>Gets the letter spacing, in pixels or ems depending on the sign of <see cref="SizePx"/>.</summary>
    [JsonProperty]
    public float LetterSpacing { get; init; }

    /// <summary>Gets the ranges of codepoints to take from this font.</summary>
    /// <value>Pairs of inclusive bounds, which may end with a zero, or <see langword="null"/> for all.</value>
    [JsonProperty]
    public ImmutableArray<uint>? CodepointRanges { get; init; }

    /// <summary>Gets the font face index within a font collection file (TTC).</summary>
    /// <remarks>
    /// This property only applies to <see cref="DalamudAsset.NotoSansCjkRegular"/> and
    /// <see cref="DalamudAsset.NotoSansCjkMedium"/>, which are TTC fonts bundling
    /// multiple language-specific CJK glyph sets (Japanese, Traditional Chinese,
    /// Simplified Chinese, Korean) into a single file.
    ///
    /// The index corresponds to the font face order in the TTC:
    /// <list type="bullet">
    ///   <item><description>0 = Japanese</description></item>
    ///   <item><description>1 = Traditional Chinese</description></item>
    ///   <item><description>2 = Simplified Chinese</description></item>
    ///   <item><description>3 = Korean</description></item>
    /// </list>
    ///
    /// This value is ignored for all other <see cref="DalamudAsset"/> entries.
    /// Only one glyph set can be active at a time. In most cases, you can omit this—
    /// Dalamud automatically selects the appropriate face based on the UI language.
    /// </remarks>
    [JsonProperty]
    public int FontNo { get; init; }

    /// <summary>Gets the minimum advance, in pixels or ems depending on the sign of <see cref="SizePx"/>.</summary>
    [JsonProperty]
    public float GlyphMinAdvanceX { get; init; }

    /// <summary>Gets the maximum advance, in pixels or ems depending on the sign of <see cref="SizePx"/>.</summary>
    [JsonProperty]
    public float GlyphMaxAdvanceX { get; init; } = float.MaxValue;

    /// <summary>Gets a value indicating whether to scale glyphs down to fit a square.</summary>
    /// <remarks>Use with icon fonts.</remarks>
    [JsonProperty]
    public bool FitToSquare { get; init; }

    /// <summary>
    /// Gets the synthetic bold strength, in pixels or ems depending on the sign of <see cref="SizePx"/>.
    /// </summary>
    [JsonProperty]
    public float Weight { get; init; }

    /// <summary>Gets the ratio to the current font size if <see cref="SizePx"/> is not positive.</summary>
    [JsonIgnore]
    public float RelativeSize => this.SizePx > 0 ? 1f : this.SizePx == 0 ? 1f : -this.SizePx;

    /// <summary>Sets <see cref="CodepointRanges"/> from <c>GlyphRanges</c> of saved specs, unless they have it.
    /// </summary>
    /// <remarks>Read as <see cref="uint"/>s. Older versions wrote <see cref="ushort"/>s, and some newer ones
    /// codepoints above U+FFFF.</remarks>
    [JsonProperty("GlyphRanges")]
    [SuppressMessage("ReSharper", "UnusedMember.Local", Justification = "JSON")]
    private uint[]? SavedGlyphRanges
    {
        init => this.CodepointRanges ??= value is null ? null : [.. value];
    }

    /// <inheritdoc/>
    public virtual bool Equals(SingleFontSpec? other) =>
        other is not null
        && Equals(this.FontId, other.FontId)
        && this.SizePx.Equals(other.SizePx)
        && this.LineHeight.Equals(other.LineHeight)
        && this.GlyphOffset.Equals(other.GlyphOffset)
        && this.LetterSpacing.Equals(other.LetterSpacing)
        && (this.CodepointRanges ?? []).SequenceEqual(other.CodepointRanges ?? [])
        && this.CodepointRanges.HasValue == other.CodepointRanges.HasValue
        && this.FontNo == other.FontNo
        && this.GlyphMinAdvanceX.Equals(other.GlyphMinAdvanceX)
        && this.GlyphMaxAdvanceX.Equals(other.GlyphMaxAdvanceX)
        && this.FitToSquare == other.FitToSquare
        && this.Weight.Equals(other.Weight);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hc = default(HashCode);
        hc.Add(this.FontId);
        hc.Add(this.SizePx);
        hc.Add(this.GlyphOffset);
        hc.Add(this.LetterSpacing);
        hc.Add(this.FontNo);
        hc.Add(this.Weight);
        foreach (var r in this.CodepointRanges ?? [])
            hc.Add(r);
        return hc.ToHashCode();
    }

    /// <inheritdoc/>
    public string ToLocalizedString(string localeCode)
    {
        var sb = new StringBuilder();
        sb.Append(this.FontId.Family.GetLocalizedName(localeCode));
        sb.Append($"({this.FontId.GetLocalizedName(localeCode)}, ");
        sb.Append(this.SizePx > 0 ? $"{this.SizePt}pt" : $"{this.RelativeSize:0.##}x");
        if (Math.Abs(this.LineHeight - 1f) > 0.000001f)
            sb.Append($", LH={this.LineHeight:0.##}");
        if (this.GlyphOffset != default)
            sb.Append($", O={this.GlyphOffset.X:0.##},{this.GlyphOffset.Y:0.##}");
        if (this.LetterSpacing != 0f)
            sb.Append($", LS={this.LetterSpacing:0.##}");
        sb.Append(')');
        return sb.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => this.ToLocalizedString("en");
}
