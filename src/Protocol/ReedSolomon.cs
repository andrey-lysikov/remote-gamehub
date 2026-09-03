//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub.Protocol;

// Reed-Solomon parity over GF(2⁸) reduced by 0x11D, matching the nanors library Moonlight links.
// Cauchy matrix P[j][i] = inverse((parityCount + i) XOR j), parity[j] = Σᵢ P[j][i]·data[i].
internal sealed class ReedSolomon
{
    private const int FieldPolynomial = 285;

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];
    private static readonly byte[] Inverse = new byte[256];

    // Every product in the field, 64 KB, built once: Product[(a << 8) | b] = a·b. The log/exp form
    // it replaces cost two lookups, an add and a branch per byte, at 440 M bytes a second at 4K120.
    private static readonly byte[] Product = new byte[256 * 256];

    static ReedSolomon()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= FieldPolynomial;
        }

        // The doubled tail lets a multiply skip the modulo on the exponent sum.
        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];

        Inverse[0] = 0;   // no inverse exists; the matrix never asks for it with valid shapes
        for (var i = 1; i < 256; i++) Inverse[i] = Exp[255 - Log[i]];

        for (var a = 1; a < 256; a++)
        for (var b = 1; b < 256; b++)
            Product[(a << 8) | b] = Exp[Log[a] + Log[b]];
    }

    private readonly int _dataShards;
    private readonly int _parityShards;
    private readonly byte[][] _matrix;

    // The audio codec: four data shards and two parity, with a matrix the formula above does not
    // produce — NVIDIA's audio FEC was built against OpenFEC, and clients expect these bytes.
    internal static ReedSolomon ForAudio() => new(4, 2, new[]
    {
        new byte[] { 0x77, 0x40, 0x38, 0x0E },
        new byte[] { 0xC7, 0xA7, 0x0D, 0x6C },
    });

    private ReedSolomon(int dataShards, int parityShards, byte[][] matrix)
    {
        _dataShards = dataShards;
        _parityShards = parityShards;
        _matrix = matrix;
    }

    internal ReedSolomon(int dataShards, int parityShards)
    {
        if (dataShards <= 0 || parityShards <= 0 || dataShards + parityShards > 255)
            throw new ArgumentOutOfRangeException(nameof(dataShards),
                "Reed-Solomon over GF(256) carries at most 255 shards in a block.");

        _dataShards = dataShards;
        _parityShards = parityShards;

        _matrix = new byte[parityShards][];
        for (var j = 0; j < parityShards; j++)
        {
            var row = new byte[dataShards];
            for (var i = 0; i < dataShards; i++)
                row[i] = Inverse[(parityShards + i) ^ j];
            _matrix[j] = row;
        }
    }

    // Fills shards[dataShards..] with parity over shards[0..dataShards). Every shard must be at
    // least blockSize bytes, and only the first blockSize bytes take part.
    internal void Encode(byte[][] shards, int blockSize)
    {
        if (shards.Length < _dataShards + _parityShards)
            throw new ArgumentException("not enough shards for this codec's shape", nameof(shards));

        for (var j = 0; j < _parityShards; j++)
        {
            var row = _matrix[j];
            var parity = shards[_dataShards + j];
            Array.Clear(parity, 0, blockSize);

            for (var i = 0; i < _dataShards; i++)
            {
                var coefficient = row[i];
                if (coefficient == 0) continue;

                var data = shards[i];
                if (coefficient == 1)
                {
                    for (var b = 0; b < blockSize; b++) parity[b] ^= data[b];
                    continue;
                }

                // parity ^= data · coefficient, one table lookup a byte. The row of the product
                // table is sliced out so the inner loop indexes it without the shift.
                var products = Product.AsSpan(coefficient << 8, 256);
                var source = data.AsSpan(0, blockSize);
                var target = parity.AsSpan(0, blockSize);

                for (var b = 0; b < blockSize; b++) target[b] ^= products[source[b]];
            }
        }
    }
}
