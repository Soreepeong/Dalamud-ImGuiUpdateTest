using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts;
using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.SeStrings;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;

namespace Dalamud.Interface.Textures.Internal;

/// <summary>Service responsible for loading and disposing ImGui texture wraps.</summary>
internal sealed partial class TextureManager
{
    [ServiceManager.ServiceDependency]
    private readonly SeStringLayouter seStringLayouter = Service<SeStringLayouter>.Get();

    /// <inheritdoc/>
    public IDalamudTextureWrap CreateTextureFromSeString(
        ReadOnlySpan<byte> text,
        ImFontPtr font = default,
        float fontSizePx = 0,
        float wrapWidth = float.MaxValue,
        string? debugName = null)
    {
        ThreadSafety.AssertMainThread();

        // Drawn once, so with every glyph, however long rasterizing takes.
        using var synchronous = GlyphRasterQueue.Synchronous();
        using var dd = this.seStringLayouter.CreateDrawData(
            text,
            font.IsNull ? ImGui.Font : font,
            fontSizePx > 0 ? fontSizePx : ImGui.GetFontSize(),
            wrapWidth);
        var texture = this.CreateDrawListTexture(debugName ?? nameof(this.CreateTextureFromSeString));
        try
        {
            texture.Size = dd.Data.DisplaySize;
            texture.Draw(dd.DataPtr);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }
}
