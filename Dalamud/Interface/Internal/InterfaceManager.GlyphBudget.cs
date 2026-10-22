using System.Diagnostics;

using Dalamud.Interface.Fonts.Internal.Glyphs;

using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace Dalamud.Interface.Internal;

/// <summary>The time glyphs may be rasterized on the main thread for in a frame.</summary>
internal unsafe partial class InterfaceManager
{
    // At most a tenth of a refresh of the display, and at most 1ms, so that new text does not drop frames.
    private const int GlyphBudgetPartOfRefresh = 10;
    private static readonly TimeSpan MaxGlyphBudget = TimeSpan.FromMilliseconds(1);

    // When the budget was last set, or 0 if never.
    private long glyphBudgetUpdated;

    // From the refresh rate of the display the game is on, once a second.
    private void UpdateGlyphBudget()
    {
        var now = Stopwatch.GetTimestamp();
        if (this.glyphBudgetUpdated != 0 && now - this.glyphBudgetUpdated < Stopwatch.Frequency)
            return;
        this.glyphBudgetUpdated = now;

        var hz = 60u;
        var monitor = MonitorFromWindow(this.GameWindowHandle, MONITOR.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEXW { Base = { cbSize = (uint)sizeof(MONITORINFOEXW) } };
        if (GetMonitorInfoW(monitor, (MONITORINFO*)&info))
        {
            var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
            if (EnumDisplaySettingsW(&info.szDevice.e0, ENUM.ENUM_CURRENT_SETTINGS, &mode) &&
                mode.dmDisplayFrequency > 1)
            {
                hz = mode.dmDisplayFrequency;
            }
        }

        var budget = TimeSpan.FromSeconds(1.0 / hz / GlyphBudgetPartOfRefresh);
        GlyphRasterQueue.FrameBudget = budget < MaxGlyphBudget ? budget : MaxGlyphBudget;
    }
}
