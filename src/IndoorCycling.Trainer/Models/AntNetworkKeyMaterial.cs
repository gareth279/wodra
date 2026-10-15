using System.Reflection;

namespace IndoorCycling.Trainer.Models;

/// <summary>Loads ANT+ network material embedded by the private Windows release build.</summary>
public static class AntNetworkKeyMaterial
{
    public const string EmbeddedResourceName = "Wodra.AntPlusNetworkKey";

    public static byte[] LoadEmbedded(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using var resource = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                "ANT+ FE-C runtime configuration is missing from this build. Rebuild with the private ANT+ network-key configuration.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var material = buffer.ToArray();
        try { return Parse(material); }
        finally { Array.Clear(material, 0, material.Length); }
    }

    /// <summary>Accepts the raw eight-byte key or the 16 hexadecimal digits supplied by ANT materials.</summary>
    public static byte[] Parse(ReadOnlySpan<byte> material)
    {
        if (material.Length == 8) return material.ToArray();
        if (material.Length is < 1 or > 128)
            throw new InvalidDataException("ANT+ FE-C runtime configuration must contain an eight-byte key.");

        var source = material;
        if (source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF)
            source = source[3..];
        var key = new byte[8];
        if (source.Length == 16)
        {
            var contiguousHex = true;
            for (var index = 0; index < source.Length; index++)
                contiguousHex &= HexValue(source[index]) >= 0;
            if (contiguousHex)
            {
                for (var index = 0; index < key.Length; index++)
                    key[index] = (byte)((HexValue(source[index * 2]) << 4) | HexValue(source[(index * 2) + 1]));
                return key;
            }
        }
        var position = 0;
        for (var index = 0; index < key.Length; index++)
        {
            SkipSeparators(source, ref position);
            if (position + 1 < source.Length && source[position] == '0' &&
                source[position + 1] is (byte)'x' or (byte)'X') position += 2;
            if (position + 1 >= source.Length)
            {
                Array.Clear(key, 0, key.Length);
                throw new InvalidDataException("ANT+ FE-C runtime configuration must contain an eight-byte key.");
            }
            var high = HexValue(source[position]);
            var low = HexValue(source[position + 1]);
            if (high < 0 || low < 0)
            {
                Array.Clear(key, 0, key.Length);
                throw new InvalidDataException("ANT+ FE-C runtime configuration must contain an eight-byte key.");
            }
            key[index] = (byte)((high << 4) | low);
            position += 2;
            if (position < source.Length && !IsSeparator(source[position]))
            {
                Array.Clear(key, 0, key.Length);
                throw new InvalidDataException("ANT+ FE-C runtime configuration must contain an eight-byte key.");
            }
        }
        SkipSeparators(source, ref position);
        if (position != source.Length)
        {
            Array.Clear(key, 0, key.Length);
            throw new InvalidDataException("ANT+ FE-C runtime configuration must contain an eight-byte key.");
        }
        return key;
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => -1
    };

    private static bool IsSeparator(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or
        (byte)'\n' or (byte)',' or (byte)':' or (byte)'-';

    private static void SkipSeparators(ReadOnlySpan<byte> source, ref int position)
    {
        while (position < source.Length && IsSeparator(source[position])) position++;
    }
}
