namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>Metrics of a font at a size, in logical pixels, as designed.</summary>
/// <param name="Ascent">From the line top to the baseline (positive).</param>
/// <param name="Descent">From the baseline to the bottom (positive).</param>
/// <param name="LineHeight">The font's line advance, including the line gap.</param>
/// <param name="UnderlinePosition">Below the baseline, positive down.</param>
/// <param name="UnderlineThickness">Thickness of the underline.</param>
/// <param name="StrikethroughPosition">The top of the strikethrough above the baseline, positive up.</param>
/// <param name="StrikethroughThickness">Thickness of the strikethrough.</param>
internal readonly record struct FontMetrics(
    float Ascent,
    float Descent,
    float LineHeight,
    float UnderlinePosition,
    float UnderlineThickness,
    float StrikethroughPosition,
    float StrikethroughThickness)
{
    /// <summary>Gets metrics for when there is no font to ask, in proportions typical of fonts.</summary>
    /// <param name="size">The font size.</param>
    /// <returns>The metrics.</returns>
    /// <remarks>The underline is a tenth of the size below the baseline, as in most fonts, and at least a pixel.
    /// </remarks>
    public static FontMetrics Fallback(float size) =>
        new(
            MathF.Round(size * 0.8f),
            MathF.Round(size * 0.2f),
            size,
            Math.Max(1, MathF.Round(size * 0.1f)),
            1,
            MathF.Round(size * 0.3f),
            1);

    /// <summary>Gets the metrics with the line gap split evenly into the ascent and the descent.</summary>
    /// <returns>The metrics.</returns>
    /// <remarks>The game's own fonts have no line gap either.</remarks>
    public FontMetrics SplitLineGap()
    {
        var ascent = this.Ascent + MathF.Round((this.LineHeight - this.Ascent - this.Descent) * 0.5f);
        return this with { Ascent = ascent, Descent = this.LineHeight - ascent };
    }

    /// <summary>Gets the distance from the top of a line box to the baseline.</summary>
    /// <param name="blockSize">Height of the line box.</param>
    /// <returns>The distance in pixels.</returns>
    /// <remarks>The space beyond (or short of) ascent + descent is split evenly above and below.</remarks>
    public float BaselineIn(float blockSize) =>
        MathF.Round(this.Ascent) + ((blockSize - (this.Ascent + this.Descent)) * 0.5f);
}
