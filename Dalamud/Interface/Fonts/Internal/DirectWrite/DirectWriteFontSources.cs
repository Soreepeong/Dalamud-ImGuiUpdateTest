using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;

using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Storage.Assets;

using JetBrains.Annotations;

using TerraFX.Interop.DirectX;

namespace Dalamud.Interface.Fonts.Internal.DirectWrite;

/// <summary>Fonts of OpenType files, and the system font fallback, drawn with DirectWrite.</summary>
/// <remarks><ul>
/// <li>Faces are cached by file, face, simulations and axis values, so that the fonts of a face share its glyph images.
/// Installed faces are mapped from their files by DirectWrite. Other files are copied into memory.</li>
/// <li>The caches keep fonts and faces only while a font uses them. The system fallbacks are kept.</li>
/// </ul></remarks>
[UsedImplicitly]
internal sealed unsafe class DirectWriteFontSources : IFontSourceProvider, IDisposable
{
    private readonly WeakCache<(DalamudAsset Asset, int FontNo, string Locale), Font> assetFonts = new();
    private readonly WeakCache<(string Path, int FontNo, string Locale), Font> fileFonts = new();
    private readonly WeakCache<(string Key, int FontNo, string Locale), Font> streamFonts = new();
    private readonly WeakCache<(SystemFontId Id, string Locale), Font> installedFonts = new();

    // By locale. Kept across default chains, so that making the chain again does not rasterize its glyphs again.
    private readonly ConcurrentDictionary<string, Lazy<Font>> systemFallbacks = new();

    // Shared by the fonts of all locales, the styled fonts and the system fallbacks.
    private readonly WeakCache<DirectWriteFontFace.FaceKey, DirectWriteFontFace> faces = new();

    private readonly ColorGlyphRasterizer colorGlyphs = new();

    // Shared by the faces made here, so that their finished glyphs are added within one budget per frame.
    private readonly ConcurrentGlyphQueue<GlyphRasterRequest> deferral = DirectWriteFontFace.CreateDeferral();

    /// <summary>Initializes a new instance of the <see cref="DirectWriteFontSources"/> class. Makes Windows enumerate
    /// the installed fonts in the background.</summary>
    public DirectWriteFontSources() => SystemFallbackFont.Preload();

    // Taken when an asset is asked for, so that fonts of files need no services.
    private static DalamudAssetManager Assets => Service<DalamudAssetManager>.Get();

    /// <inheritdoc/>
    /// <remarks><ul>
    /// <li>The game symbol asset is declined while its file is missing. AXIS stands in for it then.</li>
    /// <li>The system fallback takes the locale of its id, not <paramref name="locale"/>.</li>
    /// </ul></remarks>
    public bool TryGetFont(IFontId id, int fontNo, string locale, [NotNullWhen(true)] out Font? font)
    {
        font = id switch
        {
            DalamudAssetFontAndFamilyId { Asset: DalamudAsset.LodestoneGameSymbol }
                when !Assets.IsAvailableLocally(DalamudAsset.LodestoneGameSymbol) => null,
            DalamudAssetFontAndFamilyId a => this.GetAssetFont(a.Asset, fontNo, locale),
            FileFontId f => this.GetFileFont(f.Path, fontNo, locale),
            StreamFontId s => this.GetStreamFont(s, fontNo, locale),
            SystemFontId s => this.GetSystemFont(s, locale),
            SystemFallbackFontId s => this.GetSystemFallback(s.Locale),
            _ => null,
        };
        return font is not null;
    }

    /// <summary>Gets a face from the face cache, making it if needed.</summary>
    /// <param name="name">Name for debugging, if the face is made.</param>
    /// <param name="from">A face of the file.</param>
    /// <param name="simulations">What DirectWrite is to simulate.</param>
    /// <param name="key">The key of the face, or null to take it from the file of <paramref name="from"/>.</param>
    /// <returns>The face. Without <paramref name="key"/>, a face of a file that is not on disk is made anew.
    /// </returns>
    /// <remarks>Called on any thread.</remarks>
    public DirectWriteFontFace GetFace(
        string name, IDWriteFontFace* from, DWRITE_FONT_SIMULATIONS simulations,
        DirectWriteFontFace.FaceKey? key = null)
    {
        key ??= DirectWriteFontFace.FaceKey.Of(from, simulations);
        if (key is not { } k)
            return this.CreateFace(name, from, simulations, null);
        var pFrom = (nint)from;
        return this.faces.GetOrCreate(k, () => this.CreateFace(name, (IDWriteFontFace*)pFrom, simulations, k));
    }

    /// <inheritdoc/>
    /// <remarks>Fonts made here keep working, but draw the outlines of color glyphs from then on. They rasterize every
    /// glyph in the frame it is asked for. Called on the main thread.</remarks>
    public void Dispose()
    {
        this.deferral.Dispose();
        this.colorGlyphs.Dispose();
    }

    // Gets a font from a cache, making it if needed. One that failed to be made is not kept, so that it is tried again.
    private static T GetOrCreate<TKey, T>(ConcurrentDictionary<TKey, Lazy<T>> fonts, TKey key, Func<T> create)
        where TKey : notnull
    {
        var lazy = fonts.GetOrAdd(key, static (_, c) => new(c), create);
        try
        {
            return lazy.Value;
        }
        catch
        {
            fonts.TryRemove(new(key, lazy));
            throw;
        }
    }

    private DirectWriteFontFace CreateFace(
        string name, IDWriteFontFace* from, DWRITE_FONT_SIMULATIONS simulations, DirectWriteFontFace.FaceKey? key) =>
        new(name, from, simulations, this.colorGlyphs, this.deferral, this, key);

    private DirectWriteFontFace CreateFace(string name, ReadOnlySpan<byte> data, DirectWriteFontFace.FaceKey key) =>
        new(name, data, key.FontNo, this.colorGlyphs, this.deferral, this, key);

    private Font GetSystemFallback(string locale) =>
        GetOrCreate(
            this.systemFallbacks,
            locale,
            () => new SystemFallbackFont(locale, this));

    private Font GetStreamFont(StreamFontId id, int fontNo, string locale) =>
        this.streamFonts.GetOrCreate(
            (id.Key, fontNo, locale),
            () =>
            {
                var name = $"{id.Name.English}#{fontNo}";
                var key = new DirectWriteFontFace.FaceKey(
                    $"stream:{id.Key}", fontNo, DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE);
                return new DirectWriteFont(
                    name,
                    this.faces.GetOrCreate(key, () => this.CreateFace(name, id.Data, key)),
                    locale);
            });

    private Font GetAssetFont(DalamudAsset asset, int fontNo, string locale) =>
        this.assetFonts.GetOrCreate(
            (asset, fontNo, locale),
            () =>
            {
                var name = $"{asset}#{fontNo}";
                var key = new DirectWriteFontFace.FaceKey(
                    $"asset:{asset}", fontNo, DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE);
                var face = this.faces.GetOrCreate(
                    key,
                    () =>
                    {
                        using var stream = Assets.CreateStream(asset);
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        return this.CreateFace(name, ms.GetBuffer().AsSpan(0, (int)ms.Length), key);
                    });
                return new DirectWriteFont(name, face, locale);
            });

    // The file is copied, so that it can be changed or deleted while the font lives. An installed face that another
    // font took first is shared.
    private Font GetFileFont(string path, int fontNo, string locale)
    {
        var key = DirectWriteFontFace.FaceKey.OfFile(path, fontNo);
        return this.fileFonts.GetOrCreate(
            (key.File, fontNo, locale),
            () =>
            {
                var name = $"{Path.GetFileName(key.File)}#{fontNo}";
                return new DirectWriteFont(
                    name,
                    this.faces.GetOrCreate(key, () => this.CreateFace(name, File.ReadAllBytes(path), key)),
                    locale);
            });
    }

    // DirectWrite maps the file. The face is shared with the fonts of other ids and locales that come to it.
    private Font GetSystemFont(SystemFontId id, string locale) =>
        this.installedFonts.GetOrCreate(
            (id, locale),
            () =>
            {
                using var face = DirectWriteUtilities.CreateFace(id);
                var sims = face.Get()->GetSimulations();
                var key = DirectWriteFontFace.FaceKey.Of(face.Get(), sims);
                var name = key is { } k
                               ? $"{Path.GetFileName(k.File)}#{k.FontNo}{(k.Axes.Length > 0 ? " " : null)}{k.Axes}"
                               : $"{id.Family.Name.English} {id.Style}";
                return new DirectWriteFont(name, this.GetFace(name, face.Get(), sims, key), locale);
            });

    /// <summary>A cache that keeps its items only while something else uses them.</summary>
    /// <typeparam name="TKey">The key.</typeparam>
    /// <typeparam name="T">The item.</typeparam>
    /// <remarks>Made on font loads, not per frame. One item is made per key at a time.</remarks>
    private sealed class WeakCache<TKey, T>
        where TKey : notnull
        where T : class
    {
        // The count from which collected items are looked for.
        private const int MinPruneCount = 64;

        private readonly ConcurrentDictionary<TKey, Slot> slots = new();
        private int pruneCount = MinPruneCount;

        /// <summary>Gets an item, making it if it is not cached or was collected.</summary>
        /// <param name="key">The key.</param>
        /// <param name="create">Makes the item. If it throws, nothing is kept, so that it is tried again.</param>
        /// <returns>The item.</returns>
        public T GetOrCreate(TKey key, Func<T> create)
        {
            while (true)
            {
                var slot = this.slots.GetOrAdd(key, static _ => new());
                using (slot.Lock.EnterScope())
                {
                    if (slot.Removed)
                        continue;
                    if (slot.Item?.TryGetTarget(out var item) is not true)
                    {
                        item = create();
                        if (slot.Item is null)
                            slot.Item = new(item);
                        else
                            slot.Item.SetTarget(item);
                    }

                    if (this.slots.Count >= Volatile.Read(ref this.pruneCount))
                        this.Prune();
                    return item;
                }
            }
        }

        // Drops the slots of collected items. A slot whose item is being made has no item yet, and is kept.
        private void Prune()
        {
            foreach (var (key, slot) in this.slots)
            {
                if (!slot.Lock.TryEnter())
                    continue;
                if (slot.Item?.TryGetTarget(out _) is false)
                {
                    slot.Removed = true;
                    this.slots.TryRemove(new(key, slot));
                }

                slot.Lock.Exit();
            }

            Volatile.Write(ref this.pruneCount, Math.Max(MinPruneCount, this.slots.Count * 2));
        }

        private sealed class Slot
        {
            public Lock Lock { get; } = new();

            public WeakReference<T>? Item { get; set; }

            public bool Removed { get; set; }
        }
    }
}
