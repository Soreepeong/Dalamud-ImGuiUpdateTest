using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Sources;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Drawing.</summary>
internal sealed unsafe partial class TextLayout
{
    // Run styles resolved on the stack for a render, beyond which they are in a rented array.
    private const int MaxStackStyles = 16;

    // A cached step for a glyph with no image kept.
    private const byte NoCachedImage = byte.MaxValue;

    // Reused by drawing, which is on the main thread only.
    private readonly List<DecorationSpan> decorationSpans = [];
    private readonly List<(int Begin, int End)> visibleRanges = [];

    // The slices the kept images are in, with their generations then.
    private readonly List<(GlyphKind Kind, int Slice, int Generation)> cachedSlices = [];

    // Images kept from earlier renders, for each glyph and then for each glyph's shadow, with the subpixel step each
    // is for, or NoCachedImage. They are for the atlas, the snapping and the subpixel setting below.
    private GlyphImage[] cachedImages = [];
    private byte[] cachedSteps = [];
    private GlyphAtlas? cachedAtlas;
    private bool cachedSnap;
    private bool cachedSubpixel;

    // Of the render going on, for decorations that draw glyphs.
    private int renderTextBegin;
    private int renderTextEnd;
    private bool renderSnap;
    private Vector4? renderClip;
    private float largestItemSize = -1;

    private delegate float KeyOf<T>(in T value);

    private enum Pass
    {
        Shadow,
        Edge,
        Fill,
    }

    /// <summary>Draws the layout. Inline objects are the caller's to draw.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color, whose alpha also multiplies that of colors given by styles.</param>
    /// <param name="clipRect">Clip rectangle in untransformed space, or null for the draw list's.</param>
    /// <param name="flags">Rendering-only flags.</param>
    /// <param name="styleOverride">Style for text outside runs with their own, instead of the layout's default
    /// (only its rendering fields apply).</param>
    /// <param name="transform">Transform around <paramref name="pos"/>, or null.</param>
    /// <seealso cref="GetObjectPosition"/>
    public void Render(
        ImDrawListPtr drawList, Vector2 pos, uint col, Vector4? clipRect = null,
        ImTextRenderFlags flags = ImTextRenderFlags.None,
        ImTextStyle? styleOverride = null, ImTextTransform? transform = null) =>
        this.RenderRange(drawList, pos, col, 0, -1, clipRect, flags, styleOverride, transform);

    /// <summary>Draws the graphemes of the layout that start within a byte range.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="col">Text color, whose alpha also multiplies that of colors given by styles.</param>
    /// <param name="textBegin">Start byte offset.</param>
    /// <param name="textEnd">End byte offset, or less than 0 for the end of the text.</param>
    /// <param name="clipRect">Clip rectangle in untransformed space, or null for the draw list's.</param>
    /// <param name="flags">Rendering-only flags.</param>
    /// <param name="styleOverride">Style for text outside runs with their own, instead of the layout's default
    /// (only its rendering fields apply).</param>
    /// <param name="transform">Transform around <paramref name="pos"/>, or null.</param>
    public void RenderRange(
        ImDrawListPtr drawList, Vector2 pos, uint col, int textBegin, int textEnd, Vector4? clipRect = null,
        ImTextRenderFlags flags = ImTextRenderFlags.None, ImTextStyle? styleOverride = null,
        ImTextTransform? transform = null)
    {
        var start = FontProfiler.Begin(FontProfiler.Section.Render);
        if (textEnd < 0)
            textEnd = int.MaxValue;
        var snap = transform is null && (flags & ImTextRenderFlags.NoPixelSnap) == 0;
        if (snap)
            pos = new(MathF.Truncate(pos.X), MathF.Truncate(pos.Y));
        var clip = clipRect ?? drawList.CmdHeader.ClipRect;
        var fineClip = transform is not null || (flags & ImTextRenderFlags.CpuFineClip) != 0;
        var density = this.Density;
        var dl = drawList.Handle;

        // Runs with some user data may be drawn with another style, such as a hovered link.
        if (transform is not null || this.RenderHook is not { } hook ||
            !hook.BeginRender(this, drawList, pos, out var highlightUserData, out var highlightStyle))
        {
            (highlightUserData, highlightStyle) = (0, default);
        }

        var styleCount = this.runCount + 1;
        var rentedStyles = styleCount > MaxStackStyles ? ArrayPool<ImTextStyle>.Shared.Rent(styleCount) : null;
        var styles = rentedStyles is null ? stackalloc ImTextStyle[MaxStackStyles] : rentedStyles.AsSpan();
        this.ResolveStyles(styles, styleOverride, highlightUserData, highlightStyle);

        this.FindVisibleItems(pos, clip, transform is null && !this.vertical);
        var layers = DecorationLayersOf(styles[..styleCount]);
        Vector4? glyphClip = fineClip ? clip : null;
        (this.renderTextBegin, this.renderTextEnd, this.renderSnap, this.renderClip) =
            (textBegin, textEnd, snap, glyphClip);
        this.ValidateImageCache(snap);

        this.DrawDecorationLayer(dl, pos, col, styles, transform, layers, TextDecorationLayer.BelowText);
        if ((flags & ImTextRenderFlags.CompleteGlyphs) != 0)
        {
            this.DrawGlyphsSynchronously(
                dl, pos, col, textBegin, textEnd, styles, density, snap, glyphClip, transform, layers);
        }
        else
        {
            this.DrawGlyphs(dl, pos, col, textBegin, textEnd, styles, density, snap, glyphClip, transform, layers);
        }

        // Inline objects, for layouts that draw them. Not with transforms.
        if (this.RenderHook is { } drawer && transform is null)
        {
            foreach (ref readonly var item in this.VisibleItems())
            {
                if (item.Kind != ItemKind.Object || item.Offset < textBegin || item.Offset >= textEnd ||
                    this.GetObjectPosition(item.Object) is not { } pen)
                {
                    continue;
                }

                drawer.DrawObject(
                    drawList, item.Object, pos + pen + new Vector2(0, this.objects[item.Object].Bounds.Y), col);
            }
        }

        this.DrawDecorationLayer(dl, pos, col, styles, transform, layers, TextDecorationLayer.OverText);
        if (rentedStyles is not null)
            ArrayPool<ImTextStyle>.Shared.Return(rentedStyles);
        FontProfiler.End(FontProfiler.Section.Render, start);
    }

    /// <summary>Draws the glyphs of a span of a decoration being drawn.</summary>
    /// <param name="span">The span.</param>
    /// <param name="color">The color to draw with.</param>
    /// <param name="width">How far to dilate the glyphs, in pixels.</param>
    /// <param name="softness">How soft the dilated edge is.</param>
    /// <param name="offset">How far to move the glyphs, in pixels.</param>
    /// <remarks>Glyphs not moved share the images kept for the fill. Others are got without keeping them, so that they
    /// do not take the place of those.</remarks>
    /// <seealso cref="TextDecorationSpan.DrawGlyphs"/>
    internal void DrawDecorationGlyphs(
        in TextDecorationSpan span, uint color, float width, float softness, Vector2 offset)
    {
        var itemSpan = CollectionsMarshal.AsSpan(this.items)[span.ItemBegin..span.ItemEnd];
        var maxQuads = 0;
        foreach (ref readonly var item in itemSpan)
        {
            if (item.Kind is ItemKind.Glyph or ItemKind.Ellipsis)
                maxQuads += item.GlyphEnd - item.GlyphBegin;
        }

        if (maxQuads == 0)
            return;

        var density = this.Density;
        var packedSoftness = (byte)Math.Clamp(MathF.Round(softness * 64), 0, byte.MaxValue);
        var quads = new QuadWriter(span.DrawListHandle, maxQuads);
        foreach (ref readonly var item in itemSpan)
        {
            if (item.Kind is not (ItemKind.Glyph or ItemKind.Ellipsis) || item.GlyphBegin == item.GlyphEnd ||
                item.Offset < this.renderTextBegin || item.Offset >= this.renderTextEnd)
            {
                continue;
            }

            this.DrawItemGlyphs(
                ref quads,
                item,
                this.ItemPen(item, span.Origin) + offset,
                span.Origin,
                density,
                this.renderSnap,
                this.renderClip,
                span.Transform,
                color,
                Math.Max(width, 0) * density,
                packedSoftness,
                offset == Vector2.Zero ? 0 : -1);
        }

        quads.Finish();
    }

    // The layers the decorations of the styles are drawn in, as DrawDecorations takes them. Most text has none, and
    // going through the items for them costs.
    private static TextDecorationFlags DecorationLayersOf(ReadOnlySpan<ImTextStyle> styles)
    {
        var layers = TextDecorationFlags.None;
        foreach (ref readonly var st in styles)
        {
            if (st.Background.Alpha != 0)
                layers |= TextDecorationFlags.BelowText;
            if (st.Lines.IsOn && TextDecorationRegistry.TryGet(ImTextDecorationId.Line, out var lines))
                layers |= lines.LayersOf(st.Lines.ToDecoration());
            if (!st.ExtraDecoration.IsNone && TextDecorationRegistry.TryGet(st.ExtraDecoration.Id, out var extra))
                layers |= extra.LayersOf(st.ExtraDecoration);
        }

        return layers;
    }

    private static TextDecorationFlags LayerFlag(TextDecorationLayer layer) => (TextDecorationFlags)(1 << (int)layer);

    private static uint Tag(uint mode, in GlyphImage img) =>
        GlyphAtlas.ExtTag(mode, img.Kind, img.Slice) | (img.CorrectCoverage ? GlyphAtlas.ExtCorrect : 0);

    // Grows a glyph quad by the reach of a dilation, and makes its tag.
    private static uint Dilate(
        in GlyphImage img, float radius, byte packedSoftness, ref Vector4 rect, ref Vector2 uv0, ref Vector2 uv1)
    {
        // Subpixel glyphs take three texels across per pixel. The radius and the reach are in pixels of the bitmap.
        var subpixel = img.Kind == GlyphKind.Subpixel;
        var tag = Tag(GlyphAtlas.ExtDilate | (subpixel ? GlyphAtlas.ExtRgb : 0), img);
        var texels = ((int)(img.TexelMax & 0xFFFF) - (int)(img.TexelMin & 0xFFFF)) / (subpixel ? 3 : 1);
        var perTexel = (img.Rect.Z - img.Rect.X) / texels;
        var r = (uint)Math.Clamp(MathF.Round(radius / perTexel * 16), 0, GlyphAtlas.ExtMaxReach * 16);
        uint k = packedSoftness;
        var pad = r == 0 ? 0 : Math.Min(MathF.Ceiling(RimEnd(r / 16f, k / 64f)), GlyphAtlas.ExtMaxReach);
        var uvPad = new Vector2(pad * (subpixel ? 3 : 1), pad) / GlyphAtlas.SliceSize;
        uv0 -= uvPad;
        uv1 += uvPad;
        rect += new Vector4(-pad, -pad, pad, pad) * perTexel;
        return tag | (r << 16) | (k << 24);
    }

    // Where the weight falls to 0, in texels, as ExtRim(radius, k).z of imgui-text.hlsli.
    private static float RimEnd(float radius, float k)
    {
        k = k > 1 ? Math.Min(k, Math.Max(GlyphAtlas.ExtMaxReach / radius, 1)) : k;
        var inner = Math.Max(1 - k, 0) * radius;
        var outer = Math.Max(k, 1) * radius;
        return ((inner + outer) * 0.5f) + 0.5f + (0.5f * Math.Max(outer - inner, 1));
    }

    private static uint MulAlpha(uint color, uint alphaSource) =>
        (color & 0x00FFFFFFu) | (((color >> 24) * (alphaSource >> 24) / 255) << 24);

    private static Vector2 Apply(in ImTextTransform t, Vector2 pos, Vector2 p)
    {
        var d = p - pos;
        return pos + (t.AxisX * d.X) + (t.AxisY * d.Y);
    }

    private static bool Clip(ref Vector2 p0, ref Vector2 p1, ref Vector2 uv0, ref Vector2 uv1, in Vector4 clip)
    {
        if (p0.X >= clip.Z || p1.X <= clip.X || p0.Y >= clip.W || p1.Y <= clip.Y)
            return false;
        var size = p1 - p0;
        var uvSize = uv1 - uv0;
        if (p0.X < clip.X)
        {
            uv0.X += uvSize.X * (clip.X - p0.X) / size.X;
            p0.X = clip.X;
        }

        if (p1.X > clip.Z)
        {
            uv1.X -= uvSize.X * (p1.X - clip.Z) / size.X;
            p1.X = clip.Z;
        }

        if (p0.Y < clip.Y)
        {
            uv0.Y += uvSize.Y * (clip.Y - p0.Y) / size.Y;
            p0.Y = clip.Y;
        }

        if (p1.Y > clip.W)
        {
            uv1.Y -= uvSize.Y * (p1.Y - clip.W) / size.Y;
            p1.Y = clip.W;
        }

        return true;
    }

    // A glyph quad, clipped on the CPU if asked, slanted around the baseline for synthetic italic, then transformed.
    private static void Emit(
        ref QuadWriter quads, Vector2 p0, Vector2 p1, Vector2 uv0, Vector2 uv1, uint color, uint tag, uint texelMin,
        uint texelMax, uint maskTexelMin, uint mask, float slant, float baselineY, Vector4? fineClip, Vector2 pos,
        ImTextTransform? transform)
    {
        // The fields, not ImDrawVert.Ext, which makes a span per access in unoptimized builds.
        var vertex = new ImDrawVert
        {
            Col = color, Ext_0 = tag, Ext_1 = texelMin, Ext_2 = texelMax, Ext_3 = maskTexelMin, Ext_4 = mask,
        };

        if (fineClip is { } clip && !Clip(ref p0, ref p1, ref uv0, ref uv1, clip))
            return;
        Span<Vector2> q = [new(p0.X, p0.Y), new(p1.X, p0.Y), new(p1.X, p1.Y), new(p0.X, p1.Y)];
        if (slant != 0)
        {
            for (var i = 0; i < 4; i++)
                q[i].X += (baselineY - q[i].Y) * slant;
        }

        if (transform is { } t)
        {
            for (var i = 0; i < 4; i++)
                q[i] = Apply(t, pos, q[i]);
        }

        quads.Add(q[0], q[1], q[2], q[3], uv0, uv1, vertex);
    }

    // The first index whose key is at least the value (or more than it, if strictly), in a span of ascending keys.
    private static int LowerBound<T>(ReadOnlySpan<T> span, float value, KeyOf<T> key, bool strictly = false)
    {
        var (lo, hi) = (0, span.Length);
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var k = key(span[mid]);
            if (strictly ? k <= value : k < value)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    // DrawGlyphs, rasterizing every glyph on this thread. Apart, as its using block costs the common path otherwise.
    private void DrawGlyphsSynchronously(
        ImDrawList* dl, Vector2 pos, uint col, int textBegin, int textEnd, ReadOnlySpan<ImTextStyle> styles,
        float density, bool snap, Vector4? fineClip, ImTextTransform? transform, TextDecorationFlags decorationLayers)
    {
        using (GlyphRasterQueue.Synchronous())
        {
            this.DrawGlyphs(
                dl, pos, col, textBegin, textEnd, styles, density, snap, fineClip, transform, decorationLayers);
        }
    }

    // Draws the glyphs of the visible items in the byte range, shadows first, then edges, then fills, with the
    // decorations of the layers between them. Glyphs being rasterized are drawn as nothing.
    private void DrawGlyphs(
        ImDrawList* dl, Vector2 pos, uint col, int textBegin, int textEnd, ReadOnlySpan<ImTextStyle> styles,
        float density, bool snap, Vector4? fineClip, ImTextTransform? transform, TextDecorationFlags decorationLayers)
    {
        // At most a quad per glyph per pass its style draws, reserved at once rather than a native call per quad.
        var maxQuads = 0;
        foreach (ref readonly var item in this.VisibleItems())
        {
            if (item.Kind is not (ItemKind.Glyph or ItemKind.Ellipsis) || item.Offset < textBegin ||
                item.Offset >= textEnd)
            {
                continue;
            }

            ref readonly var st = ref styles[item.Run + 1];
            var passes = 1 + (st.Shadow.IsOn ? 1 : 0) + (st.Edge.IsOn ? 1 : 0);
            maxQuads += (item.GlyphEnd - item.GlyphBegin) * passes;
        }

        var quads = new QuadWriter(dl, maxQuads);
        for (var pass = Pass.Shadow; pass <= Pass.Fill; pass++)
        {
            // The decorations of the layer before the pass. Nothing else may draw while quads are reserved.
            var layer = pass == Pass.Edge ? TextDecorationLayer.BelowEdge : TextDecorationLayer.BelowFill;
            if (pass != Pass.Shadow && (decorationLayers & LayerFlag(layer)) != 0)
            {
                quads.Finish();
                this.DrawDecorationLayer(dl, pos, col, styles, transform, decorationLayers, layer);
                quads = new QuadWriter(dl, maxQuads);
            }

            if (maxQuads == 0)
                continue;

            foreach (ref readonly var item in this.VisibleItems())
            {
                if (item.Kind is not (ItemKind.Glyph or ItemKind.Ellipsis) || item.GlyphBegin == item.GlyphEnd ||
                    item.Offset < textBegin || item.Offset >= textEnd)
                {
                    continue;
                }

                ref readonly var st = ref styles[item.Run + 1];
                var color = st.Color != 0 ? MulAlpha(st.Color, col) : col;
                var offset = Vector2.Zero;
                var edge = st.Edge;

                // In device pixels, or negative to draw the glyph as it is. Shadows are of the edged glyph.
                var dilation = -1f;
                switch (pass)
                {
                    case Pass.Shadow:
                        var shadow = st.Shadow;
                        if (!shadow.IsOn)
                            continue;
                        color = MulAlpha(shadow.Color, col);
                        offset = shadow.Offset * item.Size;
                        dilation = edge.IsOn ? edge.WidthAt(item.Size) * density : 0;
                        break;
                    case Pass.Edge:
                        if (!edge.IsOn)
                            continue;
                        color = MulAlpha(edge.Color, col);
                        dilation = edge.WidthAt(item.Size) * density;
                        break;
                }

                // Shadows are drawn at pens of their own, so their images are kept apart.
                this.DrawItemGlyphs(
                    ref quads,
                    item,
                    this.ItemPen(item, pos) + offset,
                    pos,
                    density,
                    snap,
                    fineClip,
                    transform,
                    color,
                    dilation,
                    edge.PackedSoftness,
                    pass == Pass.Shadow ? this.glyphs.Count : 0);
            }
        }

        quads.Finish();
    }

    // Where an item's glyphs are drawn from, before an offset. Vertical text centers the item's ink in the column.
    private Vector2 ItemPen(in Item item, Vector2 pos)
    {
        var line = this.lines[item.Line];
        if (!this.vertical)
            return pos + new Vector2(item.X, line.BlockPos + line.Baseline);

        var left = this.Size.X - line.BlockPos - line.BlockSize;
        var inkWidth = item.Bounds.Z - item.Bounds.X;
        var ascent = MathF.Round(this.MetricsOf(item.FontIndex, item.Size).Ascent);
        return pos + new Vector2(left + ((line.BlockSize - inkWidth) * 0.5f) - item.Bounds.X, item.X + ascent);
    }

    // Draws the glyphs of an item in a color, dilated by device pixels, or as they are if negative. Their images are
    // kept at a cache base, a glyph count apart for each set, or got without keeping them if it is negative.
    private void DrawItemGlyphs(
        ref QuadWriter quads, in Item item, Vector2 itemPen, Vector2 pos, float density, bool snap, Vector4? fineClip,
        ImTextTransform? transform, uint color, float dilation, byte packedSoftness, int cacheBase)
    {
        // The images are kept from earlier renders for the same steps, or got in one call for the item.
        var glyphSpan = CollectionsMarshal.AsSpan(this.glyphs);
        var lookup = GlyphLookup.Get(item.GlyphEnd - item.GlyphBegin);
        var count = 0;
        for (var gi = item.GlyphBegin; gi < item.GlyphEnd; gi++)
        {
            ref readonly var glyph = ref glyphSpan[gi];
            if (glyph.Id == 0)
                continue;

            // In device pixels. Snapping gives whole-pixel y, and whole-pixel x and a step right of it for the font.
            // A float times a power of 2 is exact, so one floor gives both.
            var pen = itemPen + glyph.Pos;
            var dx = pen.X * density;
            var dy = pen.Y * density;
            if (snap)
            {
                var steps = (int)MathF.Floor(dx * ManagedFont.SubpixelStepCount);
                lookup.Pens[count] = new(steps >> ManagedFont.SubpixelStepShift, MathF.Round(dy));
                lookup.Steps[count] = (byte)(steps & (ManagedFont.SubpixelStepCount - 1));
            }
            else
            {
                lookup.Pens[count] = new(dx, dy);
                lookup.Steps[count] = 0;
            }

            lookup.Slots[count] = cacheBase + gi;
            lookup.Ids[count++] = glyph.Id;
        }

        var cached = cacheBase >= 0;
        var allCached = cached;
        for (var k = 0; k < count && allCached; k++)
            allCached = this.cachedSteps[lookup.Slots[k]] == lookup.Steps[k];
        if (!allCached)
            this.GetGlyphImages(this.fonts[item.FontIndex], item.Size, density, snap, lookup, count, cached);

        for (var k = 0; k < count; k++)
        {
            ref readonly var img = ref cached ? ref this.cachedImages[lookup.Slots[k]] : ref lookup.Images[k];
            if (img.IsEmpty)
                continue;
            var penDevice = lookup.Pens[k];

            var rect = img.Rect;
            var uv0 = img.Uv0;
            var uv1 = img.Uv1;
            var tag = dilation >= 0
                          ? Dilate(img, dilation, packedSoftness, ref rect, ref uv0, ref uv1)
                          : Tag(
                              img.Kind switch
                              {
                                  GlyphKind.Color => GlyphAtlas.ExtColor,
                                  GlyphKind.Subpixel => GlyphAtlas.ExtSubpixel,
                                  _ => GlyphAtlas.ExtGlyph,
                              },
                              img);
            var mask = dilation < 0 && img.MaskSlice >= 0 ? 1u | ((uint)img.MaskSlice << 3) : 0;
            var a = new Vector2(penDevice.X + rect.X, penDevice.Y + rect.Y) / density;
            var c = new Vector2(penDevice.X + rect.Z, penDevice.Y + rect.W) / density;
            Emit(
                ref quads,
                a,
                c,
                uv0,
                uv1,
                color,
                tag,
                img.TexelMin,
                img.TexelMax,
                img.MaskTexelMin,
                mask,
                img.Slant,
                penDevice.Y / density,
                fineClip,
                pos,
                transform);
        }
    }

    // Keeps the images of earlier renders if the atlas still has them where they were, and nothing that changes them
    // changed. Their slices are counted as used, as getting the images again would have done.
    private void ValidateImageCache(bool snap)
    {
        var atlas = GlyphAtlas.Shared;
        var subpixel = SubpixelSettings.Enabled;
        var slots = this.glyphs.Count * 2;
        var valid = this.cachedAtlas == atlas && this.cachedSnap == snap && this.cachedSubpixel == subpixel &&
                    this.cachedSteps.Length >= slots;
        if (valid)
        {
            foreach (ref readonly var s in CollectionsMarshal.AsSpan(this.cachedSlices))
            {
                if (!atlas.TryUse(s.Kind, s.Slice, s.Generation))
                {
                    valid = false;
                    break;
                }
            }
        }

        if (valid)
            return;

        if (this.cachedSteps.Length < slots)
        {
            this.cachedSteps = new byte[slots];
            this.cachedImages = new GlyphImage[slots];
        }

        this.cachedSteps.AsSpan(0, slots).Fill(NoCachedImage);
        this.cachedSlices.Clear();
        (this.cachedAtlas, this.cachedSnap, this.cachedSubpixel) = (atlas, snap, subpixel);
    }

    // Gets the images of the glyphs in a lookup, and if asked, keeps them for later renders, but those that are not
    // final.
    private void GetGlyphImages(
        ManagedFont font, float size, float density, bool snap, GlyphLookup lookup, int count, bool keep)
    {
        font.GetGlyphImages(
            lookup.Ids.AsSpan(0, count),
            lookup.Steps.AsSpan(0, count),
            size,
            density,
            0,
            snap,
            lookup.Images.AsSpan(0, count));
        if (!keep)
            return;

        for (var k = 0; k < count; k++)
        {
            ref readonly var img = ref lookup.Images[k];
            var slot = lookup.Slots[k];
            this.cachedImages[slot] = img;
            this.cachedSteps[slot] = img.IsFinal ? lookup.Steps[k] : NoCachedImage;
            if (!img.IsFinal || img.IsEmpty)
                continue;
            var plane = img.Kind == GlyphKind.Color ? GlyphKind.Color : GlyphKind.Grayscale;
            this.KeepSlice(plane, img.Slice, img.Generation);
            if (img.MaskSlice >= 0)
                this.KeepSlice(GlyphKind.Grayscale, img.MaskSlice, img.MaskGeneration);
        }
    }

    // Adds a slice kept images are in, once.
    private void KeepSlice(GlyphKind kind, int slice, int generation)
    {
        foreach (ref readonly var s in CollectionsMarshal.AsSpan(this.cachedSlices))
        {
            if (s.Kind == kind && s.Slice == slice && s.Generation == generation)
                return;
        }

        this.cachedSlices.Add((kind, slice, generation));
    }

    // The style to draw each run with, at the run index plus 1, and text outside runs at 0. The runs with the user
    // data of a highlight take its style, which only changes how they are drawn, as the layout is made.
    private void ResolveStyles(
        Span<ImTextStyle> styles, ImTextStyle? styleOverride, ulong highlightUserData, in ImTextStyle highlightStyle)
    {
        styles[0] = styleOverride ?? this.defaultStyle;
        for (var r = 0; r < this.runCount; r++)
        {
            var style = this.runs[r].Style ?? styles[0];
            styles[r + 1] = highlightUserData == 0 || this.runs[r].UserData != highlightUserData
                                ? style
                                : highlightStyle with
                                {
                                    Bold = style.Bold,
                                    Italic = style.Italic,
                                    Markup = style.Markup,
                                };
        }
    }

    // Draws the decorations of a layer, if the styles have any in it.
    private void DrawDecorationLayer(
        ImDrawList* dl, Vector2 pos, uint col, ReadOnlySpan<ImTextStyle> styles, ImTextTransform? transform,
        TextDecorationFlags layers, TextDecorationLayer layer)
    {
        if ((layers & LayerFlag(layer)) == 0)
            return;
        this.DrawDecorations(dl, pos, col, styles, transform, layer);
    }

    // Decorations of a layer, one span per adjacent items on a line with the same decoration and text color, so that
    // patterns run on across items. Only those for vertical text too (backgrounds) are drawn for it. The background
    // goes first, then the lines, then the extra decoration. A decoration drawn several times in a layer is drawn for
    // all spans before the next time.
    private void DrawDecorations(
        ImDrawList* dl, Vector2 pos, uint col, ReadOnlySpan<ImTextStyle> styles, ImTextTransform? transform,
        TextDecorationLayer layer)
    {
        var layerFlag = LayerFlag(layer);
        var spans = this.decorationSpans;
        spans.Clear();
        Span<ImTextDecoration> decorations = stackalloc ImTextDecoration[3];
        var bands = 1;
        for (var e = this.VisibleItems(); e.MoveNext();)
        {
            ref readonly var item = ref e.Current;
            if (item.Kind is ItemKind.Newline or ItemKind.Control || item.Offset < this.renderTextBegin ||
                item.Offset >= this.renderTextEnd)
            {
                continue;
            }

            ref readonly var st = ref styles[item.Run + 1];
            var textColor = st.Color != 0 ? MulAlpha(st.Color, col) : col;
            decorations[0] = st.Background.Alpha != 0 ? new(ImTextDecorationId.Background, st.Background) : default;
            decorations[1] = st.Lines.IsOn ? st.Lines.ToDecoration() : default;
            decorations[2] = st.ExtraDecoration;
            foreach (var d in decorations)
            {
                if (d.IsNone || !TextDecorationRegistry.TryGet(d.Id, out var entry) ||
                    (entry.LayersOf(d) & layerFlag) == 0 ||
                    (this.vertical && !entry.Vertical))
                {
                    continue;
                }

                var merged = false;
                for (var i = spans.Count - 1; i >= 0 && spans[i].Line == item.Line; i--)
                {
                    var s = spans[i];
                    if (s.Decoration == d && s.TextColor == textColor && Math.Abs(s.U1 - item.X) < 0.01f)
                    {
                        spans[i] = s with { U1 = item.X + item.Advance, ItemEnd = e.Index + 1 };
                        merged = true;
                        break;
                    }
                }

                if (!merged)
                {
                    spans.Add(
                        new(
                            d,
                            entry,
                            item.Line,
                            item.X,
                            item.X + item.Advance,
                            item.FontIndex,
                            item.Size,
                            textColor,
                            e.Index,
                            e.Index + 1));
                    bands = Math.Max(bands, entry.Bands);
                }
            }
        }

        // Over a span, as enumerating a list takes a try block.
        for (var band = 0; band < bands; band++)
        {
            foreach (ref readonly var s in CollectionsMarshal.AsSpan(spans))
            {
                if (band >= s.Entry.Bands)
                    continue;
                var line = this.lines[s.Line];
                var m = this.MetricsOf(s.FontIndex, s.Size);
                var r = this.RectToScreen(s.U0, line.BlockPos, s.U1, line.BlockPos + line.BlockSize);
                s.Entry.Draw(
                    new TextDecorationSpan(dl, pos, transform, col)
                    {
                        Layer = layer,
                        Band = band,
                        Layout = this,
                        ItemBegin = s.ItemBegin,
                        ItemEnd = s.ItemEnd,
                        Context = s.Decoration.Context,
                        Data = s.Decoration.Data,
                        TextColor = s.TextColor,
                        X0 = r.X,
                        X1 = r.Z,
                        LineTop = r.Y,
                        LineBottom = r.W,
                        LineStart = 0,
                        Baseline = line.BlockPos + line.Baseline,
                        FontSize = s.Size,
                        Ascent = m.Ascent,
                        Descent = m.Descent,
                        UnderlinePosition = m.UnderlinePosition,
                        UnderlineThickness = Math.Max(1f, m.UnderlineThickness),
                        StrikethroughPosition = m.StrikethroughPosition,
                        StrikethroughThickness = Math.Max(1f, m.StrikethroughThickness),
                    });
            }
        }
    }

    // The items that may show within the clip rectangle, as ranges for VisibleItems. Those are the lines across the
    // rectangle, and of each line, the items across it. The rectangle is widened by two of the largest ems for
    // overhangs, slants, edges and shadows. Lines go down and the items of a line right, so both are found by binary
    // search. With a transform, or for vertical text, all items are taken.
    private void FindVisibleItems(Vector2 pos, in Vector4 clip, bool cull)
    {
        var ranges = this.visibleRanges;
        ranges.Clear();
        if (!cull)
        {
            ranges.Add((0, this.items.Count));
            return;
        }

        if (this.largestItemSize < 0)
        {
            this.largestItemSize = this.desc.Size;
            foreach (ref readonly var item in CollectionsMarshal.AsSpan(this.items))
                this.largestItemSize = Math.Max(this.largestItemSize, item.Size);
        }

        var reach = this.largestItemSize * 2;
        var (x0, x1) = (clip.X - pos.X - reach, clip.Z - pos.X + reach);
        var itemSpan = CollectionsMarshal.AsSpan(this.items);
        var lineSpan = CollectionsMarshal.AsSpan(this.lines);
        var first = LowerBound(lineSpan, clip.Y - pos.Y - reach, static (in Line l) => l.BlockPos + l.BlockSize);
        for (var i = first; i < lineSpan.Length && pos.Y + lineSpan[i].BlockPos - reach <= clip.W; i++)
        {
            var lineItems = itemSpan[lineSpan[i].ItemBegin..lineSpan[i].ItemEnd];
            var begin = LowerBound(lineItems, x0, static (in Item it) => it.X + it.Advance);
            var end = begin + LowerBound(lineItems[begin..], x1, static (in Item it) => it.X, true);
            if (begin < end)
                ranges.Add((lineSpan[i].ItemBegin + begin, lineSpan[i].ItemBegin + end));
        }
    }

    private VisibleItemEnumerator VisibleItems() => new(CollectionsMarshal.AsSpan(this.items), this.visibleRanges);

    // A decoration over adjacent items of a line.
    private readonly record struct DecorationSpan(
        ImTextDecoration Decoration,
        TextDecorationRegistry.Entry Entry,
        int Line,
        float U0,
        float U1,
        int FontIndex,
        float Size,
        uint TextColor,
        int ItemBegin,
        int ItemEnd);

    // Writes quads into vertices and indices reserved a chunk at a time, as PrimReserve and PrimQuadUV are a native
    // call each per quad otherwise. Each chunk stays under the 65536 vertices of 16-bit indices, so that reserving the
    // next may start a new vertex offset. Nothing else may draw into the draw list before Finish. A chunk's indices
    // are cleared when it is reserved, so that if drawing throws before Finish, the rest are triangles of no area
    // rather than garbage. Hot paths are kept free of try blocks.
    private ref struct QuadWriter(ImDrawList* dl, int maxQuads)
    {
        private const int ChunkQuads = 4096;

        private int unreserved = maxQuads;
        private int reserved;

        // Quad corners in order, the UVs of the first and the third, and the color and extra data of all four.
        public void Add(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 uv0, Vector2 uv1, ImDrawVert vertex)
        {
            // More than counted is a bug, but drawing it is better than writing past the reservation.
            if (this.reserved == 0)
            {
                var n = Math.Max(Math.Min(this.unreserved, ChunkQuads), 1);
                ImGuiNative.PrimReserve(dl, n * 6, n * 4);
                new Span<ushort>(dl->IdxWritePtr, n * 6).Clear();
                this.unreserved -= n;
                this.reserved = n;
            }

            var vtx = dl->VtxWritePtr;
            var idx = dl->IdxWritePtr;
            var i = (ushort)dl->VtxCurrentIdx;
            idx[0] = i;
            idx[1] = (ushort)(i + 1);
            idx[2] = (ushort)(i + 2);
            idx[3] = i;
            idx[4] = (ushort)(i + 2);
            idx[5] = (ushort)(i + 3);
            vertex.Pos = a;
            vertex.Uv = uv0;
            vtx[0] = vertex;
            vertex.Pos = b;
            vertex.Uv = new(uv1.X, uv0.Y);
            vtx[1] = vertex;
            vertex.Pos = c;
            vertex.Uv = uv1;
            vtx[2] = vertex;
            vertex.Pos = d;
            vertex.Uv = new(uv0.X, uv1.Y);
            vtx[3] = vertex;
            dl->VtxWritePtr = vtx + 4;
            dl->IdxWritePtr = idx + 6;
            dl->VtxCurrentIdx += 4;
            this.reserved--;
        }

        // Gives back what was reserved and not written.
        public void Finish()
        {
            if (this.reserved > 0)
                ImGuiNative.PrimUnreserve(dl, this.reserved * 6, this.reserved * 4);
            this.reserved = 0;
        }
    }

    // The items of FindVisibleItems, by reference.
    private ref struct VisibleItemEnumerator(Span<Item> items, List<(int Begin, int End)> ranges)
    {
        private readonly Span<Item> items = items;
        private readonly List<(int Begin, int End)> ranges = ranges;
        private int range = -1;
        private int index;
        private int end;

        public readonly ref readonly Item Current => ref this.items[this.index];

        public readonly int Index => this.index;

        public readonly VisibleItemEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            this.index++;
            while (this.index >= this.end)
            {
                if (++this.range >= this.ranges.Count)
                    return false;
                (this.index, this.end) = this.ranges[this.range];
            }

            return true;
        }
    }

    // The glyphs of an item being drawn, shared by all layouts, as drawing is on the main thread only.
    private sealed class GlyphLookup
    {
        private static GlyphLookup shared = new(64);

        private GlyphLookup(int capacity)
        {
            this.Ids = new uint[capacity];
            this.Slots = new int[capacity];
            this.Steps = new byte[capacity];
            this.Pens = new Vector2[capacity];
            this.Images = new GlyphImage[capacity];
        }

        // The glyph ids.
        public uint[] Ids { get; }

        // Where the glyphs' images are kept in the layout.
        public int[] Slots { get; }

        // The subpixel steps, as ManagedFont.GetGlyphImages takes them.
        public byte[] Steps { get; }

        // The pens in device pixels.
        public Vector2[] Pens { get; }

        // The images.
        public GlyphImage[] Images { get; }

        // Gets the arrays, with room for a number of glyphs.
        public static GlyphLookup Get(int count)
        {
            if (shared.Ids.Length < count)
                shared = new(Math.Max(count, shared.Ids.Length * 2));
            return shared;
        }
    }
}
