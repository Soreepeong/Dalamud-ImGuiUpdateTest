using System.Diagnostics.CodeAnalysis;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts;

/// <summary>Members of <see cref="ImFontPtr"/> for Dalamud's fonts.</summary>
/// <remarks>For use from the main thread only.</remarks>
[SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101", Justification = "Extension receivers are not members")]
public static unsafe class ImFontExtensions
{
    /// <param name="font">The font.</param>
    extension(ImFontPtr font)
    {
        /// <summary>Pushes this font as it is, with its own style.</summary>
        /// <param name="sizePx">The size, as for <see cref="ImFontRef.Push"/>.</param>
        /// <returns>Calls <see cref="ImGui.PopFont"/> on <see cref="IDisposable.Dispose"/>.</returns>
        /// <remarks>A null font pushes the current font at the size.</remarks>
        /// <seealso cref="PushFamily"/>
        public ImPopFontDisposable Push(float? sizePx = null)
        {
            ThreadSafety.AssertMainThread();
            var target = Font.FromNative(font.Handle)?.Resolve() as CompositeFont;
            ImGui.PushFont(font, FontSizes.ToPushSize(target, sizePx));
            return new(font: true);
        }

        /// <summary>Pushes only the family of this font, keeping the style of the current font.</summary>
        /// <param name="sizePx">The size, as for <see cref="ImFontRef.Push"/>.</param>
        /// <returns>A disposable object that pops the font.</returns>
        /// <remarks>A null font stands for the current font.</remarks>
        /// <seealso cref="Push"/>
        /// <seealso cref="ImGui.PushFont(ImFontPtr, float)"/>
        public ImPopFontDisposable PushFamily(float? sizePx = null) =>
            font.WithStyleForFrame(ImGui.TextStyle).Push(sizePx);

        /// <summary>Gets the font with another style, for use in this frame.</summary>
        /// <param name="style">The style.</param>
        /// <returns>The font in <paramref name="style"/>, the same for the same style within a frame.
        /// <paramref name="font"/> itself if it is not a Dalamud font.</returns>
        /// <remarks>The font is valid until the frame being built has been drawn. Kept longer, it may show another
        /// style. To use a styled font over frames, keep the <see cref="ImFontRef"/> and the style, and call this in
        /// each frame.</remarks>
        public ImFontPtr WithStyleForFrame(in ImTextStyle style)
        {
            ThreadSafety.AssertMainThread();
            return font.IsNull || Font.FromNative(font.Handle) is not { } f ? font : f.GetVariant(style);
        }

        /// <summary>Tests whether a font has a glyph for a codepoint, from its own glyphs or its fallbacks.</summary>
        /// <param name="codepoint">The codepoint.</param>
        /// <returns>Whether the glyph exists.</returns>
        public bool IsGlyphInFont(uint codepoint)
        {
            ThreadSafety.AssertMainThread();
            return font.GetGlyphIndex(codepoint) != 0;
        }
    }
}
