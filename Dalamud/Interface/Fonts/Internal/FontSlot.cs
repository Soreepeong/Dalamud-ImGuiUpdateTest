using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Sources;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>A font that stands for another, which can be replaced.</summary>
/// <remarks>Its <see cref="ManagedFont.NativePointer"/> stays the same when <see cref="Target"/> changes, such as when
/// the default font changes. Layouts take <see cref="Target"/> (<see cref="ManagedFont.Resolve"/>) when they are made,
/// so a layout keeps drawing the glyphs of the font it was made with.</remarks>
internal sealed unsafe class FontSlot : ManagedFont
{
    private volatile CompositeFont target;

    /// <summary>Initializes a new instance of the <see cref="FontSlot"/> class.</summary>
    /// <param name="name">Name for debugging.</param>
    /// <param name="target">The font to stand for.</param>
    public FontSlot(string name, CompositeFont target)
        : base(name) =>
        this.target = target;

    /// <summary>Gets or sets the font this one stands for.</summary>
    /// <remarks>Set it only on the main thread before ImGui starts a frame, so that a frame uses one font throughout.
    /// </remarks>
    public CompositeFont Target
    {
        get => this.target;
        set => this.target = value;
    }

    /// <inheritdoc/>
    public override bool CanShape => this.target.CanShape;

    /// <inheritdoc/>
    public override ManagedFont Resolve() => this.target;

    /// <inheritdoc/>
    public override FontMetrics GetMetrics(float size, float density) => this.target.GetMetrics(size, density);

    /// <inheritdoc/>
    public override void GetGlyphIndices(ReadOnlySpan<uint> codepoints, Span<uint> glyphs) =>
        this.target.GetGlyphIndices(codepoints, glyphs);

    /// <inheritdoc/>
    public override void GetGlyphMetrics(
        ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics) =>
        this.target.GetGlyphMetrics(glyphs, size, density, metrics);

    /// <inheritdoc/>
    public override void GetKerning(ReadOnlySpan<uint> glyphs, float size, float density, Span<float> adjustments) =>
        this.target.GetKerning(glyphs, size, density, adjustments);

    /// <inheritdoc/>
    public override void Shape(ShapeRun run, ShapedGlyphList output) => this.target.Shape(run, output);

    /// <inheritdoc/>
    public override void GetGlyphImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, Span<GlyphImage> images) =>
        this.target.GetGlyphImages(glyphs, subpixelSteps, size, density, embolden, subpixel, images);
}
