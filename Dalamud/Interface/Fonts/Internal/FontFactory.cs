using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Data;
using Dalamud.Game.Text;
using Dalamud.Interface.Fonts.Internal.Formats;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Textures.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Storage.Assets;
using Dalamud.Utility;

using Lumina.Data.Files;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Makes fonts from font specs.</summary>
[ServiceManager.EarlyLoadedService]
internal sealed class FontFactory : IInternalDisposableService
{
    private static readonly ModuleLog Log = ModuleLog.Create<FontFactory>();

    private static readonly (uint Low, uint High) SeIconCharRange =
        ((uint)Enum.GetValues<SeIconChar>().Min(), (uint)Enum.GetValues<SeIconChar>().Max());

    private readonly DalamudAssetManager dalamudAssetManager;
    private readonly ImmutableDictionary<GameFontFamilyAndSize, Task<byte[]>> fdtFiles;
    private readonly ImmutableDictionary<string, Task<Task<TexFile>[]>> texFiles;
    private readonly ConcurrentDictionary<(DalamudAsset Asset, int FontNo), Lazy<ManagedFont>> assetFonts = new();
    private readonly ConcurrentDictionary<(string Path, int FontNo), Lazy<ManagedFont>> fileFonts = new();
    private readonly ConcurrentDictionary<(string Key, int FontNo), Lazy<ManagedFont>> streamFonts = new();
    private readonly ConcurrentDictionary<GameFontFamily, Lazy<GameFontFamilyFont>> gameFontFamilies = new();

    // TextureManager loads after this, so its WIC factory is taken when the first color glyph is drawn.
    private readonly ColorGlyphRasterizer colorGlyphs =
        new(static () => Service<TextureManager>.Get().Wic.CreateFactoryReference());

    private readonly Lock defaultChainLock = new();
    private CompositeFont? defaultChain;

    [ServiceManager.ServiceConstructor]
    private FontFactory(DataManager dataManager, DalamudAssetManager dalamudAssetManager)
    {
        this.dalamudAssetManager = dalamudAssetManager;

        SystemFallbackFont.Preload();

        var gffasInfo = Enum.GetValues<GameFontFamilyAndSize>()
                            .Select(x => (Font: x, Attr: x.GetAttribute<GameFontFamilyAndSizeAttribute>()))
                            .Where(x => x.Attr is not null)
                            .ToArray();
        var texPaths = gffasInfo.Select(x => x.Attr.TexPathFormat).Distinct().ToArray();
        this.fdtFiles = gffasInfo.ToImmutableDictionary(
            x => x.Font,
            x => Task.Run(() => dataManager.GetFile(x.Attr.Path)!.Data));
        var textureCounts = texPaths.ToImmutableDictionary(
            x => x,
            x => Task.WhenAll(gffasInfo.Where(y => y.Attr.TexPathFormat == x).Select(y => this.fdtFiles[y.Font]))
                     .ContinueWith(files => 1 + files.Result.Max(file => new FdtFileView(file).MaxTextureIndex)));
        this.texFiles = textureCounts.ToImmutableDictionary(
            x => x.Key,
            x => x.Value.ContinueWith(y => Enumerable
                                           .Range(1, 1 + ((y.Result - 1) / 4))
                                           .Select(z => dataManager.GetFileAsync<TexFile>(
                                                       string.Format(x.Key, z),
                                                       CancellationToken.None))
                                           .ToArray()));
    }

    /// <summary>Raised when the default font chain changed, such as by settings or the UI language.</summary>
    /// <remarks>Fonts that end with the default font must be made again.</remarks>
    public event Action? DefaultChainChanged;

    /// <summary>Gets or sets the default font spec to use instead of the configured one.</summary>
    public IFontSpec? DefaultFontSpecOverride { get; set; }

    /// <summary>Gets the default font spec.</summary>
    public IFontSpec DefaultFontSpec =>
        this.DefaultFontSpecOverride
        ?? Service<DalamudConfiguration>.Get().DefaultFontSpec
        ?? new SingleFontSpec
        {
            FontId = new GameFontAndFamilyId(GameFontFamily.Axis),
            SizePx = InterfaceManager.DefaultFontSizePx,
        };

    /// <summary>Gets the default font size in pixels (before global scaling).</summary>
    public float DefaultSizePx =>
        this.DefaultFontSpec.SizePx is > 0 and var size ? size : InterfaceManager.DefaultFontSizePx;

    /// <summary>Gets <see cref="SingleFontSpec.LineHeight"/> of the default font.</summary>
    public float DefaultLineHeightScale => GetLineHeight(this.DefaultFontSpec);

    /// <summary>Gets the default font with all its fallbacks.</summary>
    public CompositeFont DefaultChain
    {
        get
        {
            using (this.defaultChainLock.EnterScope())
                return this.defaultChain ??= this.CreateDefaultChain();
        }
    }

    private bool HasGameSymbolsFontFile =>
        this.dalamudAssetManager.IsStreamImmediatelyAvailable(DalamudAsset.LodestoneGameSymbol);

    /// <summary>Tests whether a spec has the default font in it, as a part or as its fallback.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>Whether it does.</returns>
    public static bool UsesDefaultFont(IFontSpec spec) =>
        spec switch
        {
            SingleFontSpec single => single.FontId is DalamudDefaultFontAndFamilyId,
            MultiFontSpec multi => multi.FallBackToDefaultFont || multi.PartsOrEmpty.Any(UsesDefaultFont),
            _ => false,
        };

    /// <inheritdoc/>
    /// <remarks>Fonts made here keep working, but draw the outlines of color glyphs from then on.</remarks>
    void IInternalDisposableService.DisposeService() => this.colorGlyphs.Dispose();

    /// <summary>Forgets the default font chain, and tells everything that uses it to make its fonts again.</summary>
    public void InvalidateDefaultChain()
    {
        using (this.defaultChainLock.EnterScope())
            this.defaultChain = null;
        this.DefaultChainChanged.InvokeSafely();
    }

    /// <summary>Gets a reader of a game font file (.fdt).</summary>
    /// <param name="gffas">The font family and size.</param>
    /// <returns>The reader.</returns>
    /// <remarks>Blocks until loaded.</remarks>
    public FdtFileView GetFdtView(GameFontFamilyAndSize gffas) => new(this.fdtFiles[gffas].GetAwaiter().GetResult());

    /// <summary>Gets the font of a stream font id.</summary>
    /// <param name="id">The font id.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <returns>The font.</returns>
    public ManagedFont GetStreamFont(StreamFontId id, int fontNo) =>
        this.streamFonts.GetOrAdd(
            (id.Key, fontNo),
            key => new(() => new DirectWriteFont(
                           $"{id.EnglishName}#{key.FontNo}",
                           id.Data,
                           key.FontNo,
                           this.colorGlyphs))).Value;

    /// <summary>Gets all sizes of a game font family, as one font.</summary>
    /// <param name="family">The family.</param>
    /// <returns>The font.</returns>
    public GameFontFamilyFont GetGameFontFamily(GameFontFamily family) =>
        this.gameFontFamilies.GetOrAdd(
            family,
            key => new(() =>
            {
                var sizes = Enum.GetValues<GameFontFamilyAndSize>()
                                .Where(x => x != GameFontFamilyAndSize.Undefined && x.Family == key)
                                .Select(x => (Size: x, Attr: x.GetAttribute<GameFontFamilyAndSizeAttribute>()!))
                                .ToArray();
                if (sizes.Length == 0)
                    throw new ArgumentOutOfRangeException(nameof(family), family, null);
                var texPathFormat = sizes[0].Attr.TexPathFormat;
                return new(
                    key.ToString(),
                    sizes.Select(x => (this.GetFdtView(x.Size), x.Attr.HorizontalOffset)),
                    i =>
                    {
                        var tex = this.GetTexFile(texPathFormat, i);
                        var (w, h, format) = (tex.Header.Width, tex.Header.Height, tex.Header.Format);
                        return format is TexFile.TextureFormat.B4G4R4A4 or TexFile.TextureFormat.B8G8R8A8
                                   ? new(
                                       w,
                                       h,
                                       format == TexFile.TextureFormat.B4G4R4A4,
                                       tex.SliceSpan(0, 0, out _, out _, out _).ToArray())
                                   : new GameFontFamilyFont.Texture(w, h, false, tex.ImageData);
                    });
            })).Value;

    /// <summary>Gets the font a font id stands for.</summary>
    /// <param name="id">The font id.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <returns>The font.</returns>
    public ManagedFont GetSourceFont(IFontId id, int fontNo) =>
        id switch
        {
            GameFontAndFamilyId g => this.GetGameFontFamily(g.GameFontFamily),
            DalamudAssetFontAndFamilyId { Asset: DalamudAsset.LodestoneGameSymbol } when !this.HasGameSymbolsFontFile =>
                this.GetGameFontFamily(GameFontFamily.Axis),
            DalamudAssetFontAndFamilyId a => this.GetAssetFont(a.Asset, fontNo),
            FileFontId f => this.GetFileFont(f.Path, fontNo),
            StreamFontId s => this.GetStreamFont(s, fontNo),
            SystemFontId s => this.GetSystemFont(s),
            DalamudDefaultFontAndFamilyId => this.DefaultChain,
            _ => throw new NotSupportedException($"{id.GetType().Name} is not a supported font id."),
        };

    /// <summary>Makes the font a spec stands for.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>A new font, with shared sources.</returns>
    public CompositeFont CreateFont(IFontSpec spec)
    {
        var font = spec.SizePx > 0
                       ? new(spec.ToString()!, spec.SizePx)
                       : CompositeFont.CreateRelative(spec.ToString()!, spec.SizePx);
        font.SetLineHeightScale(GetLineHeight(spec), font.IsRelative ? this.DefaultLineHeightScale : 1f);
        this.AppendSpec(font, spec, 1f);
        return font;
    }

    /// <summary>Adds the fonts of a spec to a font.</summary>
    /// <param name="target">The font to add to.</param>
    /// <param name="spec">The spec.</param>
    /// <param name="ratio">Size of the spec relative to the size <paramref name="target"/> is drawn at.</param>
    /// <param name="skipDefault">Leave out the default font, including <see cref="DalamudDefaultFontAndFamilyId"/>
    /// parts, for making the default font itself.</param>
    public void AppendSpec(CompositeFont target, IFontSpec spec, float ratio, bool skipDefault = false)
    {
        switch (spec)
        {
            case MultiFontSpec multi:
            {
                // Part sizes are relative to the multi spec's size in pixels.
                var multiPx = multi.SizePx > 0 ? multi.SizePx : multi.RelativeSize * this.DefaultSizePx;
                foreach (var part in multi.PartsOrEmpty)
                {
                    var partRatio = part.SizePx > 0 ? part.SizePx / multiPx : part.SizePx == 0 ? 1f : -part.SizePx;
                    this.AppendSpec(target, part, ratio * partRatio, skipDefault);
                }

                if (multi.FallBackToDefaultFont && !skipDefault)
                    target.AddTerminal(new(this.DefaultChain, null, ratio));
                break;
            }

            case SingleFontSpec single:
                this.AppendSingle(target, single, ratio, skipDefault);
                break;

            default:
                throw new NotSupportedException($"{spec.GetType().Name} is not a supported font spec.");
        }
    }

    // The line height of a spec relative to its glyph size, or 1 if it has none.
    private static float GetLineHeight(IFontSpec spec) =>
        spec switch
        {
            SingleFontSpec { LineHeight: > 0 and var x } => x,
            MultiFontSpec { LineHeight: > 0 and var x } => x,
            _ => 1f,
        };

    // The first font of a spec that is not the default font, or null if it has none.
    private static IFontId? GetFirstFontId(IFontSpec spec) =>
        spec switch
        {
            SingleFontSpec { FontId: not DalamudDefaultFontAndFamilyId } single => single.FontId,
            MultiFontSpec multi => multi.PartsOrEmpty.Select(GetFirstFontId).FirstOrDefault(x => x is not null),
            _ => null,
        };

    private void AppendSingle(CompositeFont target, SingleFontSpec spec, float ratio, bool skipDefault)
    {
        var ranges = spec.CodepointRanges is { } r ? CompositeFont.ToRanges(r.AsSpan()) : null;
        if (spec.FontId is DalamudDefaultFontAndFamilyId)
        {
            // Later parts go before it, as it has every character with the system fallback.
            if (!skipDefault)
                target.AddTerminal(new(this.DefaultChain, ranges, ratio));
            return;
        }

        // Convert lengths from pixels at the spec's size (or ems) to pixels at the target's base size (or ems).
        var scale = target.BaseSizePx * ratio / (spec.SizePx > 0 ? spec.SizePx : 1f);
        target.Add(
            new(
                this.GetSourceFont(spec.FontId, spec.FontNo),
                ranges,
                ratio,
                spec.GlyphOffset * scale,
                spec.LetterSpacing * scale,
                spec.GlyphMinAdvanceX * scale,
                spec.GlyphMaxAdvanceX >= float.MaxValue ? float.MaxValue : spec.GlyphMaxAdvanceX * scale,
                true,
                spec.Weight * scale,
                spec.FitToSquare));
    }

    // A game font texture file. Index 0 is font1.tex. Blocks until loaded.
    private TexFile GetTexFile(string texPathFormat, int index) =>
        this.texFiles[texPathFormat].GetAwaiter().GetResult()[index].GetAwaiter().GetResult();

    private ManagedFont GetAssetFont(DalamudAsset asset, int fontNo) =>
        this.assetFonts.GetOrAdd(
            (asset, fontNo),
            key => new(() =>
            {
                using var stream = this.dalamudAssetManager.CreateStream(key.Asset);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return new DirectWriteFont(
                    $"{key.Asset}#{key.FontNo}",
                    ms.GetBuffer().AsSpan(0, (int)ms.Length),
                    key.FontNo,
                    this.colorGlyphs);
            })).Value;

    private ManagedFont GetFileFont(string path, int fontNo) =>
        this.fileFonts.GetOrAdd(
            (Path.GetFullPath(path).ToLowerInvariant(), fontNo),
            key => new(() => new DirectWriteFont(
                           $"{Path.GetFileName(key.Path)}#{key.FontNo}",
                           File.ReadAllBytes(key.Path),
                           key.FontNo,
                           this.colorGlyphs))).Value;

    private ManagedFont GetSystemFont(SystemFontId id)
    {
        var (path, index) = id.GetFileAndIndex();
        return this.GetFileFont(path, index);
    }

    private CompositeFont CreateDefaultChain()
    {
        var font = CompositeFont.CreateRelative("Default", 0);

        // The user's choice, without a default font of its own (that would be this chain).
        var isAxis = true;
        try
        {
            var spec = this.DefaultFontSpec;
            this.AppendSpec(font, spec, 1f, skipDefault: true);
            if (font.Elements.Count > 0)
                isAxis = GetFirstFontId(spec) is GameFontAndFamilyId { GameFontFamily: GameFontFamily.Axis };
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to load the default font {spec}, using AXIS", this.DefaultFontSpec);
        }

        if (font.Elements.Count == 0)
            font.Add(new(this.GetGameFontFamily(GameFontFamily.Axis)));

        // Game symbols (AXIS has them).
        if (!isAxis)
        {
            font.Add(
                new(
                    this.GetSourceFont(new DalamudAssetFontAndFamilyId(DalamudAsset.LodestoneGameSymbol), 0),
                    [SeIconCharRange],
                    this.HasGameSymbolsFontFile ? 1.5f : 1f));
        }

        // Glyphs for the UI language, and for Han/Hangul once the IME has seen them.
        var language = Service<DalamudConfiguration>.GetNullable()?.EffectiveLanguage;
        var ime = Service<DalamudIme>.GetNullable();
        (string Tag, int FontNo, string Locale)? cjk = language switch
        {
            "tw" => ("zh-hant", 1, "zh-TW"),
            "zh" => ("zh-hans", 2, "zh-CN"),
            "ko" => ("ko-kr", 3, "ko-KR"),
            _ when ime?.EncounteredHan is true => ("zh-hans", 2, "zh-CN"),
            _ when ime?.EncounteredHangul is true => ("ko-kr", 3, "ko-KR"),
            _ => null,
        };
        if (cjk is { } c)
        {
            try
            {
                font.Add(
                    new(
                        this.GetAssetFont(DalamudAsset.NotoSansCjkRegular, c.FontNo),
                        CompositeFont.ToRanges(default(FluentGlyphRangeBuilder).WithLanguage(c.Tag).BuildExact())));
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to load the {tag} glyphs", c.Tag);
            }
        }

        // Anything else from an installed font.
        try
        {
            var locale = cjk?.Locale ?? language switch
            {
                "ja" => "ja-JP",
                "de" => "de-DE",
                "fr" => "fr-FR",
                _ => "en-US",
            };
            font.Add(new(new SystemFallbackFont(locale, this.colorGlyphs)));
        }
        catch (Exception e)
        {
            Log.Error(e, "System font fallback is not available");
        }

        return font;
    }
}
