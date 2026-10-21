using System.Numerics;

namespace Dalamud.Bindings.ImGui;

/// <summary>A font and the style of text drawn with it.</summary>
public partial struct ImFont
{
    /// <summary>Opaque implementation of a font.</summary>
    public void* Impl;

    /// <summary>The style.</summary>
    public ImTextStyle Style;
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
        ImGuiP.GetTextDesc(out var desc);
        desc.Size = size;
        fixed (byte* p = buf)
            ImGuiNative.AddTextEx(drawList, this.Handle, &desc, pos, col, p, p + len, null, ImTextRenderFlags.None, null);
    }
}

/// <summary>Calls through the <see cref="ImTextLayout"/> interface.</summary>
public unsafe partial struct ImTextLayoutPtr
{
    private ImTextLayoutVtbl* Vtbl => this.Handle->LpVtbl;

    /// <summary>Adds a reference.</summary>
    /// <returns>The new reference count.</returns>
    public uint AddRef() => ((delegate* unmanaged<ImTextLayout*, uint>)this.Vtbl->AddRef)(this.Handle);

    /// <summary>Releases a reference.</summary>
    /// <returns>The new reference count.</returns>
    public uint Release() => ((delegate* unmanaged<ImTextLayout*, uint>)this.Vtbl->Release)(this.Handle);

    /// <summary>Gets the layout box (from advances and line heights) and the ink box (what is drawn).</summary>
    /// <param name="ink">The ink box (x0, y0, x1, y1).</param>
    /// <returns>The layout box (x0, y0, x1, y1).</returns>
    public Vector4 GetBoundingBox(out Vector4 ink)
    {
        Vector4 layout;
        Vector4 inkBox;
        ((delegate* unmanaged<ImTextLayout*, Vector4*, Vector4*, void>)this.Vtbl->GetBoundingBox)(this.Handle, &layout, &inkBox);
        ink = inkBox;
        return layout;
    }

    /// <summary>Gets the number of bytes laid out.</summary>
    /// <returns>The length.</returns>
    public int GetTextLength() => ((delegate* unmanaged<ImTextLayout*, int>)this.Vtbl->GetTextLength)(this.Handle);

    /// <summary>Gets the first byte hidden by <see cref="ImTextLayoutFlags.Ellipsis"/>.</summary>
    /// <returns>The offset, or -1.</returns>
    public int GetEllipsisOffset() => ((delegate* unmanaged<ImTextLayout*, int>)this.Vtbl->GetEllipsisOffset)(this.Handle);

    /// <summary>Gets the lines, owned by the layout.</summary>
    /// <returns>The lines.</returns>
    public ReadOnlySpan<ImTextLine> GetLines()
    {
        ImTextLine* lines = null;
        var count = ((delegate* unmanaged<ImTextLayout*, ImTextLine**, int>)this.Vtbl->GetLines)(this.Handle, &lines);
        return count <= 0 ? default : new(lines, count);
    }

    /// <summary>Gets the caret at a byte offset.</summary>
    /// <param name="textOffset">The byte offset.</param>
    /// <param name="preferPreviousLine">At a wrap point, the end of the previous line.</param>
    /// <returns>The caret.</returns>
    public ImTextCaret GetCaret(int textOffset, bool preferPreviousLine = false)
    {
        ImTextCaret caret;
        ((delegate* unmanaged<ImTextLayout*, int, byte, ImTextCaret*, void>)this.Vtbl->GetCaret)(
            this.Handle,
            textOffset,
            preferPreviousLine ? (byte)1 : (byte)0,
            &caret);
        return caret;
    }

    /// <summary>Gets the caret nearest to a point, with the run or object under it.</summary>
    /// <param name="point">The point, relative to the layout origin.</param>
    /// <returns>The caret.</returns>
    public ImTextCaret HitTest(Vector2 point)
    {
        ImTextCaret caret;
        ((delegate* unmanaged<ImTextLayout*, Vector2, ImTextCaret*, void>)this.Vtbl->HitTest)(this.Handle, point, &caret);
        return caret;
    }

    /// <summary>Moves a caret. It moves along the block axis first, then the inline axis, then dx units along the
    /// text.</summary>
    /// <param name="from">The caret to move from.</param>
    /// <param name="flags">Where to move.</param>
    /// <param name="dx">Units along the text (the unit is in <paramref name="flags"/>), after the axes.</param>
    /// <param name="dy">Lines, for <see cref="ImTextMoveFlags.BlockRelative"/>.</param>
    /// <param name="preferredInlinePos">Position along the line to keep. If &lt; 0, it is taken from the caret.</param>
    /// <returns>The caret.</returns>
    public ImTextCaret Move(in ImTextCaret from, ImTextMoveFlags flags, int dx, int dy, float preferredInlinePos = -1)
    {
        ImTextCaret caret;
        fixed (ImTextCaret* pFrom = &from)
        {
            ((delegate* unmanaged<ImTextLayout*, ImTextCaret*, ImTextMoveFlags, int, int, float, ImTextCaret*, void>)this.Vtbl->Move)(
                this.Handle,
                pFrom,
                flags,
                dx,
                dy,
                preferredInlinePos,
                &caret);
        }

        return caret;
    }

    /// <summary>Gets the rectangles of a range, one per line it spans.</summary>
    /// <param name="textBegin">Start byte offset.</param>
    /// <param name="textEnd">End byte offset.</param>
    /// <param name="rects">Receives the rectangles (x0, y0, x1, y1).</param>
    /// <returns>The number of rectangles, which may exceed the length of <paramref name="rects"/>.</returns>
    public int GetRangeRects(int textBegin, int textEnd, Span<Vector4> rects)
    {
        fixed (Vector4* p = rects)
        {
            return ((delegate* unmanaged<ImTextLayout*, int, int, Vector4*, int, int>)this.Vtbl->GetRangeRects)(
                this.Handle,
                textBegin,
                textEnd,
                p,
                rects.Length);
        }
    }

    /// <summary>Draws the layout, clipped to the draw list's clip rectangle.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color.</param>
    /// <param name="flags">Rendering-only flags.</param>
    public void Render(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, ImTextRenderFlags flags = ImTextRenderFlags.None) =>
        this.RenderCore(drawList, pos, col, null, flags, null);

    /// <summary>Draws the layout.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color.</param>
    /// <param name="clipRect">Clip rectangle (x0, y0, x1, y1).</param>
    /// <param name="flags">Rendering-only flags.</param>
    public void Render(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, in Vector4 clipRect,
        ImTextRenderFlags flags = ImTextRenderFlags.None)
    {
        fixed (Vector4* pClip = &clipRect)
            this.RenderCore(drawList, pos, col, pClip, flags, null);
    }

    /// <summary>Draws the layout transformed around <paramref name="pos"/>, clipped to the draw list's clip
    /// rectangle.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color.</param>
    /// <param name="transform">Transform.</param>
    /// <param name="flags">Rendering-only flags.</param>
    public void Render(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, in ImTextTransform transform,
        ImTextRenderFlags flags = ImTextRenderFlags.None)
    {
        fixed (ImTextTransform* pTransform = &transform)
            this.RenderCore(drawList, pos, col, null, flags, pTransform);
    }

    /// <summary>Draws the layout transformed around <paramref name="pos"/>.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color.</param>
    /// <param name="clipRect">Clip rectangle (x0, y0, x1, y1), untransformed.</param>
    /// <param name="transform">Transform.</param>
    /// <param name="flags">Rendering-only flags.</param>
    public void Render(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, in Vector4 clipRect, in ImTextTransform transform,
        ImTextRenderFlags flags = ImTextRenderFlags.None)
    {
        fixed (Vector4* pClip = &clipRect)
        fixed (ImTextTransform* pTransform = &transform)
            this.RenderCore(drawList, pos, col, pClip, flags, pTransform);
    }

    /// <summary>Draws the glyphs of a range of the layout, clipped to the draw list's clip rectangle.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color.</param>
    /// <param name="textBegin">Start byte offset.</param>
    /// <param name="textEnd">End byte offset.</param>
    /// <param name="flags">Rendering-only flags.</param>
    public void RenderRange(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, int textBegin, int textEnd,
        ImTextRenderFlags flags = ImTextRenderFlags.None) =>
        this.RenderRangeCore(drawList, pos, col, textBegin, textEnd, null, flags, null);

    /// <summary>Draws the glyphs of a range of the layout.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color.</param>
    /// <param name="textBegin">Start byte offset.</param>
    /// <param name="textEnd">End byte offset.</param>
    /// <param name="clipRect">Clip rectangle (x0, y0, x1, y1).</param>
    /// <param name="flags">Rendering-only flags.</param>
    public void RenderRange(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, int textBegin, int textEnd, in Vector4 clipRect,
        ImTextRenderFlags flags = ImTextRenderFlags.None)
    {
        fixed (Vector4* pClip = &clipRect)
            this.RenderRangeCore(drawList, pos, col, textBegin, textEnd, pClip, flags, null);
    }

    /// <summary>Draws the glyphs of a range of the layout transformed around <paramref name="pos"/>, clipped to the
    /// draw list's clip rectangle.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color.</param>
    /// <param name="textBegin">Start byte offset.</param>
    /// <param name="textEnd">End byte offset.</param>
    /// <param name="transform">Transform.</param>
    /// <param name="flags">Rendering-only flags.</param>
    public void RenderRange(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, int textBegin, int textEnd,
        in ImTextTransform transform, ImTextRenderFlags flags = ImTextRenderFlags.None)
    {
        fixed (ImTextTransform* pTransform = &transform)
            this.RenderRangeCore(drawList, pos, col, textBegin, textEnd, null, flags, pTransform);
    }

    /// <summary>Draws the glyphs of a range of the layout transformed around <paramref name="pos"/>.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color.</param>
    /// <param name="textBegin">Start byte offset.</param>
    /// <param name="textEnd">End byte offset.</param>
    /// <param name="clipRect">Clip rectangle (x0, y0, x1, y1), untransformed.</param>
    /// <param name="transform">Transform.</param>
    /// <param name="flags">Rendering-only flags.</param>
    public void RenderRange(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, int textBegin, int textEnd, in Vector4 clipRect,
        in ImTextTransform transform, ImTextRenderFlags flags = ImTextRenderFlags.None)
    {
        fixed (Vector4* pClip = &clipRect)
        fixed (ImTextTransform* pTransform = &transform)
            this.RenderRangeCore(drawList, pos, col, textBegin, textEnd, pClip, flags, pTransform);
    }

    /// <summary>Gets the number of runs.</summary>
    /// <returns>The number of runs.</returns>
    public int GetRunCount() => ((delegate* unmanaged<ImTextLayout*, int>)this.Vtbl->GetRunCount)(this.Handle);

    /// <summary>Gets the hover boxes of a run, one per line it spans. The layout owns them.</summary>
    /// <param name="runIndex">The run.</param>
    /// <returns>The rectangles (x0, y0, x1, y1).</returns>
    public ReadOnlySpan<Vector4> GetRunRects(int runIndex)
    {
        Vector4* rects = null;
        var count = ((delegate* unmanaged<ImTextLayout*, int, Vector4**, int>)this.Vtbl->GetRunRects)(this.Handle, runIndex, &rects);
        return count <= 0 ? default : new(rects, count);
    }

    private void RenderCore(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, Vector4* clipRect, ImTextRenderFlags flags,
        ImTextTransform* transform) =>
        ((delegate* unmanaged<ImTextLayout*, ImDrawList*, Vector2, ImTextTransform*, uint, Vector4*, ImTextRenderFlags, void>)this.Vtbl->Render)(
            this.Handle,
            drawList,
            pos,
            transform,
            col,
            clipRect,
            flags);

    private void RenderRangeCore(
        ImDrawListPtr drawList, Vector2 pos, ImTextColor col, int textBegin, int textEnd, Vector4* clipRect,
        ImTextRenderFlags flags, ImTextTransform* transform) =>
        ((delegate* unmanaged<ImTextLayout*, ImDrawList*, Vector2, ImTextTransform*, uint, Vector4*, ImTextRenderFlags, int, int, void>)this.Vtbl->RenderRange)(
            this.Handle,
            drawList,
            pos,
            transform,
            col,
            clipRect,
            flags,
            textBegin,
            textEnd);
}
