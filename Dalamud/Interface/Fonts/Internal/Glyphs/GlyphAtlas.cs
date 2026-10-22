using System.Numerics;

using Dalamud.Bindings.ImGui;

using TerraFX.Interop.DirectX;

namespace Dalamud.Interface.Fonts.Internal.Glyphs;

/// <summary>Glyph slices shared by all <see cref="Sources.ManagedFont"/>s, drawn as two texture arrays.</summary>
/// <remarks><ol>
/// <li>The renderers bind both arrays for every draw. Glyph quads say where their glyph is in
/// <see cref="ImDrawVert.Ext"/>, so text never changes the draw command's texture.</li>
/// <li>The pixels are on the GPU only. Changes are queued, and made in order.</li>
/// <li>A retired slice keeps its pixels for a few frames, so that glyphs drawn from it in the meantime are copied back
/// instead of rasterized again.</li>
/// <li>Main thread only.</li>
/// </ol></remarks>
/// <seealso cref="Upload"/>
/// <seealso cref="TryRestore"/>
internal sealed unsafe partial class GlyphAtlas : IDisposable
{
    /// <summary>Width and height of a slice. <c>imgui-text.hlsli</c> has it as <c>EXT_SLICE_SIZE</c>.</summary>
    public const int SliceSize = 4096;

    /// <summary>The most slices a plane can have, as <c>imgui-text.hlsli</c> has 10 bits for the slice of a glyph.
    /// </summary>
    public const int SliceLimit = 1024;

    /// <summary>The <c>ext[0]</c> mode of <c>imgui-text.hlsli</c> of a grayscale glyph.</summary>
    public const uint ExtGlyph = 1;

    /// <summary>The <c>ext[0]</c> mode of a subpixel glyph.</summary>
    public const uint ExtSubpixel = 2;

    /// <summary>The <c>ext[0]</c> mode of a dilated glyph, for edges and shadows.</summary>
    public const uint ExtDilate = 3;

    /// <summary>The <c>ext[0]</c> mode of a color glyph.</summary>
    public const uint ExtColor = 4;

    /// <summary>The <c>ext[0]</c> flag of a dilated subpixel glyph.</summary>
    public const uint ExtRgb = 8;

    /// <summary>The <c>ext[0]</c> flag of a glyph in <see cref="Color"/>.</summary>
    public const uint ExtColorPlane = 16;

    /// <summary>The <c>ext[0]</c> flag of a grayscale glyph whose coverage is corrected as DirectWrite does.</summary>
    /// <seealso cref="GlyphImage.CorrectCoverage"/>
    public const uint ExtCorrect = 32;

    /// <summary>The most texels dilation reaches, as <c>EXT_MAX_REACH</c> of <c>imgui-text.hlsli</c>.</summary>
    public const int ExtMaxReach = 8;

    /// <summary>The default of <see cref="Plane.MaxSlices"/> of <see cref="Coverage"/>. It is 64 MiB on the GPU.
    /// </summary>
    public const int DefaultMaxCoverageSlices = 4;

    /// <summary>The default of <see cref="Plane.MaxSlices"/> of <see cref="Color"/>. It is 64 MiB on the GPU.</summary>
    public const int DefaultMaxColorSlices = 1;

    private static GlyphAtlas? shared;

    private long frame;

    private GlyphAtlas()
    {
    }

    /// <summary>Gets the atlas, creating it if needed.</summary>
    public static GlyphAtlas Shared => shared ??= new();

    /// <summary>Gets the plane of grayscale and subpixel glyphs, and of the masks of color glyphs.</summary>
    public Plane Coverage { get; } = new(PlaneKind.Coverage, DefaultMaxCoverageSlices);

    /// <summary>Gets the plane of color glyphs.</summary>
    public Plane Color { get; } = new(PlaneKind.Color, DefaultMaxColorSlices);

    /// <summary>Gets the number of glyphs in the atlas.</summary>
    public int GlyphCount => this.Coverage.GlyphCount + this.Color.GlyphCount;

    /// <summary>Gets the number of slices in use.</summary>
    public int SliceCount => this.Coverage.SliceCount + this.Color.SliceCount;

    /// <summary>Gets the number of times a slice was retired.</summary>
    public int EvictionCount => this.Coverage.EvictionCount + this.Color.EvictionCount;

    /// <summary>Gets the number of glyphs copied back from retired slices.</summary>
    public int RestoreCount { get; private set; }

    /// <summary>Makes <c>ext[0]</c> of <c>imgui-text.hlsli</c> for a glyph.</summary>
    /// <param name="mode">The mode, and flags other than <see cref="ExtColorPlane"/>.</param>
    /// <param name="kind">The kind of the glyph, which tells its plane.</param>
    /// <param name="slice">The slice of the glyph.</param>
    /// <returns>The value.</returns>
    public static uint ExtTag(uint mode, GlyphKind kind, int slice) =>
        mode | (kind == GlyphKind.Color ? ExtColorPlane : 0) | ((uint)slice << 6);

    /// <summary>Disposes <see cref="Shared"/>, if it exists.</summary>
    public static void DisposeShared()
    {
        shared?.Dispose();
        shared = null;
    }

    /// <summary>Draws a slice as it is now, through the text shader.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="plane">The plane.</param>
    /// <param name="slice">The slice.</param>
    /// <param name="p0">The top left.</param>
    /// <param name="p1">The bottom right.</param>
    /// <param name="color">The color.</param>
    public static void DrawSlice(
        ImDrawListPtr drawList, PlaneKind plane, int slice, Vector2 p0, Vector2 p1, uint color)
    {
        // A grayscale or color glyph whose box is the whole slice.
        var tag = plane == PlaneKind.Color
                      ? ExtTag(ExtColor, GlyphKind.Color, slice)
                      : ExtTag(ExtGlyph, GlyphKind.Grayscale, slice);
        var dl = drawList.Handle;
        ImGuiNative.PrimReserve(dl, 6, 4);
        ImGuiNative.PrimQuadUV(dl, p0, new(p1.X, p0.Y), p1, new(p0.X, p1.Y), default, Vector2.UnitX, Vector2.One, Vector2.UnitY, color);
        foreach (ref var v in new Span<ImDrawVert>(dl->VtxWritePtr - 4, 4))
        {
            v.Ext_0 = tag;
            v.Ext_1 = 0;
            v.Ext_2 = SliceSize | (SliceSize << 16);
            v.Ext_3 = 0;
            v.Ext_4 = 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Coverage.Dispose();
        this.Color.Dispose();
    }

    /// <summary>Makes the queued changes, and gets the texture arrays for the renderers to bind.</summary>
    /// <param name="device">The device.</param>
    /// <param name="context">The immediate context.</param>
    /// <returns>The arrays of <see cref="Coverage"/> and <see cref="Color"/>, either null if it has nothing yet.
    /// </returns>
    /// <remarks><ol>
    /// <li>They are bound at <c>t1</c> and <c>t2</c> of <c>imgui-text.hlsli</c>.</li>
    /// <li>The views are valid until the next call.</li>
    /// </ol></remarks>
    public (nint Coverage, nint Color) Upload(ID3D11Device* device, ID3D11DeviceContext* context)
    {
        // Without changes, flushing only gets the views, which is not counted as an upload.
        var changed = this.Coverage.HasChanges || this.Color.HasChanges;
        var start = changed ? FontProfiler.Begin(FontProfiler.Section.Upload) : 0;
        var bytes = 0L;
        var coverage = (nint)this.Coverage.Flush(device, context, ref bytes);
        var color = (nint)this.Color.Flush(device, context, ref bytes);
        if (changed)
            FontProfiler.End(FontProfiler.Section.Upload, start, bytes);
        return (coverage, color);
    }

    /// <summary>Counts a frame as drawn, for telling the slices used lately.</summary>
    /// <remarks>Unused slices beyond <see cref="Plane.MaxSlices"/> are retired, as no quads of theirs are waiting to be
    /// rendered.</remarks>
    public void EndFrame()
    {
        this.Coverage.EndFrame(this.frame);
        this.Color.EndFrame(this.frame);
        this.frame++;
    }

    /// <summary>Tests whether an image is still in the atlas.</summary>
    /// <param name="image">The image.</param>
    /// <returns>Whether it is valid. <see cref="CachedImage.Full"/> is not.</returns>
    public bool IsValid(in CachedImage image) =>
        (image.Slice < 0 ? image.Generation >= 0 : this.PlaneOf(image.Kind).IsCurrent(image.Slice, image.Generation))
        && (image.MaskSlice < 0 || this.Coverage.IsCurrent(image.MaskSlice, image.MaskGeneration));

    /// <summary>Packs a coverage bitmap.</summary>
    /// <param name="bitmap">The bitmap.</param>
    /// <returns>Where it is, <see cref="CachedImage.Empty"/> if the bitmap is empty or does not fit a slice, or
    /// <see cref="CachedImage.Full"/> if there is no room now.</returns>
    public CachedImage Add(in GlyphBitmap bitmap)
    {
        var kind = bitmap.Kind;
        var texelWidth = TexelWidthOf(kind, bitmap.Width);
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || texelWidth >= SliceSize / 2 || bitmap.Height >= SliceSize / 2)
            return CachedImage.Empty;

        // A subpixel bitmap is red, green and blue per pixel. It takes three texels per pixel in a row.
        var plane = this.PlaneOf(kind);
        ReadOnlySpan<byte> texels = kind switch
        {
            GlyphKind.Color => bitmap.Rgba!,
            GlyphKind.Subpixel => bitmap.Rgb!,
            _ => bitmap.Alpha,
        };
        if (!plane.TryPut(texelWidth, bitmap.Height, texels, this.frame, out var slice, out var x, out var y))
            return CachedImage.Full;

        var mask = (Slice: -1, X: 0, Y: 0, Generation: 0);
        if (bitmap.Mask is { } maskTexels)
        {
            if (!this.Coverage.TryPut(bitmap.Width, bitmap.Height, maskTexels, this.frame, out var ms, out var mx, out var my))
                return CachedImage.Full;
            mask = (ms, mx, my, this.Coverage.GenerationOf(ms));
        }

        plane.AddGlyph(slice);
        return new(
            slice,
            kind,
            x,
            y,
            bitmap.Width,
            bitmap.Height,
            bitmap.OffsetX,
            bitmap.OffsetY,
            mask.Slice,
            mask.X,
            mask.Y,
            plane.GenerationOf(slice),
            mask.Generation);
    }

    /// <summary>Copies the parts of an image whose slices were retired back into the atlas, if they still have their
    /// pixels.</summary>
    /// <param name="image">The image. Receives where it is now.</param>
    /// <returns>Whether it is valid now.</returns>
    public bool TryRestore(ref CachedImage image)
    {
        if (image.Slice < 0)
            return this.IsValid(image);

        var plane = this.PlaneOf(image.Kind);
        var imageCurrent = plane.IsCurrent(image.Slice, image.Generation);
        var maskCurrent = image.MaskSlice < 0 || this.Coverage.IsCurrent(image.MaskSlice, image.MaskGeneration);
        if (imageCurrent && maskCurrent)
            return true;
        if ((!imageCurrent && !plane.IsAvailable(image.Slice, image.Generation)) ||
            (!maskCurrent && !this.Coverage.IsAvailable(image.MaskSlice, image.MaskGeneration)))
        {
            return false;
        }

        var restored = image;
        if (!imageCurrent)
        {
            if (!plane.TryCopy(
                    image.Slice,
                    image.Generation,
                    image.X,
                    image.Y,
                    TexelWidthOf(image.Kind, image.Width),
                    image.Height,
                    this.frame,
                    out var slice,
                    out var x,
                    out var y))
            {
                return false;
            }

            plane.AddGlyph(slice);
            restored = restored with { Slice = slice, X = x, Y = y, Generation = plane.GenerationOf(slice) };
        }

        if (!maskCurrent)
        {
            if (!this.Coverage.TryCopy(
                    image.MaskSlice,
                    image.MaskGeneration,
                    image.MaskX,
                    image.MaskY,
                    image.Width,
                    image.Height,
                    this.frame,
                    out var slice,
                    out var x,
                    out var y))
            {
                return false;
            }

            restored = restored with
            {
                MaskSlice = slice, MaskX = x, MaskY = y, MaskGeneration = this.Coverage.GenerationOf(slice),
            };
        }

        this.RestoreCount++;
        image = restored;
        return true;
    }

    /// <summary>Makes the image of a cached bitmap, and marks its slices as used in this frame.</summary>
    /// <param name="image">The cached bitmap, which must be valid.</param>
    /// <param name="scale">Device pixels per bitmap pixel.</param>
    /// <param name="shift">Offset in device pixels.</param>
    /// <returns>The image.</returns>
    public GlyphImage ToImage(in CachedImage image, float scale, Vector2 shift)
    {
        // Nothing to draw, for good unless the atlas had no room.
        if (image.Slice < 0)
            return new() { IsFinal = image.Generation >= 0 };

        this.PlaneOf(image.Kind).MarkUsed(image.Slice, this.frame);
        if (image.MaskSlice >= 0)
            this.Coverage.MarkUsed(image.MaskSlice, this.frame);

        var texelWidth = TexelWidthOf(image.Kind, image.Width);
        return new()
        {
            Kind = image.Kind,
            Slice = image.Slice,
            Uv0 = new Vector2(image.X, image.Y) / SliceSize,
            Uv1 = new Vector2(image.X + texelWidth, image.Y + image.Height) / SliceSize,
            Rect = new(
                (image.OffsetX * scale) + shift.X,
                (image.OffsetY * scale) + shift.Y,
                ((image.OffsetX + image.Width) * scale) + shift.X,
                ((image.OffsetY + image.Height) * scale) + shift.Y),
            TexelMin = (uint)image.X | ((uint)image.Y << 16),
            TexelMax = (uint)(image.X + texelWidth) | ((uint)(image.Y + image.Height) << 16),
            MaskSlice = image.MaskSlice,
            MaskTexelMin = (uint)image.MaskX | ((uint)image.MaskY << 16),
            Generation = image.Generation,
            MaskGeneration = image.MaskGeneration,
            IsFinal = true,
        };
    }

    /// <summary>Tests whether a slice still holds what it held at a generation, and if so, counts it as used this
    /// frame, so that it is not retired.</summary>
    /// <param name="kind">The kind of glyph, which tells the plane.</param>
    /// <param name="slice">The slice.</param>
    /// <param name="generation">The generation.</param>
    /// <returns>Whether it does.</returns>
    /// <remarks>For keeping <see cref="GlyphImage"/>s across frames, rather than getting them again.</remarks>
    public bool TryUse(GlyphKind kind, int slice, int generation)
    {
        var plane = this.PlaneOf(kind);
        if (!plane.IsCurrent(slice, generation))
            return false;
        plane.MarkUsed(slice, this.frame);
        return true;
    }

    // The width of a glyph image in texels. Subpixel glyphs take three per pixel.
    private static int TexelWidthOf(GlyphKind kind, int width) => kind == GlyphKind.Subpixel ? width * 3 : width;

    private Plane PlaneOf(GlyphKind kind) => kind == GlyphKind.Color ? this.Color : this.Coverage;

    /// <summary>A glyph image in the atlas.</summary>
    /// <param name="Slice">Slice index in the plane of its kind, or -1 if there is nothing to draw.</param>
    /// <param name="Kind">How it stores its coverage, which tells its plane.</param>
    /// <param name="X">Left in the slice.</param>
    /// <param name="Y">Top in the slice.</param>
    /// <param name="Width">Width.</param>
    /// <param name="Height">Height.</param>
    /// <param name="OffsetX">Left relative to the pen, in bitmap pixels.</param>
    /// <param name="OffsetY">Top relative to the baseline, in bitmap pixels.</param>
    /// <param name="MaskSlice">Slice in <see cref="Coverage"/> of the text color mask of a color glyph, or -1 if none.
    /// </param>
    /// <param name="MaskX">Left of the mask in its slice.</param>
    /// <param name="MaskY">Top of the mask in its slice.</param>
    /// <param name="Generation">The generation of the slice when the image was added, or -1 for
    /// <see cref="Full"/>.</param>
    /// <param name="MaskGeneration">The generation of the slice of the mask when the image was added.</param>
    public readonly record struct CachedImage(
        int Slice,
        GlyphKind Kind,
        int X,
        int Y,
        int Width,
        int Height,
        int OffsetX,
        int OffsetY,
        int MaskSlice,
        int MaskX,
        int MaskY,
        int Generation,
        int MaskGeneration)
    {
        /// <summary>Gets an image with nothing to draw.</summary>
        public static CachedImage Empty => new(-1, GlyphKind.Grayscale, 0, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0);

        /// <summary>Gets an image with nothing to draw for now, as the atlas had no room.</summary>
        /// <remarks>It is not valid, so that the glyph is tried again.</remarks>
        public static CachedImage Full => Empty with { Generation = -1 };
    }
}
