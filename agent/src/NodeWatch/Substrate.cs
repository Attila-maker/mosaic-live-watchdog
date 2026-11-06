using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace NodeWatch;

/// <summary>xxHash64 (the "twox" hasher of Substrate storage keys).</summary>
internal static class XxHash64
{
    private const ulong P1 = 0x9E3779B185EBCA87UL, P2 = 0xC2B2AE3D27D4EB4FUL, P3 = 0x165667B19E3779F9UL,
        P4 = 0x85EBCA77C2B2AE63UL, P5 = 0x27D4EB2F165667C5UL;

    public static ulong Hash(ReadOnlySpan<byte> input, ulong seed)
    {
        int len = input.Length, i = 0;
        ulong h;
        if (len >= 32)
        {
            ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            while (i <= len - 32)
            {
                v1 = Round(v1, Read64(input, i));
                v2 = Round(v2, Read64(input, i + 8));
                v3 = Round(v3, Read64(input, i + 16));
                v4 = Round(v4, Read64(input, i + 24));
                i += 32;
            }
            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) +
                BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            h = Merge(h, v1);
            h = Merge(h, v2);
            h = Merge(h, v3);
            h = Merge(h, v4);
        }
        else
        {
            h = seed + P5;
        }
        h += (ulong)len;
        while (i + 8 <= len)
        {
            h ^= Round(0, Read64(input, i));
            h = BitOperations.RotateLeft(h, 27) * P1 + P4;
            i += 8;
        }
        if (i + 4 <= len)
        {
            h ^= BinaryPrimitives.ReadUInt32LittleEndian(input[i..]) * P1;
            h = BitOperations.RotateLeft(h, 23) * P2 + P3;
            i += 4;
        }
        while (i < len)
        {
            h ^= input[i] * P5;
            h = BitOperations.RotateLeft(h, 11) * P1;
            i++;
        }
        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return h;
    }

    private static ulong Read64(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt64LittleEndian(s[i..]);

    private static ulong Round(ulong acc, ulong input)
    {
        acc += input * P2;
        acc = BitOperations.RotateLeft(acc, 31);
        return acc * P1;
    }

    private static ulong Merge(ulong acc, ulong val)
    {
        acc ^= Round(0, val);
        return acc * P1 + P4;
    }
}

/// <summary>BLAKE2b (RFC 7693), unkeyed. Only needed for the SS58 address checksum.</summary>
internal static class Blake2b
{
    private static readonly ulong[] IV =
    [
        0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL, 0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
        0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL, 0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL,
    ];

    private static readonly byte[][] Sigma =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
        [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4],
        [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
        [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13],
        [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
        [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11],
        [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
        [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5],
        [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0],
    ];

    public static byte[] Hash(ReadOnlySpan<byte> data, int outLen = 64)
    {
        if (outLen is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(outLen));
        var h = (ulong[])IV.Clone();
        h[0] ^= 0x01010000UL ^ (ulong)outLen;
        var block = new byte[128];
        ulong counter = 0;
        var offset = 0;
        // Every full block but the last is compressed as "not last".
        while (data.Length - offset > 128)
        {
            counter += 128;
            Compress(h, data.Slice(offset, 128), counter, last: false);
            offset += 128;
        }
        var rest = data.Length - offset;
        Array.Clear(block);
        data.Slice(offset, rest).CopyTo(block);
        counter += (ulong)rest;
        Compress(h, block, counter, last: true);
        var output = new byte[64];
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(i * 8), h[i]);
        return output[..outLen];
    }

    private static void Compress(ulong[] h, ReadOnlySpan<byte> block, ulong counter, bool last)
    {
        Span<ulong> m = stackalloc ulong[16];
        for (var i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);
        Span<ulong> v = stackalloc ulong[16];
        for (var i = 0; i < 8; i++)
        {
            v[i] = h[i];
            v[i + 8] = IV[i];
        }
        v[12] ^= counter; // the high 64 bits of the counter stay 0 for anything we hash
        if (last) v[14] = ~v[14];
        for (var r = 0; r < 12; r++)
        {
            var s = Sigma[r % 10];
            G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }
        for (var i = 0; i < 8; i++) h[i] ^= v[i] ^ v[i + 8];
    }

    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}

internal static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static byte[] Decode(string text)
    {
        BigInteger value = 0;
        foreach (var c in text)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0) throw new FormatException($"érvénytelen base58 karakter: '{c}'");
            value = value * 58 + digit;
        }
        var bytes = value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = text.TakeWhile(c => c == '1').Count();
        return [.. new byte[leadingZeros], .. bytes];
    }
}

/// <summary>SS58 addresses (Substrate account addresses).</summary>
internal static class Ss58
{
    /// <summary>The 32-byte public key from an SS58 address (any network prefix) or from "0x" + 64 hex digits.</summary>
    public static byte[] DecodePublicKey(string address)
    {
        address = address.Trim();
        if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (address.Length != 66) throw new FormatException("a 0x formájú nyilvános kulcs 64 hexa számjegy legyen");
            try { return Convert.FromHexString(address[2..]); }
            catch (FormatException) { throw new FormatException("a nyilvános kulcs nem érvényes hexa szám"); }
        }
        var data = Base58.Decode(address);
        if (data.Length == 0) throw new FormatException("üres cím");
        var prefixLength = data[0] < 64 ? 1 : data[0] < 128 ? 2 : throw new FormatException("ismeretlen SS58 előtag");
        if (data.Length != prefixLength + 32 + 2) throw new FormatException("a cím hossza nem egy 32 bájtos fiókhoz tartozik");
        var checksum = Blake2b.Hash([.. "SS58PRE"u8, .. data.AsSpan(0, data.Length - 2)]);
        if (checksum[0] != data[^2] || checksum[1] != data[^1]) throw new FormatException("hibás ellenőrzőösszeg (elírt cím?)");
        return data[prefixLength..(prefixLength + 32)];
    }
}

internal enum ValidatorStateKind { Normal, Faulted, Chilled }

internal sealed record ValidatorState(ValidatorStateKind Kind, uint? ChilledSession)
{
    /// <summary>The SCALE value of pallet-nft-staking's <c>ValidatorState</c> enum: Normal, Faulted, Chilled(u32).</summary>
    public static ValidatorState Decode(ReadOnlySpan<byte> value) => value.Length switch
    {
        1 when value[0] == 0 => new(ValidatorStateKind.Normal, null),
        1 when value[0] == 1 => new(ValidatorStateKind.Faulted, null),
        5 when value[0] == 2 => new(ValidatorStateKind.Chilled, BinaryPrimitives.ReadUInt32LittleEndian(value[1..])),
        _ => throw new FormatException($"ismeretlen ValidatorState érték: 0x{Convert.ToHexStringLower(value)}"),
    };
}

internal static class StorageKeys
{
    public static byte[] Twox128(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var key = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(key, XxHash64.Hash(bytes, 0));
        BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), XxHash64.Hash(bytes, 1));
        return key;
    }

    public static byte[] Twox64Concat(ReadOnlySpan<byte> data)
    {
        var key = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(key, XxHash64.Hash(data, 0));
        data.CopyTo(key.AsSpan(8));
        return key;
    }

    /// <summary>NftStaking.ValidatorStates(account): twox128(pallet) ++ twox128(item) ++ twox64(account) ++ account.</summary>
    public static string ValidatorState(byte[] accountId) =>
        "0x" + Convert.ToHexStringLower([.. Twox128("NftStaking"), .. Twox128("ValidatorStates"), .. Twox64Concat(accountId)]);
}
