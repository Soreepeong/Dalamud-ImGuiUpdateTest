using System.Threading;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>The styler of each markup.</summary>
internal static class TextStylers
{
    // ImTextStyle keeps the markup in 2 bits.
    private static readonly TextStyler?[] Stylers = new TextStyler?[4];

    /// <summary>Gets the styler of a markup.</summary>
    /// <param name="markup">The markup.</param>
    /// <returns>The styler, or null to lay the text out as UTF-8.</returns>
    public static TextStyler? Get(ImTextMarkup markup) =>
        (uint)markup < (uint)Stylers.Length ? Volatile.Read(ref Stylers[(int)markup]) : null;

    /// <summary>Sets the styler of its markup, replacing the one before.</summary>
    /// <param name="styler">The styler.</param>
    public static void Register(TextStyler styler) => Volatile.Write(ref Stylers[(int)styler.Markup], styler);

    /// <summary>Removes a styler, if it is the one of its markup.</summary>
    /// <param name="styler">The styler.</param>
    public static void Unregister(TextStyler styler) =>
        Interlocked.CompareExchange(ref Stylers[(int)styler.Markup], null, styler);
}
