namespace Dalamud.Interface.Fonts.Internal.Glyphs;

/// <summary>A font that can rasterize glyphs on other threads.</summary>
/// <seealso cref="GlyphRasterQueue"/>
internal interface IConcurrentGlyphRasterizer
{
    /// <summary>Rasterizes a glyph. Called on any thread, concurrently.</summary>
    /// <param name="request">What to rasterize.</param>
    /// <returns>The bitmap, or null for an empty glyph.</returns>
    GlyphBitmap? RasterizeConcurrently(in GlyphRasterRequest request);

    /// <summary>Caches a glyph rasterized by <see cref="RasterizeConcurrently"/>. Called on the main thread.</summary>
    /// <param name="request">What was rasterized.</param>
    /// <param name="bitmap">The bitmap, or null for an empty glyph.</param>
    void AddRasterized(in GlyphRasterRequest request, GlyphBitmap? bitmap);
}
