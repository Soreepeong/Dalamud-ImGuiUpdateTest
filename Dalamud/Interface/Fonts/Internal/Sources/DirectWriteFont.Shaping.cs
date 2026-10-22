using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using Dalamud.Utility;

using TerraFX.Interop.DirectX;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>Shaping, with DirectWrite's text analyzer.</summary>
internal sealed unsafe partial class DirectWriteFont
{
    private static readonly Lazy<nint> AnalyzerLazy = new(CreateAnalyzer, LazyThreadSafetyMode.ExecutionAndPublication);

    private int[] unitCodepointBuffer = new int[64];

    // Shaping buffers, pinned for RentPinned.
    private char[] unitBuffer = GC.AllocateUninitializedArray<char>(64, pinned: true);

    private DirectWriteScriptSink.ScriptRun[] scriptBuffer =
        GC.AllocateUninitializedArray<DirectWriteScriptSink.ScriptRun>(16, pinned: true);

    private ushort[] clusterMapBuffer = GC.AllocateUninitializedArray<ushort>(64, pinned: true);

    private DWRITE_SHAPING_TEXT_PROPERTIES[] textPropsBuffer =
        GC.AllocateUninitializedArray<DWRITE_SHAPING_TEXT_PROPERTIES>(64, pinned: true);

    private ushort[] shapeIndexBuffer = GC.AllocateUninitializedArray<ushort>(64, pinned: true);

    private DWRITE_SHAPING_GLYPH_PROPERTIES[] glyphPropsBuffer =
        GC.AllocateUninitializedArray<DWRITE_SHAPING_GLYPH_PROPERTIES>(64, pinned: true);

    private float[] advanceBuffer = GC.AllocateUninitializedArray<float>(64, pinned: true);

    private DWRITE_GLYPH_OFFSET[] offsetBuffer = GC.AllocateUninitializedArray<DWRITE_GLYPH_OFFSET>(64, pinned: true);

    private static IDWriteTextAnalyzer* Analyzer => (IDWriteTextAnalyzer*)AnalyzerLazy.Value;

    /// <inheritdoc/>
    /// <remarks>Uses <see cref="IDWriteTextAnalyzer"/> per script, with the font's default features. DirectWrite
    /// does not take context, so the text around the run is not looked at.</remarks>
    public override void Shape(ShapeRun run, ShapedGlyphList output)
    {
        var count = run.End - run.Begin;
        if (count <= 0)
            return;

        // UTF-16, with the codepoint index of each code unit.
        var units = RentPinned(ref this.unitBuffer, count * 2);
        var unitCodepoints = Rent(ref this.unitCodepointBuffer, count * 2);
        var n = 0;
        for (var i = run.Begin; i < run.End; i++)
        {
            if (!Rune.TryCreate(run.Text[i], out var rune))
                rune = Rune.ReplacementChar;
            var length = rune.EncodeToUtf16(new(units + n, 2));
            unitCodepoints.Slice(n, length).Fill(i);
            n += length;
        }

        var scripts = RentPinned(ref this.scriptBuffer, n);
        var source = new DirectWriteTextSource(units, (uint)n, EmptyLocale);
        var sink = new DirectWriteScriptSink(scripts, n);
        Analyzer->AnalyzeScript((IDWriteTextAnalysisSource*)&source, 0, (uint)n, (IDWriteTextAnalysisSink*)&sink)
            .ThrowOnError();
        var scriptCount = sink.Count;
        if (scriptCount == 0)
            scripts[scriptCount++] = new() { Position = 0, Length = (uint)n };
        new Span<DirectWriteScriptSink.ScriptRun>(scripts, scriptCount)
            .Sort((a, b) => a.Position.CompareTo(b.Position));

        var emSize = this.Scale(run.Size) * this.unitsPerEm;
        for (var s = 0; s < scriptCount; s++)
        {
            var script = scripts[s];
            var (position, length) = ((int)script.Position, (int)script.Length);
            this.ShapeScript(
                units + position,
                length,
                unitCodepoints.Slice(position, length),
                &script.Analysis,
                emSize,
                output);
        }
    }

    private static nint CreateAnalyzer()
    {
        IDWriteTextAnalyzer* analyzer;
        ((IDWriteFactory*)Factory)->CreateTextAnalyzer(&analyzer).ThrowOnError();
        return (nint)analyzer;
    }

    // Like Rent, but pinned so that it can be passed without fixed.
    private static T* RentPinned<T>(ref T[] buffer, int count)
        where T : unmanaged
    {
        buffer = buffer.Grow(count, true);
        return (T*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(buffer));
    }

    // Shapes text of one script and appends the glyphs.
    private void ShapeScript(
        char* text, int length, ReadOnlySpan<int> unitCodepoints, DWRITE_SCRIPT_ANALYSIS* script, float emSize,
        ShapedGlyphList output)
    {
        const int eNotSufficientBuffer = unchecked((int)0x8007007A);
        var clusterMap = RentPinned(ref this.clusterMapBuffer, length);
        var textProps = RentPinned(ref this.textPropsBuffer, length);
        var maxGlyphs = (length * 3 / 2) + 16;
        ushort* indices;
        DWRITE_SHAPING_GLYPH_PROPERTIES* glyphProps;
        uint glyphCount;
        while (true)
        {
            indices = RentPinned(ref this.shapeIndexBuffer, maxGlyphs);
            glyphProps = RentPinned(ref this.glyphPropsBuffer, maxGlyphs);
            var hr = Analyzer->GetGlyphs(
                text,
                (uint)length,
                this.face,
                false,
                false,
                script,
                EmptyLocale,
                null,
                null,
                null,
                0,
                (uint)maxGlyphs,
                clusterMap,
                textProps,
                indices,
                glyphProps,
                &glyphCount);
            if (hr != eNotSufficientBuffer)
            {
                hr.ThrowOnError();
                break;
            }

            maxGlyphs *= 2;
        }

        var advances = RentPinned(ref this.advanceBuffer, (int)glyphCount);
        var offsets = RentPinned(ref this.offsetBuffer, (int)glyphCount);
        Analyzer->GetGlyphPlacements(
            text,
            clusterMap,
            textProps,
            (uint)length,
            indices,
            glyphProps,
            glyphCount,
            this.face,
            emSize,
            false,
            false,
            script,
            EmptyLocale,
            null,
            null,
            0,
            advances,
            offsets).ThrowOnError();

        // A cluster is the code units mapping to the same first glyph.
        for (var u = 0; u < length;)
        {
            var g0 = (int)clusterMap[u];
            var ue = u + 1;
            while (ue < length && clusterMap[ue] == g0)
                ue++;
            var g1 = ue < length ? clusterMap[ue] : (int)glyphCount;
            var unsafeBefore = u > 0 && textProps[u - 1].canBreakShapingAfter == 0;
            for (var g = g0; g < g1; g++)
            {
                var offset = new Vector2(offsets[g].advanceOffset, -offsets[g].ascenderOffset);
                output.Add(indices[g], unitCodepoints[u], advances[g], offset, g == g0 && unsafeBefore);
            }

            u = ue;
        }
    }
}
