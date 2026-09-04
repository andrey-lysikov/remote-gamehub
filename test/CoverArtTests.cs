//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using RemoteGameHub.Library;
using Xunit;

namespace RemoteGameHub.Tests;

// PadToAspect is the one thing standing between a cover of any shape and a client that either
// stretches or crops to fit its own tile: it must never trim a pixel, only add margin.
public class CoverArtTests
{
    [Theory]
    [InlineData(600, 900)]   // Steam's own portrait shape, narrower than 3:4
    [InlineData(460, 215)]   // a landscape header, used as a fallback when there is no portrait
    [InlineData(300, 400)]   // already the target shape: padding must be a no-op
    public void PadsToTheTargetAspectWithoutCroppingAnything(int width, int height)
    {
        using var original = new Bitmap(width, height);

        using var padded = CoverArt.PadToAspect(original, 3.0 / 4.0);

        Assert.True(padded.Width >= width);
        Assert.True(padded.Height >= height);
        Assert.Equal(3.0 / 4.0, padded.Width / (double)padded.Height, 2);
    }
}
