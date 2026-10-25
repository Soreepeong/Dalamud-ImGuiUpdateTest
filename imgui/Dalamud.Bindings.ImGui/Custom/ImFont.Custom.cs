using System.Numerics;

namespace Dalamud.Bindings.ImGui;

/// <summary>A font and the style of text drawn with it.</summary>
/// <remarks>Native TextEx reads <see cref="Impl"/>, and the color and flags of <see cref="Style"/>, at fixed offsets
/// (ImFontStyleHead in imgui_widgets.cpp). Keep the field order.</remarks>
public partial struct ImFont
{
    /// <summary>Opaque implementation of a font.</summary>
    public readonly void* Impl;

    /// <summary>The style.</summary>
    public readonly ImTextStyle Style;

    /// <summary>Initializes a new instance of the <see cref="ImFont"/> struct.</summary>
    /// <param name="impl">Opaque implementation of a font.</param>
    /// <param name="style">The style.</param>
    public unsafe ImFont(void* impl, in ImTextStyle style)
    {
        this.Impl = impl;
        this.Style = style;
    }
}

/// <summary>A pointer to an <see cref="ImFont"/>.</summary>
public unsafe partial struct ImFontPtr
{
    /// <summary>Gets the style.</summary>
    public readonly ref readonly ImTextStyle Style => ref this.Handle->Style;

    /// <summary>Draws one character, like the removed <c>ImFont::RenderChar</c>.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="size">Size in pixels (after global scaling).</param>
    /// <param name="pos">Top-left position of the line.</param>
    /// <param name="col">Color.</param>
    /// <param name="codepoint">The character.</param>
    public void RenderChar(ImDrawListPtr drawList, float size, Vector2 pos, uint col, uint codepoint)
    {
        if (!System.Text.Rune.TryCreate(codepoint, out var rune))
            return;
        Span<byte> buf = stackalloc byte[4];
        var len = rune.EncodeToUtf8(buf);
        ImGuiP.GetTextDesc(out var desc, drawList: drawList);
        desc.Size = size;
        fixed (byte* p = buf)
            ImGuiNative.AddTextEx(
                drawList, this.Handle, &desc, pos, col, p, p + len, null, ImTextRenderFlags.None, null);
    }
}
