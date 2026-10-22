using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace Dalamud.Interface.Fonts.Internal.Glyphs;

/// <summary>Subpixel text rendering, as the user and the ClearType settings of the monitors ask for.</summary>
/// <remarks>
/// <ol>
/// <li>Glyphs are rasterized per subpixel if the game's monitor shows subpixel text, in the order DirectWrite gives,
/// red first. Each viewport is drawn with the settings of its own monitor, which may reverse the order or show the
/// glyphs in grayscale.</li>
/// <li>Main thread only.</li>
/// </ol>
/// </remarks>
internal static unsafe class SubpixelSettings
{
    private const uint SpiGetFontSmoothing = 0x004A;
    private const uint SpiGetFontSmoothingType = 0x200A;
    private const uint FeFontSmoothingClearType = 2;
    private const uint DmdoDefault = 0;
    private const uint DmdoUpsideDown = 2;

    // DWrite_GetGammaRatios' table for gammas 1.0 to 2.2 in steps of 0.1, and its normalization.
    private static readonly Vector4 GammaRatioScale = new(65536f / 65025, 256f / 255, 65536f / 65025, 256f / 255);

    private static readonly Vector4[] GammaRatioTable =
    [
        new(0.0000f, 0.0000f, 0.0000f, 0.0000f),
        new(0.0166f, -0.0807f, 0.2227f, -0.0751f),
        new(0.0350f, -0.1760f, 0.4325f, -0.1370f),
        new(0.0543f, -0.2821f, 0.6302f, -0.1876f),
        new(0.0739f, -0.3963f, 0.8167f, -0.2287f),
        new(0.0933f, -0.5161f, 0.9926f, -0.2616f),
        new(0.1121f, -0.6395f, 1.1588f, -0.2877f),
        new(0.1300f, -0.7649f, 1.3159f, -0.3080f),
        new(0.1469f, -0.8911f, 1.4644f, -0.3234f),
        new(0.1627f, -1.0170f, 1.6051f, -0.3347f),
        new(0.1773f, -1.1420f, 1.7385f, -0.3426f),
        new(0.1908f, -1.2652f, 1.8650f, -0.3476f),
        new(0.2031f, -1.3864f, 1.9851f, -0.3501f),
    ];

    private static readonly Dictionary<nint, (long ReadTime, MonitorSettings Settings)> Monitors = [];

    static SubpixelSettings() => Refresh(default);

    /// <summary>Gets or sets a value indicating whether the user turned subpixel rendering on, as saved.</summary>
    public static bool Requested { get; set; }

    /// <summary>Gets or sets whether to use subpixel rendering instead of <see cref="Requested"/>, or null.</summary>
    /// <remarks>For previewing the setting.</remarks>
    public static bool? Override { get; set; }

    /// <summary>Gets a value indicating whether text is rasterized per subpixel.</summary>
    public static bool Enabled => Available && (Override ?? Requested);

    /// <summary>Gets a value indicating whether the monitor the game is on shows subpixel text.</summary>
    public static bool Available { get; private set; }

    /// <summary>Reads the ClearType settings of all monitors again.</summary>
    /// <param name="window">The game window, or default for the primary monitor.</param>
    public static void Refresh(HWND window)
    {
        Monitors.Clear();
        Available = ForWindow(window).Subpixel;
    }

    /// <summary>Gets how the monitor a window is on wants text drawn.</summary>
    /// <param name="window">The window, or default for the primary monitor.</param>
    /// <returns>The settings, read again once a second.</returns>
    public static MonitorSettings ForWindow(HWND window)
    {
        var monitor = MonitorFromWindow(window, MONITOR.MONITOR_DEFAULTTOPRIMARY);
        var now = Environment.TickCount64;
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(Monitors, (nint)monitor.Value, out var exists);
        if (!exists || now - entry.ReadTime >= 1000)
            entry = (now, Read(monitor));
        return entry.Settings;
    }

    private static MonitorSettings Read(HMONITOR monitor)
    {
        try
        {
            BOOL smoothing = false;
            uint type = 0;
            var clearType = SystemParametersInfoW(SpiGetFontSmoothing, 0, &smoothing, 0) && smoothing &&
                            SystemParametersInfoW(SpiGetFontSmoothingType, 0, &type, 0) &&
                            type == FeFontSmoothingClearType;

            using var rendering = default(ComPtr<IDWriteRenderingParams>);
            using var rendering1 = default(ComPtr<IDWriteRenderingParams1>);
            ((IDWriteFactory*)DirectWriteFont.Factory)->CreateMonitorRenderingParams(monitor, rendering.GetAddressOf())
                .ThrowOnError();
            var p = rendering.Get();
            var contrast = p->GetEnhancedContrast();
            var grayscaleContrast = rendering.As(&rendering1).SUCCEEDED
                                        ? rendering1.Get()->GetGrayscaleEnhancedContrast()
                                        : contrast;
            var level = p->GetClearTypeLevel();
            var geometry = p->GetPixelGeometry();

            // ClearType reads the geometry as if the monitor were not rotated. A quarter turn makes the stripes
            // vertical, which it cannot draw for, and half a turn reverses them.
            var orientation = OrientationOf(monitor);
            return new(
                GammaRatiosOf(p->GetGamma()),
                contrast,
                grayscaleContrast,
                level,
                clearType && level > 0 && geometry != DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_FLAT &&
                orientation is DmdoDefault or DmdoUpsideDown,
                (geometry == DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_BGR) != (orientation == DmdoUpsideDown));
        }
        catch
        {
            return new(GammaRatiosOf(1.8f), 0.5f, 1f, 1f, false, false);
        }
    }

    // DEVMODEW.dmDisplayOrientation, or DmdoDefault if unknown.
    private static uint OrientationOf(HMONITOR monitor)
    {
        var info = new MONITORINFOEXW { Base = { cbSize = (uint)sizeof(MONITORINFOEXW) } };
        var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
        return GetMonitorInfoW(monitor, (MONITORINFO*)&info) &&
               EnumDisplaySettingsW(&info.szDevice.e0, ENUM.ENUM_CURRENT_SETTINGS, &mode)
                   ? mode.dmDisplayOrientation
                   : DmdoDefault;
    }

    // DirectWrite takes the nearest row. This interpolates, so that gamma changes smoothly.
    private static Vector4 GammaRatiosOf(float gamma)
    {
        var position = (Math.Clamp(gamma, 1f, 2.2f) * 10) - 10;
        var row = Math.Min((int)position, GammaRatioTable.Length - 2);
        return Vector4.Lerp(GammaRatioTable[row], GammaRatioTable[row + 1], position - row) * GammaRatioScale;
    }

    /// <summary>How a monitor wants text drawn.</summary>
    /// <param name="GammaRatios">The alpha correction coefficients for the display gamma.</param>
    /// <param name="EnhancedContrast">The contrast added to subpixel coverage.</param>
    /// <param name="GrayscaleEnhancedContrast">The contrast added to grayscale coverage.</param>
    /// <param name="ClearTypeLevel">How much of the color fringes to keep, from 0 (grayscale) to 1.</param>
    /// <param name="Subpixel">Whether subpixel text can be shown.</param>
    /// <param name="Bgr">Whether the subpixels are in blue, green, red order, as seen.</param>
    public readonly record struct MonitorSettings(
        Vector4 GammaRatios,
        float EnhancedContrast,
        float GrayscaleEnhancedContrast,
        float ClearTypeLevel,
        bool Subpixel,
        bool Bgr);
}
