using CPURacer.Native;

namespace CPURacer.Tests;

public class NativeSmokeTests
{
    [Fact]
    public void NativeMethods_Type_IsLoadable()
    {
        Assert.NotNull(typeof(NativeMethods));
    }
}
