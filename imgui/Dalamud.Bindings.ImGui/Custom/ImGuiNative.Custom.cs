using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dalamud.Bindings.ImGui;

public static unsafe partial class ImGuiNative
{
    private const string LibraryName = "cimgui";

    static ImGuiNative()
    {
        if (LibraryName != ImGui.GetLibraryName())
        {
            throw new(
                $"{nameof(LibraryName)}(={LibraryName})" +
                $" does not match " +
                $"{nameof(ImGui)}.{nameof(ImGui.GetLibraryName)}(={ImGui.GetLibraryName()})");
        }
    }

    [LibraryImport(LibraryName, EntryPoint = "ImDrawList_AddCallback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void AddCallback(
        ImDrawList* self,
        delegate*<ImDrawList*, ImDrawCmd*, void> callback,
        void* callbackData = null);

    /// <summary>Sets the host text callbacks (imgui_dalamud_text.h) as function pointers.</summary>
    /// <param name="layout">Creates layouts:
    /// <c>ImHRESULT (ImFont*, const ImTextDesc*, const char*, const char*, ImTextLayout** out_layout)</c>.</param>
    /// <param name="metrics">Gives font metrics:
    /// <c>void (ImFont*, float size, float density, ImTextMetricsFlags flags, ImTextMetrics*)</c>.</param>
    [LibraryImport(LibraryName, EntryPoint = "igSetTextCallbacks")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetTextCallbacks(
        delegate* unmanaged[Cdecl]<ImFont*, ImTextDesc*, byte*, byte*, ImTextLayout**, int> layout,
        delegate* unmanaged[Cdecl]<ImFont*, float, float, ImTextMetricsFlags, ImTextMetrics*, void> metrics);

    [LibraryImport(LibraryName, EntryPoint = "igInputTextEx")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte InputTextEx(
        byte* label,
        byte* hint,
        byte* buf,
        int bufSize,
        Vector2 sizeArg,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None,
        delegate* unmanaged[Cdecl]<ImGuiInputTextCallbackData*, int> callback = null,
        void* userData = null);
}
