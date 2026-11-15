using System.Security.Cryptography;
using System.Text;
using BetterGenshinImpact.Service.AccountRecords;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

/// <summary>在独立临时目录验证名称缓存与旧版本图标接续。</summary>
public sealed class InventoryIconCacheTests : IDisposable
{
    /// <summary>本测试独占的目录，不访问实际用户缓存。</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "InventoryIconCacheTests", Guid.NewGuid().ToString("N"));

    /// <summary>图标跨服务实例复用；后续出现 CSV 版本目录也不会切换或覆盖当前图标。</summary>
    [Fact]
    public void Store_ReusesStableNamePathAndLoadsFrozenImage()
    {
        using var input = new Mat(20, 40, MatType.CV_8UC3, Scalar.All(128));
        Cv2.ImEncode(".png", input, out var bytes);
        var cache = new InventoryIconCache(_root);
        cache.Store("摩拉", bytes);
        var fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("摩拉"))) + ".png";
        var stored = File.ReadAllBytes(Path.Combine(_root, "icons", fileName));
        var legacy = Path.Combine(_root, "icons", "999999");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, fileName), "stale");
        var reopened = new InventoryIconCache(_root);
        var image = reopened.Load("摩拉");
        Assert.NotNull(image);
        Assert.True(image.IsFrozen);
        Assert.Equal(36, image.PixelWidth);
        Assert.Equal(36, image.PixelHeight);
        Assert.Equal(stored, File.ReadAllBytes(Path.Combine(_root, "icons", fileName)));
        reopened.Clear();
        Assert.False(reopened.Exists("摩拉"));
    }

    /// <summary>释放本测试创建的临时数据。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
