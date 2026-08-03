using AirBlade.Interop;
using AirBlade.Models;
using AirBlade.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AirBlade.Tests;

[TestClass]
public sealed class DeviceSettingsServiceTests
{
    private string _directory = null!;
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [TestInitialize]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), $"AirBlade.Tests.{Guid.NewGuid():N}");

    [TestCleanup]
    public void CleanUp()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task 新建配置使用安全默认值()
    {
        await using var service = new DeviceSettingsService(SettingsPath);
        await service.InitializeAsync();
        var global = service.GetGlobalSettings();
        var device = service.GetDeviceSettings("device-1");
        Assert.IsFalse(device.AutoConnect);
        Assert.AreEqual(AirplayConnectionPolicy.Manual, device.ConnectionPolicy);
        Assert.IsTrue(device.RememberVolume);
        Assert.AreEqual(-72f, device.VolumeDb);
        Assert.IsFalse(global.StartupEnabled);
        Assert.AreEqual(TimeSpan.FromSeconds(2), global.DiscoveryTimeout);
        Assert.IsFalse(File.Exists(SettingsPath));
    }

    [TestMethod]
    public async Task 保存后可恢复设备与全局配置()
    {
        var connectedAt = DateTimeOffset.UtcNow;
        await using (var service = new DeviceSettingsService(SettingsPath))
        {
            await service.InitializeAsync();
            await service.UpdateGlobalSettingsAsync(value => value with { StartupEnabled = true, LoggingEnabled = true, DefaultVolumeDb = -12, DiscoveryTimeout = TimeSpan.FromSeconds(8) });
            await service.UpdateDeviceSettingsAsync("device-1", value => value with { AutoConnect = true, ConnectionPolicy = AirplayConnectionPolicy.Automatic, RememberVolume = false, VolumeDb = -6, Hidden = true, LastConnectedAt = connectedAt });
        }
        await using var restored = new DeviceSettingsService(SettingsPath);
        await restored.InitializeAsync();
        var global = restored.GetGlobalSettings();
        var device = restored.GetDeviceSettings("device-1");
        Assert.IsTrue(global.StartupEnabled);
        Assert.IsTrue(global.LoggingEnabled);
        Assert.AreEqual(-12f, global.DefaultVolumeDb);
        Assert.AreEqual(TimeSpan.FromSeconds(8), global.DiscoveryTimeout);
        Assert.IsTrue(device.AutoConnect);
        Assert.AreEqual(AirplayConnectionPolicy.Automatic, device.ConnectionPolicy);
        Assert.IsFalse(device.RememberVolume);
        Assert.AreEqual(-6f, device.VolumeDb);
        Assert.IsTrue(device.Hidden);
        Assert.AreEqual(connectedAt, device.LastConnectedAt);
    }

    [TestMethod]
    public async Task 同一设备标识的地址变化不会创建配置()
    {
        await using var service = new DeviceSettingsService(SettingsPath);
        await service.InitializeAsync();
        var first = service.ApplyDiscoveredDevice(new("device-1", "客厅", "192.168.1.10", 7000));
        var second = service.ApplyDiscoveredDevice(new("device-1", "客厅", "192.168.1.11", 7001));
        Assert.AreSame(first, second);
        Assert.AreEqual("192.168.1.11", second.Address);
        Assert.AreEqual((ushort)7001, second.Port);
        await service.SaveAsync();
        Assert.IsFalse((await File.ReadAllTextAsync(SettingsPath)).Contains("device-1", StringComparison.Ordinal));
        await using var restored = new DeviceSettingsService(SettingsPath);
        await restored.InitializeAsync();
        Assert.IsFalse(restored.GetDeviceSettings("device-1").AutoConnect);
    }

    [TestMethod]
    public async Task 损坏配置会备份并回退默认值()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, "{ invalid json");
        await using var service = new DeviceSettingsService(SettingsPath);
        await service.InitializeAsync();
        Assert.IsFalse(service.GetDeviceSettings("device-1").AutoConnect);
        Assert.IsFalse(File.Exists(SettingsPath));
        Assert.AreEqual(1, Directory.GetFiles(_directory, "settings.json.corrupt-*.json").Length);
    }

    [TestMethod]
    public async Task 并发更新不同设备不会丢失配置()
    {
        await using (var service = new DeviceSettingsService(SettingsPath))
        {
            await service.InitializeAsync();
            await Task.WhenAll(
                service.UpdateDeviceSettingsAsync("device-1", value => value with { Hidden = true }),
                service.UpdateDeviceSettingsAsync("device-2", value => value with { VolumeDb = -8 }));
        }
        await using var restored = new DeviceSettingsService(SettingsPath);
        await restored.InitializeAsync();
        Assert.IsTrue(restored.GetDeviceSettings("device-1").Hidden);
        Assert.AreEqual(-8f, restored.GetDeviceSettings("device-2").VolumeDb);
    }
}
