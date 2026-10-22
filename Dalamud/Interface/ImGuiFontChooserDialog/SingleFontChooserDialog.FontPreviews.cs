using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts;

namespace Dalamud.Interface.ImGuiFontChooserDialog;

/// <summary>
/// A dialog for choosing a font and its size.
/// </summary>
public sealed partial class SingleFontChooserDialog
{
    private sealed class FontPreviews
    {
        private const int SwitchesPerFrame = 4;
        private const int KeepFrames = 600;

        private readonly Dictionary<IFontId, Entry> entries = [];
        private int frame = -1;
        private int switchesLeft;

        public bool Selectable(IFontId? id, string name, ref bool selected)
        {
            ImGui.PushID(name);
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

        public void Clear() => this.entries.Clear();

        private ImFontPtr GetFont(IFontId id)
        {
            var now = ImGui.GetFrameCount();
            if (now != this.frame)
            {
                this.frame = now;
                this.switchesLeft = SwitchesPerFrame;
                if (now % 60 == 0)
                    this.Sweep(now);
            }

            if (id is SystemFontId { IsSymbolFont: true }
                or GameFontAndFamilyId
                {
                    GameFontFamily: GameFontFamily.Meidinger or GameFontFamily.MiedingerMid
                    or GameFontFamily.JupiterNumeric,
                })
            {
                return ImFontPtr.Null;
            }

            if (!this.entries.TryGetValue(id, out var entry))
            {
                var spec = new MultiFontSpec { Parts = [new SingleFontSpec { FontId = id, SizePx = 0 }] };
                this.entries[id] = entry = new(spec.GetFontAsync());
            }

            entry.LastUsedFrame = now;
            if (!entry.Load.IsCompletedSuccessfully || entry.Load.Result.Exception is not null)
                return ImFontPtr.Null;
            if (!entry.Shown)
            {
                if (this.switchesLeft == 0)
                    return ImFontPtr.Null;
                this.switchesLeft--;
                entry.Shown = true;
            }

            return entry.Load.Result;
        }

        private void Sweep(int now)
        {
            foreach (var id in this.entries.Where(x => now - x.Value.LastUsedFrame > KeepFrames).Select(x => x.Key)
                                   .ToList())
                this.entries.Remove(id);
        }

        private sealed class Entry(Task<ImFontRef> load)
        {
            public Task<ImFontRef> Load { get; } = load;

            public int LastUsedFrame { get; set; }

            public bool Shown { get; set; }
        }
    }
}
