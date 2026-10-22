using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>A range of attributed text.</summary>
/// <param name="TextBegin">Starting byte offset.</param>
/// <param name="TextEnd">Ending byte offset.</param>
/// <param name="Font">Optional font override.</param>
/// <param name="Size">Optional size override.</param>
/// <param name="Style">Optional style override.</param>
/// <param name="UserData">Context for hit test returns.</param>
internal readonly record struct TextRun(
    int TextBegin,
    int TextEnd,
    ManagedFont? Font = null,
    float Size = 0,
    ImTextStyle? Style = null,
    ulong UserData = 0);
