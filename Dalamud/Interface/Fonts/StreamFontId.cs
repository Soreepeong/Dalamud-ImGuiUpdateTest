using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using Dalamud.Interface.Fonts.Internal;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>Represents a font read from a stream.</summary>
/// <remarks>Cannot be serialized.</remarks>
[JsonConverter(typeof(NotSerializableJsonConverter))]
public sealed class StreamFontId : IFontFamilyId, IFontId
{
    private static readonly ConditionalWeakTable<Assembly, ConcurrentDictionary<string, WeakReference<StreamFontId>>>
        ResourceIds = new();

    /// <summary>Initializes a new instance of the <see cref="StreamFontId"/> class.</summary>
    /// <param name="stream">A stream with the font file from its current position.</param>
    /// <param name="name">Display name, or <see langword="null"/> to use a key made from the data.</param>
    internal StreamFontId(Stream stream, string? name = null)
    {
        long length = -1;
        try
        {
            length = Math.Max(stream.Length - stream.Position, 0);
        }
        catch
        {
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            this.Data = ms.ToArray();
        }

        if (length >= 0)
        {
            var data = new byte[length];
            stream.ReadExactly(data);
            this.Data = data;
        }

        this.Key = ComputeKey(this.Data!);
        this.Name = new(name ?? this.Key);
    }

    /// <summary>Initializes a new instance of the <see cref="StreamFontId"/> class.</summary>
    /// <param name="data">The font file, not copied.</param>
    /// <param name="name">Display name, or <see langword="null"/> to use a key made from the data.</param>
    internal StreamFontId(byte[] data, string? name = null)
    {
        this.Data = data;
        this.Key = ComputeKey(data);
        this.Name = new(name ?? this.Key);
    }

    private StreamFontId(byte[] data, string key, string name)
    {
        this.Data = data;
        this.Key = key;
        this.Name = new(name);
    }

    /// <inheritdoc cref="IFontFamilyId.Name"/>
    public LocalizedName Name { get; }

    /// <inheritdoc/>
    public IReadOnlyList<IFontId> Fonts => new List<IFontId> { this }.AsReadOnly();

    /// <inheritdoc/>
    public IFontFamilyId Family => this;

    /// <inheritdoc/>
    public FontStyle Style => FontStyle.Default;

    /// <summary>Gets a string sufficient to distinguish a font from another.</summary>
    internal string Key { get; }

    /// <summary>Gets the font file.</summary>
    internal byte[] Data { get; }

    public static bool operator ==(StreamFontId? left, StreamFontId? right) => Equals(left, right);

    public static bool operator !=(StreamFontId? left, StreamFontId? right) => !Equals(left, right);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is StreamFontId other && this.Key == other.Key;

    /// <inheritdoc/>
    public override int GetHashCode() => this.Key.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(StreamFontId)}:{this.Name.English}";

    /// <inheritdoc/>
    public int FindBestMatch(FontStyle style) => 0;

    /// <summary>Creates an instance from a resource embedded in an assembly.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="resourceName">The name of the resource.</param>
    /// <returns>The font id. The same one while it is alive.</returns>
    internal static StreamFontId FromManifestResource(Assembly assembly, string resourceName)
    {
        // Specs of a resource may be made for every frame. The resource is read again only after its id is gone.
        var ids = ResourceIds.GetOrCreateValue(assembly);
        if (ids.TryGetValue(resourceName, out var weak) && weak.TryGetTarget(out var id))
            return id;

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new FileNotFoundException("Resource not found.", resourceName);
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        id = new(data, $"resource:{assembly.ManifestModule.ModuleVersionId:N}:{resourceName}", resourceName);
        ids[resourceName] = new(id);
        return id;
    }

    /// <summary>Hashes the head and name tables of every face (head holds the version and a checksum of the whole
    /// file), plus the file length.</summary>
    private static string ComputeKey(byte[] data)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var span = data.AsSpan();
            var faceOffsets = BinaryPrimitives.ReadUInt32BigEndian(span) == 0x74746366 /* ttcf */
                                  ? ReadCollectionOffsets(span)
                                  : [0];
            foreach (var face in faceOffsets)
            {
                var numTables = BinaryPrimitives.ReadUInt16BigEndian(span[(face + 4)..]);
                for (var i = 0; i < numTables; i++)
                {
                    var record = span[(face + 12 + (i * 16))..];
                    var tag = BinaryPrimitives.ReadUInt32BigEndian(record);
                    if (tag is not (0x68656164 /* head */ or 0x6E616D65 /* name */))
                        continue;
                    var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
                    var length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
                    sha.AppendData(span.Slice(offset, length));
                }
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            sha.AppendData(data);
        }

        return $"{data.Length}:{Convert.ToHexString(sha.GetHashAndReset())}";
    }

    private static int[] ReadCollectionOffsets(ReadOnlySpan<byte> span)
    {
        // A broken file must not make a huge array. The data cannot hold more offsets than this.
        var count = (int)Math.Min(
                        BinaryPrimitives.ReadUInt32BigEndian(span[8..]),
                        (uint)Math.Max(0, (span.Length - 12) / 4));
        var offsets = new int[count];
        for (var i = 0; i < count; i++)
            offsets[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(12 + (i * 4))..]);
        return offsets;
    }
}
