using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts;

namespace Dalamud.Interface.ImGuiFontChooserDialog;

/// <summary>
/// A dialog for choosing a font and its size.
/// </summary>
public sealed partial class SingleFontChooserDialog
{
    /// <summary>Draws the names of fonts in their own fonts, loading the fonts as they scroll into view.</summary>
    private sealed class FontPreviews
    {
        // Fonts that may switch from the list's font to their own in a frame, so that a scroll does not change many.
        private const int SwitchesPerFrame = 4;

        // Frames a font is kept after its last use.
        private const int KeepFrames = 600;

        // Frames between looks for fonts to drop.
        private const int SweepFrames = 60;

        // Loads started and not done, at most. Scrolling through the list would start one for every family otherwise.
        private const int MaxLoads = 4;

        private readonly Dictionary<IFontId, Entry> entries = [];
        private readonly List<Entry> loading = [];
        private int frame = -1;
        private int lastSweepFrame;
        private int switchesLeft;

        /// <summary>Draws a row.</summary>
        /// <param name="row">The row index, to tell rows of the same name apart.</param>
        /// <param name="id">The font to draw the name in, or <see langword="null"/> for the list's font.</param>
        /// <param name="name">The name.</param>
        /// <param name="selected">Whether the row is selected.</param>
        /// <returns>Whether it was clicked.</returns>
        public bool Selectable(int row, IFontId? id, string name, ref bool selected)
        {
            ImGui.PushID(row);
            var clicked = ImGui.Selectable(
                "##preview"u8,
                ref selected,
                ImGuiSelectableFlags.NoAutoClosePopups,
                new(0, ImGui.GetTextLineHeight()));
            ImGui.PopID();

            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            if (!ImGui.IsRectVisible(min, max))
                return clicked;

            var drawList = ImGui.GetWindowDrawList();
            var font = id is null ? ImFontPtr.Null : this.GetFont(id);

            var spacing = ImGui.GetStyle().ItemSpacing;
            drawList.PushClipRect(min, max, true);
            drawList.AddText(
                font.IsNull ? ImGui.Font : font,
                ImGui.GetFontSize(),
                min + new Vector2(MathF.Floor(spacing.X * 0.5f), MathF.Floor(spacing.Y * 0.5f)),
                ImGui.GetColorU32(ImGuiCol.Text),
                name);
            drawList.PopClipRect();
            return clicked;
        }

        /// <summary>Forgets all the fonts.</summary>
        public void Clear()
        {
            this.entries.Clear();
            this.loading.Clear();
        }

        private ImFontPtr GetFont(IFontId id)
        {
            var now = ImGui.GetFrameCount();
            if (now != this.frame)
            {
                this.frame = now;
                this.switchesLeft = SwitchesPerFrame;
                for (var i = this.loading.Count - 1; i >= 0; i--)
                {
                    if (this.loading[i].Font.LoadTask.IsCompleted)
                        this.loading.RemoveAt(i);
                }

                if (now - this.lastSweepFrame >= SweepFrames)
                    this.Sweep(now);
            }

            if (!id.PreviewsName)
                return ImFontPtr.Null;

            if (!this.entries.TryGetValue(id, out var entry))
            {
                if (this.loading.Count >= MaxLoads)
                    return ImFontPtr.Null;
                var spec = FontSpec.From(id);
                this.entries[id] = entry = new(spec.GetFont());
                if (!entry.Font.LoadTask.IsCompleted)
                    this.loading.Add(entry);
            }

            entry.LastUsedFrame = now;
            if (!entry.Font.LoadTask.IsCompletedSuccessfully)
                return ImFontPtr.Null;
            if (!entry.Shown)
            {
                if (this.switchesLeft == 0)
                    return ImFontPtr.Null;
                this.switchesLeft--;
                entry.Shown = true;
            }

            return entry.Font;
        }

        private void Sweep(int now)
        {
            this.lastSweepFrame = now;
            foreach (var (id, entry) in this.entries)
            {
                if (now - entry.LastUsedFrame > KeepFrames)
                    this.entries.Remove(id);
            }
        }

        private sealed class Entry(ImFontRef font)
        {
            public ImFontRef Font { get; } = font;

            public int LastUsedFrame { get; set; }

            public bool Shown { get; set; }
        }
    }
}
