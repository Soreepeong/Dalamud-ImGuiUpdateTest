using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Queries of fonts for Dalamud itself.</summary>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101", Justification = "Extension receivers are not members")]
internal static unsafe class ImFontPtrExtensions
{
    /// <param name="font">The font.</param>
    extension(ImFontPtr font)
    {
        /// <summary>Gets the glyph for a codepoint.</summary>
        /// <param name="codepoint">The codepoint.</param>
        /// <returns>The glyph id, or 0 if missing or not a Dalamud font.</returns>
        public uint GetGlyphIndex(uint codepoint)
        {
            if (ManagedFont.FromNative(font.Handle) is not { } f)
                return 0;
            Span<uint> glyph = stackalloc uint[1];
            f.GetGlyphIndices([codepoint], glyph);
            return glyph[0];
        }

        /// <summary>Gets the metrics of a glyph.</summary>
        /// <param name="glyph">The glyph.</param>
        /// <param name="size">Size in logical pixels.</param>
        /// <param name="density">Device pixels per logical pixel.</param>
        /// <param name="metrics">Receives the metrics, or empty ones if not a Dalamud font.</param>
        public void GetGlyphMetrics(uint glyph, float size, float density, out GlyphMetrics metrics)
        {
            Unsafe.SkipInit(out metrics);
            if (ManagedFont.FromNative(font.Handle) is { } f)
                f.GetGlyphMetrics([glyph], size, density, new(ref metrics));
            else
                metrics = default;
        }

        /// <summary>Gets the metrics of a font at a size.</summary>
        /// <param name="size">Size in logical pixels.</param>
        /// <param name="density">Device pixels per logical pixel.</param>
        /// <returns>The metrics.</returns>
        public FontMetrics GetMetrics(float size, float density = 1) =>
            ManagedFont.FromNative(font.Handle)?.GetMetrics(size, density) ?? FontMetrics.Fallback(size);

        /// <summary>Adds a reference to the font behind a handle.</summary>
        public void AddRef() => ManagedFont.AddRef(font.Handle);

        /// <summary>Releases a reference to the font behind a handle.</summary>
        public void Release() => ManagedFont.Release(font.Handle);
    }
}
