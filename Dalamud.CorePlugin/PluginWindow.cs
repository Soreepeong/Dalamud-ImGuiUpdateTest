#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Dalamud.CorePlugin
{
    /// <summary>
    /// Class responsible for drawing the main plugin window.
    /// </summary>
    internal class PluginWindow : Window, IDisposable
    {
        private readonly Random random = new();
        private readonly List<StringBuilder> texts = [];
        private readonly StringBuilder codepointList = new();
        private readonly StringBuilder row = new();
        private int[]? validCodepoints;
        private Dictionary<int, int[]>? blocks;
        private int countPerFrame = 64;
        private int passes = 1;
        private int runLength = 1;
        private bool paused;
        private bool showCodepoints;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginWindow"/> class.
        /// </summary>
        public PluginWindow()
            : base("CorePlugin")
        {
            this.IsOpen = true;

            this.Size = new Vector2(810, 520);
            this.SizeCondition = ImGuiCond.FirstUseEver;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
        }

        /// <inheritdoc/>
        public override void OnOpen()
        {
        }

        /// <inheritdoc/>
        public override void Draw()
        {
            // Assigned characters outside the private use areas, as .NET knows them. Controls, surrogates and
            // noncharacters are left out.
            this.validCodepoints ??= FindValidCodepoints();

            // The characters of each block of 128 codepoints, which are mostly of one script.
            this.blocks ??= this.validCodepoints.GroupBy(x => x >> 7).ToDictionary(x => x.Key, x => x.ToArray());

            ImGui.Checkbox("Pause", ref this.paused);
            ImGui.SameLine();
            ImGui.Checkbox("Show code points", ref this.showCodepoints);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
            ImGui.SliderInt("Per pass", ref this.countPerFrame, 1, 1024, "%d", ImGuiSliderFlags.Logarithmic);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
            ImGui.SliderInt("Passes", ref this.passes, 1, 64, "%d", ImGuiSliderFlags.Logarithmic);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
            ImGui.SliderInt("Run length", ref this.runLength, 1, 64, "%d", ImGuiSliderFlags.Logarithmic);

            var raster = FontProfiler.Last(FontProfiler.Section.Rasterize);
            var render = FontProfiler.Last(FontProfiler.Section.Render);
            var layout = FontProfiler.Last(FontProfiler.Section.Layout);
            ImGui.TextUnformatted(
                $"{this.validCodepoints.Length} characters to pick from. Last frame: made {layout.Calls} layouts in " +
                $"{FontProfiler.ToMs(layout.Ticks):0.000}ms; rasterized {raster.Calls} glyphs in " +
                $"{FontProfiler.ToMs(raster.Ticks):0.000}ms; drew text in {FontProfiler.ToMs(render.Ticks):0.000}ms.");
            ImGui.Separator();

            if (!this.paused || this.texts.Count != this.passes)
                this.Pick();

            // Each pass is a layout of its own, drawn over the others, so that none is culled.
            var start = ImGui.GetCursorPos();
            var end = start;
            foreach (var text in this.texts)
            {
                ImGui.SetCursorPos(start);
                ImGui.TextWrapped(text.ToString());
                end = Vector2.Max(end, ImGui.GetCursorPos());
            }

            // An item after moving the cursor, for the window to grow to it.
            ImGui.SetCursorPos(end);
            ImGui.Dummy(Vector2.Zero);
            if (this.showCodepoints)
            {
                ImGui.Separator();
                ImGui.TextWrapped(this.codepointList.ToString());
            }

            this.DrawAllCharacters();
        }

        // Every character picked from, in order, a row of CharactersPerRow at a time. Only the rows in view are drawn.
        private void DrawAllCharacters()
        {
            const int charactersPerRow = 32;
            if (!ImGui.CollapsingHeader("All characters"))
                return;

            var all = this.validCodepoints!;
            var rows = (all.Length + charactersPerRow - 1) / charactersPerRow;
            using var child = ImRaii.Child("##all", new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 24), true);
            if (!child)
                return;

            var clipper = ImGui.ImGuiListClipper();
            try
            {
                clipper.Begin(rows, ImGui.GetTextLineHeightWithSpacing());
                while (clipper.Step())
                {
                    for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                    {
                        this.row.Clear();
                        var first = row * charactersPerRow;
                        this.row.Append($"U+{all[first]:X5}  ");
                        for (var i = first; i < Math.Min(first + charactersPerRow, all.Length); i++)
                            this.row.Append(char.ConvertFromUtf32(all[i]));
                        ImGui.TextUnformatted(this.row.ToString());
                    }
                }

                clipper.End();
            }
            finally
            {
                clipper.Destroy();
            }
        }

        private static int[] FindValidCodepoints()
        {
            var list = new List<int>();
            for (var c = 0; c <= 0x10FFFF; c++)
            {
                // Noncharacters: U+FDD0..U+FDEF, and the last two of each plane.
                if (c is >= 0xFDD0 and <= 0xFDEF || (c & 0xFFFE) == 0xFFFE)
                    continue;

                switch (CharUnicodeInfo.GetUnicodeCategory(c))
                {
                    case UnicodeCategory.OtherNotAssigned:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.Control:
                        continue;
                    default:
                        list.Add(c);
                        break;
                }
            }

            return list.ToArray();
        }

        private void Pick()
        {
            while (this.texts.Count < this.passes)
                this.texts.Add(new());
            this.texts.RemoveRange(this.passes, this.texts.Count - this.passes);

            // The code points shown are those of the first pass.
            this.codepointList.Clear();
            // Runs of characters from the block of a random character, as text keeps to a script for a while.
            foreach (var text in this.texts)
            {
                text.Clear();
                for (var i = 0; i < this.countPerFrame;)
                {
                    var block = this.blocks![this.validCodepoints![this.random.Next(this.validCodepoints.Length)] >> 7];
                    for (var j = 0; j < this.runLength && i < this.countPerFrame; j++, i++)
                    {
                        var c = block[this.random.Next(block.Length)];
                        text.Append(char.ConvertFromUtf32(c));
                        if (this.showCodepoints && text == this.texts[0])
                            this.codepointList.Append($"U+{c:X4} ");
                    }
                }
            }
        }
    }
}
#endif
