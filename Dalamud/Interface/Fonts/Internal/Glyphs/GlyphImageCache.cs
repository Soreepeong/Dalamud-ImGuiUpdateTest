using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dalamud.Interface.Fonts.Internal.Glyphs;

/// <summary>Glyph images of one font in the <see cref="GlyphAtlas"/>, by a key the font makes.</summary>
internal sealed class GlyphImageCache
{
    private readonly Dictionary<ulong, GlyphAtlas.CachedImage> images = [];

    /// <summary>Quantizes an effect amount for a cache key, in 1/8 pixels.</summary>
    /// <param name="value">The amount.</param>
    /// <returns>The quantized amount, 0 to 255.</returns>
    public static ulong Quantize8(float value) => (ulong)Math.Clamp((int)((value * 8f) + 0.5f), 0, 255);

    /// <summary>Gets a cached image, copying it back into the atlas if its slice was cleared lately.</summary>
    /// <param name="key">The key.</param>
    /// <param name="image">The image.</param>
    /// <returns>Whether it is cached, and in the atlas.</returns>
    public bool TryGet(ulong key, out GlyphAtlas.CachedImage image)
    {
        ref var cached = ref CollectionsMarshal.GetValueRefOrNullRef(this.images, key);
        if (Unsafe.IsNullRef(ref cached) || !GlyphAtlas.Shared.TryRestore(ref cached))
        {
            image = default;
            return false;
        }

        image = cached;
        return true;
    }

    /// <summary>Applies synthetic bold to a freshly rasterized bitmap, packs it and caches the result.</summary>
    /// <param name="key">The key.</param>
    /// <param name="bitmap">The bitmap before effects, or null for an empty glyph.</param>
    /// <param name="embolden">Synthetic bold in bitmap pixels.</param>
    /// <returns>The image.</returns>
    public GlyphAtlas.CachedImage Add(ulong key, GlyphBitmap? bitmap, float embolden)
    {
        if (bitmap is not { } b)
            return this.images[key] = GlyphAtlas.CachedImage.Empty;

        var start = FontProfiler.Begin(FontProfiler.Section.Pack);
        b.Embolden(embolden);
        var image = this.images[key] = GlyphAtlas.Shared.Add(b);
        FontProfiler.End(FontProfiler.Section.Pack, start);
        return image;
    }
}
