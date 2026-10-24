using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Utility.Raii;

namespace Dalamud.Interface.Internal.Windows.Data.Widgets;

/// <summary>Shows the state of the font service.</summary>
internal class FontServiceWidget : IDataWindowWidget
{
    private static readonly string[] PlaneNames = ["Coverage", "Color"];

    private int plane;
    private float zoom = 0.125f;

    // For delta tracking.
    private (long Requests, long Misses, long Bytes) lastCounters;
    private (long Requests, long Misses, long Bytes) perFrame;
    private int lastCountersFrame = -1;

    /// <inheritdoc/>
    public string[]? CommandShortcuts { get; init; } = ["fontservice", "fonts"];

    /// <inheritdoc/>
    public string DisplayName { get; init; } = "Font: Service";

    /// <inheritdoc/>
    public bool Ready { get; set; }

    /// <inheritdoc/>
    public void Load() => this.Ready = true;

    /// <inheritdoc/>
    public void Draw()
    {
        if (ImGui.CollapsingHeader("Profile"u8, ImGuiTreeNodeFlags.DefaultOpen))
            DrawProfile();
        if (ImGui.CollapsingHeader("Fonts"u8, ImGuiTreeNodeFlags.DefaultOpen))
            DrawFonts();
        if (ImGui.CollapsingHeader("Layouts"u8, ImGuiTreeNodeFlags.DefaultOpen))
            this.DrawLayouts();
        if (ImGui.CollapsingHeader("Glyph atlas"u8, ImGuiTreeNodeFlags.DefaultOpen))
            this.DrawAtlas();
    }

    private static void DrawProfile()
    {
        ImGui.Text(
            $"Main thread only, over the last {FontProfiler.FrameCount} frames; " +
            $"a frame takes {FontProfiler.ToMs(FontProfiler.AverageFrameTicks()):0.00}ms on average. " +
            "Drawing text includes rasterizing and packing.");

        // The time glyphs may be rasterized on the main thread for in a frame, before the rest go to the thread pool.
        var budget = GlyphRasterQueue.FrameBudget;
        var overridden = GlyphRasterQueue.FrameBudgetOverride is not null;
        if (ImGui.Checkbox("Override the rasterizing budget"u8, ref overridden))
        {
            var start = budget == TimeSpan.MaxValue ? TimeSpan.FromMilliseconds(1) : budget;
            GlyphRasterQueue.FrameBudgetOverride = overridden ? start : null;
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!overridden))
        {
            var ms = budget == TimeSpan.MaxValue ? float.PositiveInfinity : (float)budget.TotalMilliseconds;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
            if (ImGui.SliderFloat("ms per frame (0 rasterizes nothing on the main thread)"u8, ref ms, 0, 16, "%.2f"))
                GlyphRasterQueue.FrameBudgetOverride = TimeSpan.FromMilliseconds(Math.Max(ms, 0));
        }

        ImGui.Text($"{GlyphRasterQueue.PendingCount} glyphs being rasterized or waiting to be packed");

        using var table = ImRaii.Table("##profile"u8, 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table)
            return;

        ImGui.TableSetupColumn("Section"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Last frame"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Average"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Worst frame"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        for (var section = default(FontProfiler.Section); section < FontProfiler.Section.Count; section++)
        {
            var (average, worst, worstFrameTicks) = FontProfiler.Summarize(section);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(
                section switch
                {
                    FontProfiler.Section.Render => "Drawing text",
                    FontProfiler.Section.RenderSetup => "- Setup",
                    FontProfiler.Section.RenderImages => "- Glyph images",
                    FontProfiler.Section.RenderDecorations => "- Decorations",
                    FontProfiler.Section.Layout => "Making layouts",
                    FontProfiler.Section.Rasterize => "Rasterizing",
                    FontProfiler.Section.Deferred => "Deferred",
                    FontProfiler.Section.Pack => "Packing",
                    FontProfiler.Section.Upload => "Uploading",
                    FontProfiler.Section.LayoutAnalyze => "- Analyzing",
                    FontProfiler.Section.LayoutShape => "- Shaping",
                    FontProfiler.Section.LayoutShapeByFonts => "  - By fonts",
                    FontProfiler.Section.LayoutFinish => "- Lines",
                    _ => section.ToString(),
                });
            ImGui.TableNextColumn();
            ImGui.Text(Describe(section, FontProfiler.Last(section)));
            ImGui.TableNextColumn();
            ImGui.Text(Describe(section, average));
            ImGui.TableNextColumn();
            ImGui.Text($"{Describe(section, worst)} of {FontProfiler.ToMs(worstFrameTicks):0.00}ms");
        }

        static string Describe(FontProfiler.Section section, FontProfiler.Stat s)
        {
            var text = $"{FontProfiler.ToMs(s.Ticks):0.000}ms, {s.Calls} ";
            return section switch
            {
                FontProfiler.Section.Render => text + $"calls, {s.Bytes} quads",
                FontProfiler.Section.RenderSetup or FontProfiler.Section.RenderDecorations => text + "calls",
                FontProfiler.Section.Layout => text + $"layouts, {s.Bytes / 1024f:0.#} KiB",
                FontProfiler.Section.LayoutAnalyze => text + $"layouts, {s.Bytes} bytes",
                FontProfiler.Section.LayoutShape or FontProfiler.Section.LayoutShapeByFonts or
                    FontProfiler.Section.LayoutFinish =>
                    text + $"layouts, {s.Bytes} codepoints",
                FontProfiler.Section.Upload => text + $"uploads, {s.Bytes / 1024f:0.#} KiB",
                _ => text + "glyphs",
            };
        }
    }

    private static void DrawFonts()
    {
        var resolver = Service<FontResolver>.Get();
        var entries = resolver.Snapshot();
        ImGui.Text($"{entries.Length} fonts; frame {resolver.Frame}");
        using var table = ImRaii.Table(
            "##fonts"u8,
            6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
            new(0, ImGui.GetTextLineHeightWithSpacing() * 16));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Spec"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("State"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("ImFont*"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Held"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Unused for"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Follows default"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();
        foreach (var e in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(e.Spec.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(e.State);
            ImGui.TableNextColumn();
            ImGui.Text(e.Pointer == 0 ? "-" : $"{e.Pointer:X}");
            ImGui.TableNextColumn();
            ImGui.Text(e.Held ? "Yes" : "No");
            ImGui.TableNextColumn();
            ImGui.Text($"{e.FramesSinceUse} frames");
            ImGui.TableNextColumn();
            ImGui.Text(e.FollowsDefault ? "Yes" : "No");
        }
    }

    private void DrawLayouts()
    {
        var now = (TextLayouter.Requests, TextLayouter.Misses, TextLayouter.RequestedBytes);
        var frame = ImGui.GetFrameCount();
        if (frame != this.lastCountersFrame)
        {
            if (this.lastCountersFrame >= 0)
            {
                this.perFrame = (
                    now.Requests - this.lastCounters.Requests,
                    now.Misses - this.lastCounters.Misses,
                    now.RequestedBytes - this.lastCounters.Bytes);
            }

            (this.lastCounters, this.lastCountersFrame) = (now, frame);
        }

        ImGui.Text($"{TextLayouter.CacheCount} cached, {TextLayouter.ReusedCount} of them used across frames");
        ImGui.Text(
            $"Last frame: {this.perFrame.Requests} asked for, {this.perFrame.Misses} made, " +
            $"{this.perFrame.Bytes / 1024f:0.#} KiB of text");
        ImGui.Text($"In all: {now.Requests} asked for, {now.Misses} made");
    }

    private void DrawAtlas()
    {
        var atlas = GlyphAtlas.Shared;
        ImGui.Text($"Slices of {GlyphAtlas.SliceSize}x{GlyphAtlas.SliceSize}; {atlas.RestoreCount} glyphs restored");
        foreach (var plane in (ReadOnlySpan<GlyphAtlas.Plane>)[atlas.Coverage, atlas.Color])
        {
            using var id = ImRaii.PushId((int)plane.Kind);
            ImGui.Text(
                $"{plane.Kind}: {plane.SliceCount} in use, {plane.RetiredCount} retired, {plane.SlotCount} slots; " +
                $"{plane.GlyphCount} glyphs; {plane.EvictionCount} evictions");
            ImGui.SameLine();
            var maxSlices = plane.MaxSlices;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
            if (ImGui.InputInt("Max slices"u8, ref maxSlices))
                plane.MaxSlices = Math.Max(1, maxSlices);
        }

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
        ImGui.Combo("Plane"u8, ref this.plane, PlaneNames, PlaneNames.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
        ImGui.SliderFloat("Zoom"u8, ref this.zoom, 0.03125f, 1f, "%.3fx");

        // As they are now, on black, through the text shader.
        var kind = (GlyphAtlas.PlaneKind)this.plane;
        var shown = kind == GlyphAtlas.PlaneKind.Color ? atlas.Color : atlas.Coverage;
        var size = new Vector2(GlyphAtlas.SliceSize * this.zoom);
        var drawList = ImGui.GetWindowDrawList();
        for (var i = 0; i < shown.SlotCount; i++)
        {
            ImGui.Text($"Slot {i}: {shown.StateOf(i)}");
            var p0 = ImGui.GetCursorScreenPos();
            var p1 = p0 + size;
            if (ImGui.IsRectVisible(p0, p1))
            {
                drawList.AddRectFilled(p0, p1, 0xFF000000);
                GlyphAtlas.DrawSlice(drawList, kind, i, p0, p1, 0xFFFFFFFF);
            }

            ImGui.Dummy(size);
        }
    }
}
