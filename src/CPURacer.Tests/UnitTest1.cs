using CPURacer.Native;

namespace CPURacer.Tests;

public class CoordMapperTests
{
    [Fact]
    public void PixelsToDiu_At96Dpi_IsIdentity()
    {
        Assert.Equal(100, CoordMapper.PixelsToDiu(100, 96));
    }

    [Fact]
    public void PixelsToDiu_At144Dpi_ScalesByTwoThirds()
    {
        Assert.Equal(100, CoordMapper.PixelsToDiu(150, 144), 3);
    }

    [Fact]
    public void RectPixelsToDiu_MapsAllEdges()
    {
        var (l, t, w, h) = CoordMapper.RectPixelsToDiu(150, 300, 750, 450, 144);
        Assert.Equal(100, l, 3);
        Assert.Equal(200, t, 3);
        Assert.Equal(500, w, 3);
        Assert.Equal(300, h, 3);
    }

    [Fact]
    public void FrameYFromTop_RoundTrips_ToWorldY()
    {
        const int insetTop = 4;
        const int plotH = 100;
        const float yFromTop = 54f;
        var worldY = CoordMapper.FrameYFromTopToWorldY(yFromTop, insetTop, plotH);
        Assert.Equal(50f, worldY, precision: 3);
        Assert.Equal(yFromTop, CoordMapper.WorldYToFrameYFromTop(worldY, insetTop, plotH), precision: 3);
    }
}

public class NativeSmokeTests
{
    [Fact]
    public void NativeMethods_Type_IsLoadable()
    {
        Assert.NotNull(typeof(NativeMethods));
    }
}
