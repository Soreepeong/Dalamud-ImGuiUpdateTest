using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Configuration.Internal;
using Dalamud.Data;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Interface.Internal;
using Dalamud.Storage.Assets;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Makes fonts from font specs.</summary>
/// <seealso cref="FontRegistry"/>
[ServiceManager.EarlyLoadedService]
internal sealed class FontFactory : IInternalDisposableService
{
    private readonly FontRegistry registry;
    private readonly DefaultFontChain defaultChainMaker;
    private readonly Func<string?> language;

    // By locale. Replaced as a whole on invalidation, so that invalidating never waits for a chain being made. Each is
    // also the lock for making its chains.
    private Dictionary<string, CompositeFont> defaultChains = [];

    /// <summary>Initializes a new instance of the <see cref="FontFactory"/> class.</summary>
    /// <param name="registry">The font sources.</param>
    /// <param name="isFamilyInstalled">Tests whether a font family is installed, by name, or null to ask Windows.
    /// </param>
    /// <param name="language">Gets the UI language. Without it, or with a null result, it is English.</param>
    /// <remarks>Tests set <see cref="DefaultFontSpecOverride"/>, as reading the configuration waits for the services.
    /// </remarks>
    internal FontFactory(
        FontRegistry registry, Func<string, bool>? isFamilyInstalled = null, Func<string?>? language = null)
    {
        this.registry = registry;
        this.language = language ?? (static () => null);
        this.defaultChainMaker = new(this, registry, isFamilyInstalled ?? DirectWriteUtilities.IsFamilyInstalled);
    }

    // The font sources and stylers that the registry finds take these services while they are made.
    [ServiceManager.ServiceConstructor]
    private FontFactory(DataManager dataManager, DalamudAssetManager dalamudAssetManager, Localization localization)
        : this(new(), null, () => localization.LanguageCode)
    {
    }

    /// <summary>Raised when the default font chain changed, such as by settings or the UI language.</summary>
    /// <remarks>Fonts that end with the default font, or take the locale of the UI language, must be made again.
    /// </remarks>
    public event Action? DefaultChainChanged;

    /// <summary>Gets the default font spec that is used if none is configured.</summary>
    public static FontSpec BuiltInDefaultFontSpec { get; } =
        FontSpec.From(GameFontFamily.Axis, InterfaceManager.DefaultFontSizePx, fallback: false);

    /// <summary>Gets or sets the default font spec to use instead of the configured one.</summary>
    public FontSpec? DefaultFontSpecOverride { get; set; }

    /// <summary>Gets the default font spec.</summary>
    public FontSpec DefaultFontSpec =>
        this.DefaultFontSpecOverride
        ?? Service<DalamudConfiguration>.Get().DefaultFontSpec
        ?? BuiltInDefaultFontSpec;

    /// <summary>Gets the default font size in pixels (before global scaling).</summary>
    public float DefaultSizePx =>
        this.DefaultFontSpec.SizePx is > 0 and var size ? size : InterfaceManager.DefaultFontSizePx;

    /// <summary>Gets <see cref="FontSpec.LineHeight"/> of the default font.</summary>
    public float DefaultLineHeightScale => GetLineHeight(this.DefaultFontSpec);

    /// <summary>Gets the locale of the UI language, for specs without a <see cref="FontSpec.Locale"/>.</summary>
    /// <remarks>It is read when a font or a default chain is made.</remarks>
    public string InferredLocale => LocaleOf(this.language());

    /// <summary>Tests whether the font of a spec changes when the default font changes.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>Whether it has a relative size, the default font in it, or no locale.</returns>
    /// <remarks>These are what <see cref="CreateFont"/> reads of the default font.</remarks>
    public static bool FollowsDefaultFont(FontSpec spec) =>
        spec.SizePx <= 0 || spec.Locale is null || UsesDefaultFont(spec);

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService() => this.registry.Dispose();

    /// <summary>Forgets the default font chains, and tells everything that uses them to make its fonts again.</summary>
    public void InvalidateDefaultChain()
    {
        Volatile.Write(ref this.defaultChains, []);
        this.DefaultChainChanged.InvokeSafely();
    }

    /// <summary>Gets the font a font id stands for.</summary>
    /// <param name="id">The font id.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="locale">The locale to draw text in.</param>
    /// <returns>The font.</returns>
    /// <exception cref="NotSupportedException">If no font source serves the id.</exception>
    /// <remarks>The game symbols are AXIS while nothing serves them, as in the default chain.</remarks>
    public Font GetSourceFont(IFontId id, int fontNo, string locale) =>
        id is DalamudDefaultFontAndFamilyId
            ? this.GetDefaultChain(locale)
            : this.registry.GetSourceFont(id, fontNo, locale)
              ?? (id.Equals(DefaultFontChain.GameSymbolsId)
                      ? this.registry.GetSourceFont(DefaultFontChain.GameSymbolsStandInId, 0, locale)
                      : null)
              ?? throw new NotSupportedException($"{id.GetType().Name} is not a supported font id.");

    /// <summary>Makes the font a spec stands for.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>A new font, with shared sources.</returns>
    /// <remarks>A spec without a locale is made in <see cref="InferredLocale"/>.</remarks>
    public CompositeFont CreateFont(FontSpec spec)
    {
        var name = spec.ToString()!;
        var elements = new List<CompositeFont.Element>();
        if (spec.SizePx > 0)
        {
            this.AppendSpec(elements, spec.SizePx, spec, 1f, this.InferredLocale);
            return new(name, spec.SizePx, CollectionsMarshal.AsSpan(elements), GetLineHeight(spec));
        }

        this.AppendSpec(elements, 1f, spec, 1f, this.InferredLocale);
        return CompositeFont.CreateRelative(
            name,
            spec.SizePx,
            CollectionsMarshal.AsSpan(elements),
            GetLineHeight(spec),
            this.DefaultLineHeightScale);
    }

    /// <summary>Adds the elements of a spec's fonts.</summary>
    /// <param name="target">The elements to add to.</param>
    /// <param name="targetBaseSizePx">The <see cref="CompositeFont.BaseSizePx"/> of the font the elements are for.
    /// </param>
    /// <param name="spec">The spec.</param>
    /// <param name="ratio">Size of the spec relative to the size the font is drawn at.</param>
    /// <param name="locale">The locale of the spec it is in, for a spec without one.</param>
    /// <param name="skipDefault">Leave out the default font, including <see cref="DalamudDefaultFontAndFamilyId"/>
    /// fallbacks, for making the default font itself.</param>
    /// <remarks>The spec's own font goes first, then each fallback.</remarks>
    public void AppendSpec(
        List<CompositeFont.Element> target, float targetBaseSizePx, FontSpec spec, float ratio, string locale,
        bool skipDefault = false) =>
        this.AppendSpec(
            target,
            targetBaseSizePx,
            spec,
            ratio,
            FontSizes.ToGlyphSize(spec, this.DefaultSizePx),
            locale,
            skipDefault);

    // The locale of a Dalamud UI language, such as "ko-KR" for "ko". It is "en-US" for English, null, and languages not
    // in Localization.ApplicableLangCodes.
    private static string LocaleOf(string? language) =>
        language?.ToLowerInvariant() switch
        {
            "de" => "de-DE",
            "ja" => "ja-JP",
            "fr" => "fr-FR",
            "it" => "it-IT",
            "es" => "es-ES",
            "ko" => "ko-KR",
            "no" => "nb-NO",
            "ru" => "ru-RU",
            "zh" => "zh-CN",
            "tw" => "zh-TW",
            _ => "en-US",
        };

    // Whether a spec has the default font in it, as its font or in its fallbacks.
    private static bool UsesDefaultFont(FontSpec spec) =>
        spec.FontId is DalamudDefaultFontAndFamilyId || spec.Fallbacks.Any(UsesDefaultFont);

    // A locale name in the case Windows gives it, such as "ja-JP" for "ja-jp", so that locales that differ only in case
    // share fonts. Names Windows does not know are returned as they are.
    private static string NormalizeLocale(string locale)
    {
        try
        {
            return CultureInfo.GetCultureInfo(locale).Name is { Length: > 0 } name ? name : locale;
        }
        catch (CultureNotFoundException)
        {
            return locale;
        }
    }

    // The line height of a spec relative to its glyph size, or 1 if it has none.
    private static float GetLineHeight(FontSpec spec) => spec.LineHeight > 0 ? spec.LineHeight : 1f;

    // The default font with all its fallbacks, in a locale. It is made once per locale, until InvalidateDefaultChain.
    private CompositeFont GetDefaultChain(string locale)
    {
        locale = NormalizeLocale(locale);
        var chains = Volatile.Read(ref this.defaultChains);
        lock (chains)
        {
            if (!chains.TryGetValue(locale, out var chain))
                chains.Add(locale, chain = this.CreateDefaultChain(locale));
            return chain;
        }
    }

    // Makes the default font chain of DefaultFontSpec in a locale.
    private CompositeFont CreateDefaultChain(string locale) =>
        this.defaultChainMaker.Create(this.DefaultFontSpec, locale);

    // A relative fallback is relative to the spec it is in, so its pixel size comes from there. Only the outermost
    // relative spec is taken at the default size.
    private void AppendSpec(
        List<CompositeFont.Element> target, float targetBaseSizePx, FontSpec spec, float ratio, float specPx,
        string locale, bool skipDefault)
    {
        locale = spec.Locale is { } own ? NormalizeLocale(own) : locale;
        if (spec.FontId is { } id)
            this.AppendOwn(target, targetBaseSizePx, spec, id, ratio, locale, skipDefault);
        foreach (var fallback in spec.Fallbacks)
        {
            var fallbackPx = FontSizes.ToGlyphSize(fallback, specPx);
            this.AppendSpec(
                target,
                targetBaseSizePx,
                fallback,
                ratio * (fallbackPx / specPx),
                fallbackPx,
                locale,
                skipDefault);
        }
    }

    // The spec's own font, with its adjustments.
    private void AppendOwn(
        List<CompositeFont.Element> target, float targetBaseSizePx, FontSpec spec, IFontId id, float ratio,
        string locale, bool skipDefault)
    {
        var ranges = spec.CodepointRanges is { } r ? CompositeFont.ToRanges(r.AsSpan()) : null;
        if (id is DalamudDefaultFontAndFamilyId)
        {
            // Later fonts go before it, as it has every character with the system fallback.
            if (!skipDefault)
                target.Add(new(this.GetDefaultChain(locale), ranges, ratio, Terminal: true));
            return;
        }

        // Convert lengths from pixels at the spec's size (or ems) to pixels at the target's base size (or ems).
        var scale = (targetBaseSizePx * ratio) / (spec.SizePx > 0 ? spec.SizePx : 1f);
        target.Add(
            new(
                this.GetSourceFont(id, spec.FontNo, locale),
                ranges,
                ratio,
                spec.GlyphOffset * scale,
                spec.LetterSpacing * scale,
                spec.GlyphMinAdvanceX * scale,
                spec.GlyphMaxAdvanceX >= float.MaxValue ? float.MaxValue : spec.GlyphMaxAdvanceX * scale,
                true,
                spec.Embolden * scale,
                spec.FitToSquare));
    }
}
