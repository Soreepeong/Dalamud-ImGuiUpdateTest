using System.Collections.Generic;
using System.Numerics;

using BitFaster.Caching.Lru;

using Dalamud.Bindings.ImGui;
using Dalamud.Data;
using Dalamud.Interface.Fonts.Internal.Formats;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Interface.Utility;
using Dalamud.Utility;

using Lumina.Excel.Sheets;
using Lumina.Text.Parse;
using Lumina.Text.ReadOnly;

namespace Dalamud.Interface.Fonts.Internal.SeStrings;

/// <summary>Loads what SeStrings need, and lays out text in fonts with <see cref="ImTextMarkup.SeString"/> and
/// <see cref="ImTextMarkup.MacroString"/>.</summary>
[ServiceManager.EarlyLoadedService]
internal sealed class SeStringLayouter : IInternalDisposableService
{
    /// <summary>Cache of compiled SeStrings from <see cref="CompileAndCache"/>.</summary>
    private readonly ConcurrentLru<string, ReadOnlySeString> cache = [with(1024)];

    private readonly SeStringStyler seString;
    private readonly MacroStringStyler macroString;

    [ServiceManager.ServiceConstructor]
    private SeStringLayouter(DataManager dm)
    {
        var gfd = new GfdFileView(dm.GetFile("common/font/gfdata.gfd")!.Data);
        var colorTypes = SeStringColorStackSet.ColorTypesOf(dm.Excel.GetSheet<UIColor>());
        this.seString = new(gfd, colorTypes);
        this.macroString = new(gfd, colorTypes);
        TextStylers.Register(this.seString);
        TextStylers.Register(this.macroString);
    }

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        TextStylers.Unregister(this.seString);
        TextStylers.Unregister(this.macroString);
    }

    /// <summary>Compiles and caches a SeString from a text macro representation.</summary>
    /// <param name="text">SeString text macro representation. Newline characters will be normalized to newline
    /// payloads.</param>
    /// <returns>Compiled SeString.</returns>
    public ReadOnlySeString CompileAndCache(string text) =>
        this.cache.GetOrAdd(
            text,
            static text => ReadOnlySeString.FromMacroString(
                text.ReplaceLineEndings("<br>"),
                new() { ExceptionMode = MacroStringParseExceptionMode.EmbedError }));

    /// <summary>Creates a draw data with a SeString drawn onto it.</summary>
    /// <param name="text">The SeString.</param>
    /// <param name="font">The font, whose style the text starts with.</param>
    /// <param name="fontSize">The font size in pixels.</param>
    /// <param name="wrapWidth">The width to wrap at.</param>
    /// <returns>A new self-contained draw data.</returns>
    public unsafe BufferBackedImDrawData CreateDrawData(
        ReadOnlySpan<byte> text, ImFontPtr font, float fontSize, float wrapWidth)
    {
        if (ManagedFont.FromNative(font) is not { } managed)
            throw new ArgumentException("The font is not a Dalamud font.", nameof(font));
        ImGuiP.GetTextDesc(out var desc);
        var style = font.Style with { Markup = ImTextMarkup.SeString };
        var layout = this.seString.CreateLayout(
                         managed,
                         new(fontSize, desc.Density, WrapWidth: wrapWidth >= float.MaxValue ? 0 : wrapWidth),
                         style,
                         text)
                     ?? throw new InvalidOperationException("SeStrings can only be laid out on the main thread.");

        var dd = BufferBackedImDrawData.Create();
        try
        {
            layout.Render(dd.ListPtr, Vector2.Zero, uint.MaxValue);
            var offset = Vector2.Zero;
            foreach (var vtx in new Span<ImDrawVert>(dd.ListPtr.VtxBuffer.Data, dd.ListPtr.VtxBuffer.Size))
                offset = Vector2.Min(offset, vtx.Pos);
            dd.Data.DisplayPos = offset;
            dd.Data.DisplaySize = layout.Size - offset;
            dd.Data.Valid = 1;
            dd.UpdateDrawDataStatistics();
            return dd;
        }
        catch
        {
            dd.Dispose();
            throw;
        }
    }
}
