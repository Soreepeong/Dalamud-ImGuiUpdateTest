namespace Dalamud.Bindings.ImGui;

/// <summary>How text is read.</summary>
/// <seealso cref="ImTextStyle.Markup"/>
/// <remarks>The native side reads these values as well. Keep it in sync when values are added.</remarks>
public enum ImTextMarkup : byte
{
    /// <summary>UTF-8.</summary>
    Plain,

    /// <summary>A SeString.</summary>
    SeString,

    /// <summary>The macro representation of a SeString.</summary>
    MacroString,
}
