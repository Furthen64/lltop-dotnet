using System.Text;

sealed record GgufMetadata(uint Version, IReadOnlyDictionary<string, object?> Values)
{
    public string? String(string key) => Values.TryGetValue(key, out var value) ? value as string : null;
}

static class GgufMetadataReader
{
    const int MaxMetadataEntries = 100_000;
    const int MaxStringBytes = 16 * 1024 * 1024;

    public static GgufMetadata Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "GGUF") throw new InvalidDataException("Not a GGUF file.");
        var version = reader.ReadUInt32();
        if (version is < 2 or > 3) throw new InvalidDataException($"Unsupported GGUF version {version}.");
        _ = reader.ReadUInt64(); // tensor count; tensor data is intentionally not read
        var count = reader.ReadUInt64();
        if (count > MaxMetadataEntries) throw new InvalidDataException("GGUF metadata entry count is unreasonable.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            var key = ReadString(reader);
            var type = reader.ReadUInt32();
            values[key] = ReadValue(reader, type, keepValue: true);
        }
        return new(version, values);
    }

    static object? ReadValue(BinaryReader reader, uint type, bool keepValue) => type switch
    {
        0 => Keep(reader.ReadByte(), keepValue),
        1 => Keep(reader.ReadSByte(), keepValue),
        2 => Keep(reader.ReadUInt16(), keepValue),
        3 => Keep(reader.ReadInt16(), keepValue),
        4 => Keep(reader.ReadUInt32(), keepValue),
        5 => Keep(reader.ReadInt32(), keepValue),
        6 => Keep(reader.ReadSingle(), keepValue),
        7 => Keep(reader.ReadByte() != 0, keepValue),
        8 => keepValue ? ReadString(reader) : SkipString(reader),
        9 => ReadArray(reader),
        10 => Keep(reader.ReadUInt64(), keepValue),
        11 => Keep(reader.ReadInt64(), keepValue),
        12 => Keep(reader.ReadDouble(), keepValue),
        _ => throw new InvalidDataException($"Unknown GGUF metadata type {type}.")
    };

    static object? ReadArray(BinaryReader reader)
    {
        var itemType = reader.ReadUInt32();
        var count = reader.ReadUInt64();
        if (count > int.MaxValue) throw new InvalidDataException("GGUF metadata array is too large.");
        for (ulong i = 0; i < count; i++) ReadValue(reader, itemType, keepValue: false);
        return null;
    }

    static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > MaxStringBytes) throw new InvalidDataException("GGUF metadata string is too large.");
        var bytes = reader.ReadBytes((int)length);
        if ((ulong)bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    static object? SkipString(BinaryReader reader) { _ = ReadString(reader); return null; }
    static object? Keep<T>(T value, bool keep) where T : struct => keep ? value : null;
}
