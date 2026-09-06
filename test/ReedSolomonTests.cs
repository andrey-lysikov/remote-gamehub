//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// The parity the client rebuilds lost packets from. It has to match nanors byte for byte, and
// the reference case here is the one that was checked against the real library.
public class ReedSolomonTests
{
    [Fact]
    public void The_reference_case_matches_nanors()
    {
        const int dataShards = 5, parityShards = 3, blockSize = 16;

        var shards = new byte[dataShards + parityShards][];
        for (var i = 0; i < shards.Length; i++) shards[i] = new byte[blockSize];
        for (var i = 0; i < dataShards; i++)
            for (var b = 0; b < blockSize; b++)
                shards[i][b] = (byte)(i * 37 + b * 11 + 7);

        new ReedSolomon(dataShards, parityShards).Encode(shards, blockSize);

        Assert.Equal("f2a6b19025a802d688fbdcc6c18636f9",
                     Convert.ToHexString(shards[dataShards]).ToLowerInvariant());
    }

    [Fact]
    public void Parity_is_linear_so_a_zero_block_gives_zero_parity()
    {
        var shards = Enumerable.Range(0, 6).Select(_ => new byte[8]).ToArray();

        new ReedSolomon(4, 2).Encode(shards, 8);

        Assert.All(shards.Skip(4), parity => Assert.All(parity, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void Only_the_block_takes_part()
    {
        var shards = Enumerable.Range(0, 3).Select(_ => new byte[16]).ToArray();
        shards[0][15] = 0xFF;   // past the block: must not reach the parity

        new ReedSolomon(2, 1).Encode(shards, 8);

        Assert.All(shards[2].Take(8), b => Assert.Equal(0, b));
    }

    [Fact]
    public void The_audio_codec_uses_the_fixed_matrix()
    {
        // Four data shards, two parity; the matrix is the eight bytes both clients hard-code.
        // With one data shard set to 1 and the rest zero, each parity byte is the coefficient.
        var shards = Enumerable.Range(0, 6).Select(_ => new byte[1]).ToArray();
        shards[0][0] = 1;

        ReedSolomon.ForAudio().Encode(shards, 1);

        Assert.Equal(0x77, shards[4][0]);
        Assert.Equal(0xC7, shards[5][0]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(200, 56)]
    public void Impossible_shapes_are_refused(int data, int parity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomon(data, parity));
    }

    [Fact]
    public void Too_few_shards_are_refused()
    {
        var shards = Enumerable.Range(0, 2).Select(_ => new byte[4]).ToArray();
        Assert.Throws<ArgumentException>(() => new ReedSolomon(2, 1).Encode(shards, 4));
    }
}
