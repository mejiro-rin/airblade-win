using AirBlade.Interop;
using AirBlade.Models;
using AirBlade.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AirBlade.Tests;

[TestClass]
public sealed class AirplayDeviceManagerTests
{
    private string _directory = null!;
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [TestInitialize]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), $"AirBlade.ManagerTests.{Guid.NewGuid():N}");

    [TestCleanup]
    public void CleanUp()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task 发现按设备标识合并并更新地址()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.11", 7001)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await manager.DiscoverAsync();
        await manager.DiscoverAsync();

        var device = manager.GetDevices().Single();
        Assert.AreEqual("192.168.1.11", device.Address);
        Assert.AreEqual((ushort)7001, device.Port);
        Assert.AreEqual(0, native.ConnectCount);
        await settings.SaveAsync();
        Assert.IsFalse((await File.ReadAllTextAsync(SettingsPath)).Contains("device-1", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task 隐藏设备默认过滤但仍保留在运行时列表()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await settings.UpdateDeviceSettingsAsync("device-1", value => value with { Hidden = true });
        await manager.DiscoverAsync();

        Assert.AreEqual(0, manager.GetDevices().Count);
        Assert.AreEqual(1, manager.GetDevices(includeHidden: true).Count);
    }

    [TestMethod]
    public async Task 初始化和手动发现不会自动连接()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await settings.InitializeAsync();
        await settings.UpdateDeviceSettingsAsync("device-1", value => value with { AutoConnect = true });
        await using var manager = CreateManager(settings, native);

        await manager.InitializeAsync();
        await manager.DiscoverAsync();

        Assert.AreEqual(0, native.ConnectCount);
    }

    [TestMethod]
    public async Task 连接使用最新地址和设备连接策略并切换时先断开旧设备()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([
            new("device-1", "客厅", "192.168.1.10", 7000),
            new("device-2", "卧室", "192.168.1.20", 7001)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await settings.UpdateDeviceSettingsAsync("device-1", value => value with { ConnectionPolicy = AirplayConnectionPolicy.Automatic, RememberVolume = false });
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");
        await manager.ConnectAsync("device-2");

        CollectionAssert.AreEqual(new[] { "policy:Automatic", "connect:192.168.1.10:7000", "disconnect", "policy:Manual", "connect:192.168.1.20:7001" }, native.Operations);
        Assert.AreEqual("device-2", manager.CurrentConnectedDeviceId);
    }

    [TestMethod]
    public async Task 记忆音量时连接应用音量且用户修改会持久化()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using (var settings = new DeviceSettingsService(SettingsPath))
        {
            await using var manager = CreateManager(settings, native);
            await manager.InitializeAsync();
            await settings.UpdateDeviceSettingsAsync("device-1", value => value with { RememberVolume = true, VolumeDb = -10 });
            await manager.DiscoverAsync();
            await manager.ConnectAsync("device-1");
            await manager.SetVolumeAsync("device-1", -6);
            CollectionAssert.AreEqual(new[] { -10f, -6f }, native.SetVolumes);
        }
        await using var restored = new DeviceSettingsService(SettingsPath);
        await restored.InitializeAsync();
        Assert.AreEqual(-6f, restored.GetDeviceSettings("device-1").VolumeDb);
        Assert.IsNotNull(restored.GetDeviceSettings("device-1").LastConnectedAt);
    }

    [TestMethod]
    public async Task 连接时不应用未记忆预设但用户调节音量总是持久化()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await settings.UpdateDeviceSettingsAsync("device-1", value => value with { RememberVolume = false, VolumeDb = -10 });
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");
        await manager.SetVolumeAsync("device-1", -6);

        CollectionAssert.AreEqual(new[] { -6f }, native.SetVolumes);
        Assert.AreEqual(-6f, settings.GetDeviceSettings("device-1").VolumeDb);
        Assert.IsTrue(settings.GetDeviceSettings("device-1").RememberVolume);
    }

    [TestMethod]
    public async Task 会话事件同步到当前设备并通知变化()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        var changed = 0;
        manager.DevicesChanged += (_, _) => Interlocked.Increment(ref changed);
        await manager.InitializeAsync();
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");
        native.Events.Enqueue(new() { Kind = AirplayEventKind.State, State = AirplaySessionState.Streaming });
        native.Events.Enqueue(new() { Kind = AirplayEventKind.Volume, VolumeDb = -8 });
        native.Events.Enqueue(new() { Kind = AirplayEventKind.Error, ErrorCode = -5 });

        await WaitUntilAsync(() => manager.GetDevices().Single().ConnectionState == AirplaySessionState.Failed);
        var device = manager.GetDevices().Single();
        Assert.AreEqual(-8f, device.VolumeDb);
        Assert.IsNotNull(device.LastError);
        Assert.IsTrue(changed >= 4);
    }

    private static AirplayDeviceManager CreateManager(DeviceSettingsService settings, FakeNative native) => new(settings, new AirplaySessionService(native));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 50 && !predicate(); index++) await Task.Delay(20);
        Assert.IsTrue(predicate(), "等待会话事件超时。");
    }

    private sealed class FakeNative : IAirplayCoreNative
    {
        public Queue<IReadOnlyList<AirplayDiscoveredDevice>> DiscoveryRounds { get; } = new();
        public Queue<AirplayEvent> Events { get; } = new();
        public List<string> Operations { get; } = [];
        public List<float> SetVolumes { get; } = [];
        public int ConnectCount { get; private set; }
        public void EnsureLoaded() { }
        public ulong CreateSession() => 1;
        public int SetConnectionPolicy(ulong handle, AirplayConnectionPolicy policy) { Operations.Add($"policy:{policy}"); return 0; }
        public int Connect(ulong handle, string endpoint) { ConnectCount++; Operations.Add($"connect:{endpoint}"); return 0; }
        public int Disconnect(ulong handle) { Operations.Add("disconnect"); return 0; }
        public int SetVolume(ulong handle, float volumeDb) { SetVolumes.Add(volumeDb); return 0; }
        public int PollEvent(ulong handle, out AirplayEvent airplayEvent) { airplayEvent = Events.Count > 0 ? Events.Dequeue() : default; return 0; }
        public int Destroy(ulong handle) => 0;
        public int Discover(uint timeoutMs, AirplayTargetInfo[] targets, out nuint count)
        {
            var results = DiscoveryRounds.Count > 0 ? DiscoveryRounds.Dequeue() : [];
            for (var index = 0; index < results.Count && index < targets.Length; index++)
            {
                var result = results[index];
                targets[index].DeviceId = Text(result.DeviceId, 32);
                targets[index].DisplayName = Text(result.DisplayName, 128);
                targets[index].Address = Text(result.Address, 64);
                targets[index].Port = result.Port;
            }
            count = (nuint)Math.Min(results.Count, targets.Length);
            return 0;
        }

        private static byte[] Text(string value, int length)
        {
            var bytes = new byte[length];
            System.Text.Encoding.UTF8.GetBytes(value, bytes);
            return bytes;
        }
    }
}
