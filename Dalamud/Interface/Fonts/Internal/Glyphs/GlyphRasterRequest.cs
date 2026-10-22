namespace Dalamud.Interface.Fonts.Internal.Glyphs;

/// <summary>A glyph to rasterize, maybe on the thread pool.</summary>
/// <param name="Font">The font.</param>
/// <param name="Key">The key of the glyph image in the font's cache.</param>
/// <param name="Glyph">The glyph.</param>
/// <param name="DeviceSize">The size in device pixels.</param>
/// <param name="SubpixelX">Fraction of a device pixel the pen is right of a whole pixel.</param>
/// <param name="Subpixel">Whether subpixel coverage is wanted.</param>
/// <param name="Embolden">Synthetic bold, to apply when caching.</param>
/// <seealso cref="GlyphRasterQueue"/>
internal readonly record struct GlyphRasterRequest(
    IConcurrentGlyphRasterizer Font,
    ulong Key,
    uint Glyph,
    float DeviceSize,
    float SubpixelX,
    bool Subpixel,
    float Embolden);
