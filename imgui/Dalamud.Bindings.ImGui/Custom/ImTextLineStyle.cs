namespace Dalamud.Bindings.ImGui;

/// <summary>How a line of a text is drawn.</summary>
/// <seealso cref="ImTextLineDecoration"/>
public enum ImTextLineStyle : byte
{
    /// <summary>None.</summary>
    None,

    /// <summary><c>---------------</c></summary>
    Solid,

    /// <summary><c>. . . . . . . .</c></summary>
    Dotted,

    /// <summary><c>-- -- -- -- --</c></summary>
    Dashed,

    /// <summary><c>^v^v^v^v^v^v^v</c></summary>
    Wavy,
}
