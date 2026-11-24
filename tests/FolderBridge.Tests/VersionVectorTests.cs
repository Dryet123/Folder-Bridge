using FolderBridge.Core;

namespace FolderBridge.Tests;

public class VersionVectorTests
{
    [Fact]
    public void IncrementMakesNewerVersion()
    {
        var a = VersionVector.Empty.Increment("a");
        var b = a.Increment("a");
        Assert.Equal(Ordering.Greater, b.Compare(a));
        Assert.Equal(Ordering.Lesser, a.Compare(b));
        Assert.Equal(Ordering.Equal, a.Compare(VersionVector.Empty.Increment("a")));
    }

    [Fact]
    public void ChangesOnTwoDevicesAreConcurrent()
    {
        var shared = VersionVector.Empty.Increment("a");
        var onA = shared.Increment("a");
        var onB = shared.Increment("b");
        Assert.Equal(Ordering.Concurrent, onA.Compare(onB));
        var merged = onA.Merge(onB);
        Assert.Equal(Ordering.Greater, merged.Compare(onA));
        Assert.Equal(Ordering.Greater, merged.Compare(onB));
    }

    [Fact]
    public void PairingCodeIsTheSameOnBothSides()
    {
        var a = new DeviceId(new string('A', 52));
        var b = new DeviceId(new string('B', 52));
        byte[] na = [1, 2, 3], nb = [4, 5, 6];
        var code = Identity.PairingCode(a, b, na, nb);
        Assert.Equal(code, Identity.PairingCode(b, a, nb, na));
        Assert.Matches(@"^\d{3} \d{3}$", code);
        Assert.NotEqual(code, Identity.PairingCode(a, b, nb, na));
    }
}
