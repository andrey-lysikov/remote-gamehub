//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Media;
using RemoteGameHub.Native;
using Xunit;

namespace RemoteGameHub.Tests;

// The HDR conversion, as far as it goes without a graphics card: the shader is built by the
// compiler Windows ships, and the matrix behind it is arithmetic.
public class ColourConverterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_shader_compiles(bool fullRange)
    {
        var source = ColourConverter.Hlsl(fullRange);

        foreach (var (entry, target) in new[] { ("vs", "vs_5_0"), ("luma", "ps_5_0"),
                                                ("chroma", "ps_5_0") })
        {
            var bytecode = D3DCompiler.Compile(source, entry, target);
            Assert.NotEmpty(bytecode);
        }
    }

    // White is 940 of 1023 in limited range and 1023 in full, and neutral chroma is the middle of
    // the range either way. Both are the ten-bit codes divided by 1023, as a unorm target takes.
    [Theory]
    [InlineData(false, 940.0, 512.0)]
    [InlineData(true, 1023.0, 512.0)]
    public void White_and_neutral_land_where_the_standard_says(bool fullRange, double white,
                                                               double neutral)
    {
        var (y, u, v) = ColourConverter.Vectors(fullRange);

        Assert.Equal(white / 1023.0, y.R + y.G + y.B + y.Add, 6);

        // Chroma of any grey is the offset alone: the three coefficients of each vector sum to
        // zero, which is what makes a colourless pixel colourless.
        Assert.Equal(0.0, u.R + u.G + u.B, 9);
        Assert.Equal(0.0, v.R + v.G + v.B, 9);
        Assert.Equal(neutral / 1023.0, u.Add, 6);
        Assert.Equal(neutral / 1023.0, v.Add, 6);
    }

    // Red is all of the V vector's positive half and green is all of U's, which is the sign
    // convention: a mistake here shows as blue and red swapped in every stream.
    [Fact]
    public void The_chroma_vectors_point_the_way_the_standard_says()
    {
        var (_, u, v) = ColourConverter.Vectors(fullRange: false);

        Assert.True(u.B > 0 && u.R < 0 && u.G < 0);
        Assert.True(v.R > 0 && v.G < 0 && v.B < 0);
    }
}
