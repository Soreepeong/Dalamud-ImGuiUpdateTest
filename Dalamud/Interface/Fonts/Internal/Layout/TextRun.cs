using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>A range of attributed text.</summary>
/// <param name="TextBegin">Starting byte offset.</param>
/// <param name="TextEnd">Ending byte offset.</param>
/// <param name="Style">Optional style override.</param>
/// <param name="UserData">Context for hit test returns.</param>
internal readonly record struct TextRun(
    int TextBegin,
    int TextEnd,
    ImTextStyle? Style = null,
    ulong UserData = 0);
