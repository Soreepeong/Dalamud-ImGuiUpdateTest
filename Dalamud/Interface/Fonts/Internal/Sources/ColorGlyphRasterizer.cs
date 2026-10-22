using System.Runtime.Versioning;
using System.Threading;

using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Utility;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>Draws color glyphs (COLR, SVG and bitmaps) with Direct2D.</summary>
/// <remarks>A glyph is drawn twice, in black and in white text, which splits it into what it draws itself and what it
/// draws in the text color. Layers above a text color layer then still cover it. Blend modes other than normal,
/// multiply and screen make the split approximate. Main thread only.</remarks>
internal sealed unsafe class ColorGlyphRasterizer : IDisposable
{
    /// <summary>The formats that make a glyph a color glyph.</summary>
    public const DWRITE_GLYPH_IMAGE_FORMATS ColorFormats =
        DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_COLR |
        DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_SVG |
        DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_PNG |
        DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_JPEG |
        DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_TIFF |
        DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_PREMULTIPLIED_B8G8R8A8;

    private const DWRITE_GLYPH_IMAGE_FORMATS OutlineFormats =
        DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_TRUETYPE |
        DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_CFF;

    // COLR version 1, with gradients. Asked for only where DirectWrite and Direct2D can draw it, from Windows 11.
    private const DWRITE_GLYPH_IMAGE_FORMATS PaintTreeFormat =
        DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_COLR_PAINT_TREE;

    private readonly Lock syncRoot = new();
    private readonly Func<ComPtr<IWICImagingFactory>> getWicFactory;
    private ComPtr<IWICImagingFactory> wicFactory;
    private ComPtr<ID2D1Factory> d2dFactory;

    // Set once paint trees are known to be drawable.
    private ComPtr<IDWriteFactory8> paintTreeFactory;
    private bool paintTreesProbed;
    private bool disposed;

    /// <summary>Initializes a new instance of the <see cref="ColorGlyphRasterizer"/> class.</summary>
    /// <param name="getWicFactory">Gets a reference to a WIC factory, when the first color glyph is drawn.</param>
    public ColorGlyphRasterizer(Func<ComPtr<IWICImagingFactory>> getWicFactory) => this.getWicFactory = getWicFactory;

    /// <summary>Tests whether a font has color glyphs.</summary>
    /// <param name="face">The font face.</param>
    /// <returns>Whether it has a color format.</returns>
    public static bool HasColorGlyphs(IDWriteFontFace* face)
    {
        using var face4 = default(ComPtr<IDWriteFontFace4>);
        fixed (Guid* iid = &IID.IID_IDWriteFontFace4)
        {
            return face->QueryInterface(iid, (void**)face4.GetAddressOf()).SUCCEEDED &&
                   (face4.Get()->GetGlyphImageFormats() & (ColorFormats | PaintTreeFormat)) != 0;
        }
    }

    /// <summary>Draws a glyph, if it is a color glyph.</summary>
    /// <param name="face">The font face.</param>
    /// <param name="glyph">The glyph.</param>
    /// <param name="emSize">The em size in pixels.</param>
    /// <param name="subpixelX">Fraction of a pixel the pen is right of a whole pixel.</param>
    /// <param name="slant">Horizontal shift per pixel above the baseline to slant the glyph by.</param>
    /// <param name="canvas">The area to draw in, relative to the pen on the baseline. Ink outside is lost.</param>
    /// <param name="bitmap">The glyph, trimmed to its ink.</param>
    /// <returns>Whether it is a color glyph, and was drawn with ink. False once disposed.</returns>
    public bool TryRasterize(
        IDWriteFontFace* face, ushort glyph, float emSize, float subpixelX, float slant, RECT canvas,
        out GlyphBitmap bitmap)
    {
        bitmap = default;
        var w = canvas.right - canvas.left;
        var h = canvas.bottom - canvas.top;
        if (w <= 0 || h <= 0)
            return false;

        // The Direct2D factory is single-threaded, and glyphs are rasterized on the thread pool too.
        byte[]? black, white;
        lock (this.syncRoot)
        {
            black = this.Render(face, glyph, emSize, subpixelX, slant, canvas, 0f);
            white = black is null ? null : this.Render(face, glyph, emSize, subpixelX, slant, canvas, 1f);
        }

        if (black is null || white is null)
            return false;

        // Black text gives what the glyph draws itself. White text adds the text color's share, the same in every
        // channel, so green stands for it.
        var hasMask = false;
        int x0 = w, y0 = h, x1 = 0, y1 = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = ((y * w) + x) * 4;
                var mask = white[i + 1] - black[i + 1];
                hasMask |= mask > 0;
                if (black[i + 3] != 0 || mask > 0)
                    (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x + 1), Math.Max(y1, y + 1));
            }
        }

        if (x0 >= x1 || y0 >= y1)
            return false;

        var tw = x1 - x0;
        var th = y1 - y0;
        var rgba = new byte[tw * th * 4];
        var alpha = new byte[tw * th];
        var maskPixels = hasMask ? new byte[tw * th] : null;
        for (var y = 0; y < th; y++)
        {
            for (var x = 0; x < tw; x++)
            {
                var i = (((y + y0) * w) + x + x0) * 4;
                var o = (y * tw) + x;
                rgba[(o * 4) + 0] = black[i + 2];
                rgba[(o * 4) + 1] = black[i + 1];
                rgba[(o * 4) + 2] = black[i + 0];
                rgba[(o * 4) + 3] = black[i + 3];
                alpha[o] = black[i + 3];
                if (maskPixels is not null)
                    maskPixels[o] = (byte)Math.Max(0, white[i + 1] - black[i + 1]);
            }
        }

        bitmap = new()
        {
            Width = tw,
            Height = th,
            OffsetX = canvas.left + x0,
            OffsetY = canvas.top + y0,
            Alpha = alpha,
            Rgba = rgba,
            Mask = maskPixels,
        };
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (this.syncRoot)
        {
            this.disposed = true;
            this.wicFactory.Reset();
            this.d2dFactory.Reset();
            this.paintTreeFactory.Reset();
        }
    }

    [SupportedOSPlatform("windows10.0")]
    private static void DrawLayer(
        ID2D1DeviceContext4* dc, ID2D1DeviceContext7* paintTreeContext, DWRITE_COLOR_GLYPH_RUN1* layer,
        ID2D1Brush* textBrush, ID2D1SolidColorBrush* layerBrush)
    {
        var b = &layer->Base;
        var origin = new D2D_POINT_2F { x = b->baselineOriginX, y = b->baselineOriginY };
        switch (layer->glyphImageFormat)
        {
            // Translated so only when there is a paintTreeContext.
            case PaintTreeFormat:
                paintTreeContext->DrawPaintGlyphRun(origin, &b->glyphRun, textBrush, 0, layer->measuringMode);
                break;

            case DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_SVG:
                dc->DrawSvgGlyphRun(origin, &b->glyphRun, textBrush, null, 0, layer->measuringMode);
                break;

            case DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_PNG:
            case DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_JPEG:
            case DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_TIFF:
            case DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_PREMULTIPLIED_B8G8R8A8:
                dc->DrawColorBitmapGlyphRun(layer->glyphImageFormat, origin, &b->glyphRun, layer->measuringMode);
                break;

            default:
                // COLR layers, and outlines. Palette index 0xFFFF is the text color.
                ID2D1Brush* brush;
                if (b->paletteIndex == 0xFFFF)
                {
                    brush = textBrush;
                }
                else
                {
                    var color = b->runColor;
                    layerBrush->SetColor(&color);
                    brush = (ID2D1Brush*)layerBrush;
                }

                dc->DrawGlyphRun(origin, &b->glyphRun, brush, layer->measuringMode);
                break;
        }
    }

    private bool EnsureFactories()
    {
        if (this.disposed)
            return false;

        if (this.wicFactory.IsEmpty())
            this.wicFactory = this.getWicFactory();

        if (this.d2dFactory.IsEmpty())
        {
            var options = new D2D1_FACTORY_OPTIONS { debugLevel = D2D1_DEBUG_LEVEL.D2D1_DEBUG_LEVEL_NONE };
            fixed (Guid* iid = &IID.IID_ID2D1Factory)
            {
                DirectX.D2D1CreateFactory(
                    D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED,
                    iid,
                    &options,
                    (void**)this.d2dFactory.GetAddressOf()).ThrowOnError();
            }
        }

        if (!this.paintTreesProbed)
        {
            this.paintTreesProbed = true;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                _ = ((IUnknown*)DirectWriteFont.Factory)->QueryInterface(
                    __uuidof<IDWriteFactory8>(),
                    (void**)this.paintTreeFactory.GetAddressOf());
            }
        }

        return true;
    }

    // Draws the glyph's color layers with the text in a gray level, and returns premultiplied BGRA. Null if it is not a
    // color glyph.
    private byte[]? Render(
        IDWriteFontFace* face, ushort glyph, float emSize, float subpixelX, float slant, RECT canvas, float text)
    {
        // The device context drawn with is on Windows 10 and later.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return null;

        // DirectWrite tells color glyphs apart here. GetGlyphImageFormats reports none for COLR glyphs.
        var advance = 0f;
        var run = new DWRITE_GLYPH_RUN
        {
            fontFace = face,
            fontEmSize = emSize,
            glyphCount = 1,
            glyphIndices = &glyph,
            glyphAdvances = &advance,
        };
        var origin = new D2D_POINT_2F { x = subpixelX - canvas.left, y = -canvas.top };
        if (!this.EnsureFactories())
            return null;

        using var layers = default(ComPtr<IDWriteColorGlyphRunEnumerator1>);
        var paintTrees = !this.paintTreeFactory.IsEmpty();
        var translated = paintTrees
                             ? this.paintTreeFactory.Get()->TranslateColorGlyphRun(
                                 origin,
                                 &run,
                                 null,
                                 ColorFormats | OutlineFormats | PaintTreeFormat,
                                 DWRITE_PAINT_FEATURE_LEVEL.DWRITE_PAINT_FEATURE_LEVEL_COLR_V1,
                                 DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL,
                                 null,
                                 0,
                                 layers.GetAddressOf())
                             : ((IDWriteFactory4*)DirectWriteFont.Factory)->TranslateColorGlyphRun(
                                 origin,
                                 &run,
                                 null,
                                 ColorFormats | OutlineFormats,
                                 DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL,
                                 null,
                                 0,
                                 layers.GetAddressOf());
        if (translated.FAILED)
            return null;

        var w = canvas.right - canvas.left;
        var h = canvas.bottom - canvas.top;
        using var wicBitmap = default(ComPtr<IWICBitmap>);
        var format = GUID.GUID_WICPixelFormat32bppPBGRA;
        if (this.wicFactory.Get()->CreateBitmap(
                (uint)w,
                (uint)h,
                &format,
                WICBitmapCreateCacheOption.WICBitmapCacheOnLoad,
                wicBitmap.GetAddressOf()).FAILED)
        {
            return null;
        }

        var props = new D2D1_RENDER_TARGET_PROPERTIES
        {
            type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_DEFAULT,
            pixelFormat = new()
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX = 96,
            dpiY = 96,
        };
        using var target = default(ComPtr<ID2D1RenderTarget>);
        using var context = default(ComPtr<ID2D1DeviceContext4>);
        using var paintTreeContext = default(ComPtr<ID2D1DeviceContext7>);
        if (this.d2dFactory.Get()->CreateWicBitmapRenderTarget(
                wicBitmap.Get(),
                &props,
                target.GetAddressOf()).FAILED ||
            target.As(&context).FAILED)
        {
            return null;
        }

        // DirectWrite drawing paint trees without Direct2D doing so too is not expected, but then they are not asked for.
        if (paintTrees && target.As(&paintTreeContext).FAILED)
        {
            this.paintTreeFactory.Reset();
            return this.Render(face, glyph, emSize, subpixelX, slant, canvas, text);
        }

        var dc = context.Get();
        dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);

        // Slanted around the baseline (y is down).
        var shear = new D2D_MATRIX_3X2_F { _11 = 1, _21 = -slant, _22 = 1, _31 = slant * origin.y };
        dc->SetTransform(&shear);
        var textColor = new DXGI_RGBA { r = text, g = text, b = text, a = 1 };
        using var textBrush = default(ComPtr<ID2D1SolidColorBrush>);
        using var layerBrush = default(ComPtr<ID2D1SolidColorBrush>);
        dc->CreateSolidColorBrush(&textColor, textBrush.GetAddressOf()).ThrowOnError();
        dc->CreateSolidColorBrush(&textColor, layerBrush.GetAddressOf()).ThrowOnError();

        var transparent = default(DXGI_RGBA);
        dc->BeginDraw();
        dc->Clear(&transparent);
        while (true)
        {
            BOOL hasRun;
            if (layers.Get()->MoveNext(&hasRun).FAILED || !hasRun)
                break;
            DWRITE_COLOR_GLYPH_RUN1* layer;
            if (layers.Get()->GetCurrentRun(&layer).FAILED)
                break;
            DrawLayer(dc, paintTreeContext.Get(), layer, (ID2D1Brush*)textBrush.Get(), layerBrush.Get());
        }

        if (dc->EndDraw(null, null).FAILED)
            return null;

        var pixels = new byte[w * h * 4];
        fixed (byte* p = pixels)
        {
            if (((IWICBitmapSource*)wicBitmap.Get())->CopyPixels(null, (uint)w * 4, (uint)pixels.Length, p).FAILED)
                return null;
        }

        return pixels;
    }
}
