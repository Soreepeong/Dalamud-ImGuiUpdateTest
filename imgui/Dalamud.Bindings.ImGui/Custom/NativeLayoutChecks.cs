#if DEBUG
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dalamud.Bindings.ImGui;

/// <summary>Checks the layouts of the structs that native code or Dalamud read at fixed offsets.</summary>
internal static unsafe class NativeLayoutChecks
{
    /// <summary>Checks the layouts when the bindings are loaded.</summary>
    /// <remarks>Not in static constructors of the structs, as those may never run for structs used through pointers.
    /// </remarks>
    /// <exception cref="InvalidOperationException">If a layout is wrong.</exception>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Debug-only check of the bindings' own types")]
    internal static void Check()
    {
        // ImFontStyleHead in imgui_widgets.cpp.
        Require(OffsetOf<ImFont>(nameof(ImFont.Impl)) == 0, "ImFont.Impl");
        Require(OffsetOf<ImFont>(nameof(ImFont.Style)) == sizeof(void*), "ImFont.Style");
        Require(OffsetOf<ImTextStyle>(nameof(ImTextStyle.Color)) == 0, "ImTextStyle.Color");
        Require(OffsetOf<ImTextStyle>("flags") == 4, "ImTextStyle.flags");

        Require(sizeof(ImTextStyle) == 0x40, "sizeof(ImTextStyle)");
        Require(OffsetOf<ImTextStyle>("<ExtraDecoration>k__BackingField") == 0x30, "ImTextStyle.ExtraDecoration");
        Require(sizeof(ImTextEdge) == 8, "sizeof(ImTextEdge)");
    }

    private static int OffsetOf<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    private static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException($"The layout of {what} changed.");
    }
}
#endif
