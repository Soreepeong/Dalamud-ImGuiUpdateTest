using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Game.Text;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Logging.Internal;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Makes the default font chain.</summary>
/// <remarks>The game symbols come first, the default font spec from Dalamud Settings next, and then Windows' system
/// font fallback.</remarks>
internal sealed class DefaultFontChain
{
    /// <summary>The font id of the game symbol font, from the local Dalamud assets.</summary>
    public static readonly DalamudAssetFontAndFamilyId GameSymbolsId = new(DalamudAsset.LodestoneGameSymbol);

    /// <summary>The font id that stands in for the game symbol font while it is missing and fills its gaps.</summary>
    public static readonly GameFontAndFamilyId GameSymbolsStandInId = new(GameFontFamily.Axis);

    // The family name of the game symbol font.
    private const string GameSymbolsFamilyName = "XIV AXIS Std ATK";

    // The size of the game symbol font, relative to the default font. Its hhea line height is 2 em. At 1.5, its
    // symbols match those of AXIS in size and baseline.
    private const float GameSymbolsSizeRatio = 1.5f;

    private static readonly ModuleLog Log = ModuleLog.Create<DefaultFontChain>();

    // The font id of the game symbol font, when it is installed.
    private static readonly SystemFontId GameSymbolsInstalledId = new(new(GameSymbolsFamilyName), default);

    private static readonly (uint Low, uint High) GameSymbolsRange =
        ((uint)Enum.GetValues<SeIconChar>().Min(), (uint)Enum.GetValues<SeIconChar>().Max());

    private readonly FontFactory factory;
    private readonly FontRegistry registry;
    private readonly Func<string, bool> isFamilyInstalled;

    /// <summary>Initializes a new instance of the <see cref="DefaultFontChain"/> class.</summary>
    /// <param name="factory">The factory to add the default font spec with.</param>
    /// <param name="registry">The registry to get the other fonts from.</param>
    /// <param name="isFamilyInstalled">Tests whether a font family is installed, by name.</param>
    public DefaultFontChain(FontFactory factory, FontRegistry registry, Func<string, bool> isFamilyInstalled)
    {
        this.factory = factory;
        this.registry = registry;
        this.isFamilyInstalled = isFamilyInstalled;
    }

    /// <summary>Makes the default font chain.</summary>
    /// <param name="userSpec">The default font spec.</param>
    /// <param name="locale">The locale.</param>
    /// <returns>The chain.</returns>
    /// <remarks>A step that throws is logged and left out.</remarks>
    public CompositeFont Create(FontSpec userSpec, string locale)
    {
        var elements = new List<CompositeFont.Element>();
        try
        {
            this.AddGameSymbols(elements, locale);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to add the game symbols to the default font; ignoring");
        }

        var count = elements.Count;
        this.TryAppendSpec(elements, userSpec, locale);
        if (elements.Count == count)
            this.TryAppendSpec(elements, FontFactory.BuiltInDefaultFontSpec, locale);

        try
        {
            if (this.registry.GetSourceFont(new SystemFallbackFontId(locale), 0, locale) is { } fallback)
                elements.Add(new(fallback));
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to add the system font fallback for {Locale}; ignoring", locale);
        }

        return CompositeFont.CreateRelative($"Default ({locale})", 0, CollectionsMarshal.AsSpan(elements));
    }

    private void AddGameSymbols(List<CompositeFont.Element> elements, string locale)
    {
        var symbols = (this.isFamilyInstalled(GameSymbolsFamilyName)
                           ? this.TryGetFont(GameSymbolsInstalledId, locale)
                           : null)
                      ?? this.TryGetFont(GameSymbolsId, locale);
        if (symbols is not null)
            elements.Add(new(symbols, [GameSymbolsRange], GameSymbolsSizeRatio, Supplement: true));

        // Lodestone font lacks the boxed outlined numbers.
        if (this.TryGetFont(GameSymbolsStandInId, locale) is { } axis)
            elements.Add(new(axis, [GameSymbolsRange], Supplement: true));
    }

    private Font? TryGetFont(IFontId id, string locale)
    {
        try
        {
            return this.registry.GetSourceFont(id, 0, locale);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to load the game symbols from {Id}; ignoring", id);
            return null;
        }
    }

    private void TryAppendSpec(List<CompositeFont.Element> elements, FontSpec spec, string locale)
    {
        try
        {
            this.factory.AppendSpec(elements, 1f, spec, 1f, locale, skipDefault: true);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to load the default font {Spec}", spec);
        }
    }
}
