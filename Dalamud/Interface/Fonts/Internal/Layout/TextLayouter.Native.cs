using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;

using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>ImGui's text callbacks, and layouts as ImGui's <see cref="ImTextLayout"/>.</summary>
internal static unsafe partial class TextLayouter
{
    private static readonly ImTextLayoutVtbl* NativeVtbl = CreateNativeVtbl();

    /// <summary>Gives ImGui a reference to a layout.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>The native layout, which ImGui releases.</returns>
    /// <remarks>A layout has one native layout at a time, made on the first reference. The cache holds one while it
    /// has the layout, so that drawing it only counts references.</remarks>
    public static ImTextLayout* ToNative(TextLayout layout)
    {
        var p = (NativeLayout*)layout.NativeHandle;
        if (p is null)
        {
            p = (NativeLayout*)NativeMemory.Alloc((nuint)sizeof(NativeLayout));
            p->Vtbl = NativeVtbl;
            p->Handle = GCHandle.ToIntPtr(GCHandle.Alloc(layout));
            p->RefCount = 0;
            layout.NativeHandle = (nint)p;
        }

        p->RefCount++;
        return (ImTextLayout*)p;
    }

    // Releases a reference. The last frees the native layout, and gives the layout back to the pool if the cache does
    // not have it either.
    private static uint ReleaseNative(NativeLayout* p)
    {
        var refs = --p->RefCount;
        if (refs != 0)
            return refs;
        var handle = GCHandle.FromIntPtr(p->Handle);
        var layout = (TextLayout)handle.Target!;
        handle.Free();
        NativeMemory.Free(p);
        layout.NativeHandle = 0;
        if (!layout.Cached)
            layout.Recycle();
        return 0;
    }

    private static ImTextLayoutVtbl* CreateNativeVtbl()
    {
        var v = (ImTextLayoutVtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(TextLayouter),
            sizeof(ImTextLayoutVtbl));
        v->QueryInterface = (delegate* unmanaged<ImTextLayout*, ImGUID*, void**, int>)&QueryInterfaceImpl;
        v->AddRef = (delegate* unmanaged<ImTextLayout*, uint>)&AddRefImpl;
        v->Release = (delegate* unmanaged<ImTextLayout*, uint>)&ReleaseImpl;
        v->GetBoundingBox = (delegate* unmanaged<ImTextLayout*, Vector4*, Vector4*, void>)&GetBoundingBoxImpl;
        v->GetTextLength = (delegate* unmanaged<ImTextLayout*, int>)&GetTextLengthImpl;
        v->GetEllipsisOffset = (delegate* unmanaged<ImTextLayout*, int>)&GetEllipsisOffsetImpl;
        v->GetLines = (delegate* unmanaged<ImTextLayout*, ImTextLine**, int>)&GetLinesImpl;
        v->GetCaret = (delegate* unmanaged<ImTextLayout*, int, byte, ImTextCaret*, void>)&GetCaretImpl;
        v->HitTest = (delegate* unmanaged<ImTextLayout*, Vector2, ImTextCaret*, void>)&HitTestImpl;
        v->Move =
            (delegate* unmanaged<ImTextLayout*, ImTextCaret*, ImTextMoveFlags, int, int, float, ImTextCaret*,
                void>)&MoveImpl;
        v->GetRangeRects = (delegate* unmanaged<ImTextLayout*, int, int, Vector4*, int, int>)&GetRangeRectsImpl;
        v->Render =
            (delegate* unmanaged<ImTextLayout*, ImDrawList*, Vector2, ImTextTransform*, uint, Vector4*,
                ImTextRenderFlags, void>)&RenderImpl;
        v->RenderRange =
            (delegate* unmanaged<ImTextLayout*, ImDrawList*, Vector2, ImTextTransform*, uint, Vector4*,
                ImTextRenderFlags, int, int, void>)&RenderRangeImpl;
        v->GetRunCount = (delegate* unmanaged<ImTextLayout*, int>)&GetRunCountImpl;
        v->GetRunRects = (delegate* unmanaged<ImTextLayout*, int, Vector4**, int>)&GetRunRectsImpl;
        return v;
    }

    private static TextLayout Get(ImTextLayout* self) =>
        (TextLayout)GCHandle.FromIntPtr(((NativeLayout*)self)->Handle).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int LayoutCallback(
        ImFont* font, ImTextDesc* desc, byte* text, byte* textEnd, ImTextLayout** outLayout)
    {
        *outLayout = null;
        try
        {
            if (ManagedFont.FromNative(font) is not { } managed)
                return E.E_INVALIDARG; // The font is not Dalamud's.
            var d = TextLayoutDesc.FromNative(*desc);

            var bytes = new ReadOnlySpan<byte>(text, (int)(textEnd - text));
            var layout = TextStylers.Get(font->Style.Markup)?.CreateLayout(managed, d, font->Style, bytes);
            *outLayout = ToNative(layout ?? Create(managed, d, font->Style, bytes));
            return 0;
        }
        catch (Exception e)
        {
            Report(e);
            return e.HResult < 0 ? e.HResult : E.E_FAIL;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MetricsCallback(
        ImFont* font, float size, float density, ImTextMetricsFlags flags, ImTextMetrics* outMetrics)
    {
        try
        {
            if (ManagedFont.FromNative(font) is { } managed)
            {
                var m = managed.GetMetrics(size, density);
                if ((flags & ImTextMetricsFlags.DesignMetrics) == 0)
                    m = m.SplitLineGap();
                WriteMetrics(m, outMetrics);
                return;
            }
        }
        catch (Exception e)
        {
            Report(e);
        }

        WriteMetrics(FontMetrics.Fallback(size), outMetrics);

        static void WriteMetrics(in FontMetrics m, ImTextMetrics* outMetrics) =>
            *outMetrics = new(m.Ascent, m.Descent, m.LineHeight, m.UnderlinePosition, m.UnderlineThickness);
    }

    [UnmanagedCallersOnly]
    private static int QueryInterfaceImpl(ImTextLayout* self, ImGUID* iid, void** outObject)
    {
        if (outObject is not null)
            *outObject = null;
        return E.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly]
    private static uint AddRefImpl(ImTextLayout* self) => ++((NativeLayout*)self)->RefCount;

    [UnmanagedCallersOnly]
    private static uint ReleaseImpl(ImTextLayout* self) => ReleaseNative((NativeLayout*)self);

    [UnmanagedCallersOnly]
    private static void GetBoundingBoxImpl(ImTextLayout* self, Vector4* outLayout, Vector4* outInk)
    {
        var layout = Get(self);
        if (outLayout is not null)
            *outLayout = new(0, 0, layout.Size.X, layout.Size.Y);
        if (outInk is not null)
            *outInk = layout.Ink;
    }

    [UnmanagedCallersOnly]
    private static int GetTextLengthImpl(ImTextLayout* self) => Get(self).TextLength;

    [UnmanagedCallersOnly]
    private static int GetEllipsisOffsetImpl(ImTextLayout* self) => Get(self).EllipsisOffset;

    [UnmanagedCallersOnly]
    private static int GetLinesImpl(ImTextLayout* self, ImTextLine** outLines)
    {
        var lines = Get(self).Lines;
        if (outLines is not null)
            *outLines = lines.IsEmpty ? null : (ImTextLine*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(lines));
        return lines.Length;
    }

    [UnmanagedCallersOnly]
    private static void GetCaretImpl(ImTextLayout* self, int textOffset, byte preferPreviousLine, ImTextCaret* outCaret)
    {
        try
        {
            *outCaret = Get(self).GetCaret(textOffset, preferPreviousLine != 0);
        }
        catch (Exception e)
        {
            Report(e);
            *outCaret = default;
        }
    }

    [UnmanagedCallersOnly]
    private static void HitTestImpl(ImTextLayout* self, Vector2 point, ImTextCaret* outCaret)
    {
        try
        {
            *outCaret = Get(self).HitTest(point);
        }
        catch (Exception e)
        {
            Report(e);
            *outCaret = default;
        }
    }

    [UnmanagedCallersOnly]
    private static void MoveImpl(
        ImTextLayout* self, ImTextCaret* from, ImTextMoveFlags flags, int dx, int dy, float preferredInlinePos,
        ImTextCaret* outCaret)
    {
        try
        {
            *outCaret = Get(self).Move(*from, flags, dx, dy, preferredInlinePos);
        }
        catch (Exception e)
        {
            Report(e);
            *outCaret = *from;
        }
    }

    [UnmanagedCallersOnly]
    private static int GetRangeRectsImpl(
        ImTextLayout* self, int textBegin, int textEnd, Vector4* outRects, int capacity)
    {
        try
        {
            return Get(self).GetRangeRects(
                textBegin,
                textEnd,
                outRects is null ? default : new(outRects, Math.Max(capacity, 0)));
        }
        catch (Exception e)
        {
            Report(e);
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static void RenderImpl(
        ImTextLayout* self, ImDrawList* drawList, Vector2 pos, ImTextTransform* transform, uint col, Vector4* clipRect,
        ImTextRenderFlags flags) =>
        RenderRangeCore(self, drawList, pos, transform, col, clipRect, flags, 0, -1);

    [UnmanagedCallersOnly]
    private static void RenderRangeImpl(
        ImTextLayout* self, ImDrawList* drawList, Vector2 pos, ImTextTransform* transform, uint col, Vector4* clipRect,
        ImTextRenderFlags flags, int textBegin, int textEnd) =>
        RenderRangeCore(self, drawList, pos, transform, col, clipRect, flags, textBegin, Math.Max(textEnd, textBegin));

    [UnmanagedCallersOnly]
    private static int GetRunCountImpl(ImTextLayout* self) => Get(self).RunCount;

    [UnmanagedCallersOnly]
    private static int GetRunRectsImpl(ImTextLayout* self, int runIndex, Vector4** outRects)
    {
        var rects = Get(self).GetRunRects(runIndex);
        if (outRects is not null)
            *outRects = rects.IsEmpty ? null : (Vector4*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(rects));
        return rects.Length;
    }

    private static void RenderRangeCore(
        ImTextLayout* self, ImDrawList* drawList, Vector2 pos, ImTextTransform* transform, uint col, Vector4* clipRect,
        ImTextRenderFlags flags, int textBegin, int textEnd)
    {
        try
        {
            Get(self).RenderRange(
                drawList,
                pos,
                col,
                textBegin,
                textEnd,
                clipRect is null ? null : *clipRect,
                flags,
                null,
                transform is null ? null : *transform);
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLayout
    {
        public ImTextLayoutVtbl* Vtbl;
        public nint Handle;
        public uint RefCount;
    }
}
