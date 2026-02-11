// unset

using System;

namespace Typhon.Schema.Definition;

// Ripped from here http://landman-code.blogspot.com/2009/02/c-superfasthash-and-murmurhash2.html
public unsafe static class MurmurHash2
{
    const uint M = 0x5bd1e995;
    const int  R = 24;

    public static uint Hash(byte[] data)
    {
        fixed (byte* a = data)
        {
            return Hash(a, data.Length, 0xc58f1a7b);
        }
    }

    public static uint Hash(ReadOnlySpan<byte> data)
    {
        fixed (byte* a = data)
        {
            return Hash(a, data.Length, 0xc58f1a7b);
        }
    }

    public static uint Hash(byte* dataAddr, int length, uint seed)
    {
        if (length == 0) return 0;
        uint h = seed ^ (uint)length;
        int remainingBytes = length & 3; // mod 4
        int numberOfLoops = length >> 2; // div 4
        byte* firstByte = dataAddr;
        uint* realData = (uint*)firstByte;
        while (numberOfLoops != 0)
        {
            uint k = *realData;
            k *= M;
            k ^= k >> R;
            k *= M;

            h *= M;
            h ^= k;
            numberOfLoops--;
            realData++;
        }
        switch (remainingBytes)
        {
            case 3:
                h ^= (ushort)(*realData);
                h ^= ((uint)(*(((byte*)(realData)) + 2))) << 16;
                h *= M;
                break;
            case 2:
                h ^= (ushort)(*realData);
                h *= M;
                break;
            case 1:
                h ^= *((byte*)realData);
                h *= M;
                break;
        }

        // Do a few final mixes of the hash to ensure the last few
        // bytes are well-incorporated.

        h ^= h >> 13;
        h *= M;
        h ^= h >> 15;

        return h;
    }
}
