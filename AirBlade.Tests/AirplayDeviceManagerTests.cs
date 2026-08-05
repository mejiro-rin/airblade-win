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
    public async Task 连接成功后记忆设备信息且重启后离线恢复()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        var settingsPath = SettingsPath;
        await using (var settings = new DeviceSettingsService(settingsPath))
        {
            await using var manager = CreateManager(settings, native);
            await manager.InitializeAsync();
            await manager.DiscoverAsync();
            await manager.ConnectAsync("device-1");

            var remembered = settings.GetDeviceSettings("device-1");
            Assert.AreEqual("客厅", remembered.DisplayName);
            Assert.AreEqual("192.168.1.10", remembered.Address);
            Assert.AreEqual((ushort)7000, remembered.Port);
        }

        // 重启后不依赖发现结果，也能从配置恢复设备信息（离线状态）。
        await using var restored = new DeviceSettingsService(settingsPath);
        await using var restoredManager = CreateManager(restored, new FakeNative());
        await restoredManager.InitializeAsync();
        var device = restoredManager.GetDevices(includeHidden: true).Single();
        Assert.AreEqual("客厅", device.DisplayName);
        Assert.AreEqual("192.168.1.10", device.Address);
        Assert.AreEqual((ushort)7000, device.Port);
        Assert.IsFalse(device.IsDiscovered);
    }

    [TestMethod]
    public async Task 自动连接只连接已发现且开启自动连接的设备()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([
            new("device-1", "客厅", "192.168.1.10", 7000),
            new("device-2", "卧室", "192.168.1.20", 7001)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await settings.UpdateDeviceSettingsAsync("device-2", value => value with { AutoConnect = true, LastConnectedAt = DateTimeOffset.UtcNow });
        await manager.DiscoverAsync();

        Assert.IsTrue(await manager.AutoConnectAsync());
        Assert.AreEqual("device-2", manager.CurrentConnectedDeviceId);
        // 未开启自动连接的 device-1 不应被连接。
        Assert.AreEqual(1, native.ConnectCount);
    }

    [TestMethod]
    public async Task 离线记忆设备不触发自动连接()
    {
        await using var settings = new DeviceSettingsService(SettingsPath);
        await settings.InitializeAsync();
        await settings.UpdateDeviceSettingsAsync("device-1", value => value with
        {
            AutoConnect = true,
            DisplayName = "客厅",
            Address = "192.168.1.10",
            Port = 7000,
        });
        var native = new FakeNative();
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();

        Assert.IsFalse(await manager.AutoConnectAsync());
        Assert.AreEqual(0, native.ConnectCount);
    }

    [TestMethod]
    public async Task 记忆设备启动渲染即带记忆音量()
    {
        await using var settings = new DeviceSettingsService(SettingsPath);
        await settings.InitializeAsync();
        await settings.UpdateDeviceSettingsAsync("device-1", value => value with
        {
            DisplayName = "客厅",
            Address = "192.168.1.10",
            Port = 7000,
            RememberVolume = true,
            VolumeDb = -6,
        });
        await using var manager = CreateManager(settings, new FakeNative());
        await manager.InitializeAsync();

        var device = manager.GetDevices(includeHidden: true).Single();
        Assert.AreEqual(-6f, device.VolumeDb);
    }

    [TestMethod]
    public async Task 发现设备时即显示记忆音量()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await settings.InitializeAsync();
        await settings.UpdateDeviceSettingsAsync("device-1", value => value with { RememberVolume = true, VolumeDb = -6 });
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await manager.DiscoverAsync();

        var device = manager.GetDevices().Single();
        Assert.AreEqual(-6f, device.VolumeDb);
    }

    [TestMethod]
    public async Task 忘记设备会断开并删除运行时条目与记忆()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");
        await manager.RemoveDeviceAsync("device-1");

        Assert.AreEqual(0, manager.GetDevices(includeHidden: true).Count);
        Assert.IsNull(manager.CurrentConnectedDeviceId);
        Assert.IsNull(settings.GetDeviceSettings("device-1").Address);
        StringAssert.Contains(string.Join("\n", native.Operations), "disconnect");
    }

    [TestMethod]
    public async Task 连接成功会清除此前残留的错误()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");

        // 播放中出错，错误应挂到当前设备卡片上（真实场景引擎在连接后才运行）。
        native.Events.Enqueue(new() { Kind = AirplayEventKind.Error, ErrorCode = -5 });
        await WaitUntilAsync(() => manager.GetDevices().Single().LastError is not null);
        Assert.IsNotNull(manager.GetDevices().Single().LastError);

        // 重新连接成功后，旧错误不应继续挂在设备卡片上。
        await manager.ConnectAsync("device-1");
        Assert.IsNull(manager.GetDevices().Single().LastError);
    }

    [TestMethod]
    public async Task 会话恢复活跃状态会清除残留错误()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");

        native.Events.Enqueue(new() { Kind = AirplayEventKind.Error, ErrorCode = -5 });
        await WaitUntilAsync(() => manager.GetDevices().Single().LastError is not null);
        Assert.IsNotNull(manager.GetDevices().Single().LastError);

        // 会话重新进入活跃状态（Streaming）时，旧错误自动清除。
        native.Events.Enqueue(new() { Kind = AirplayEventKind.State, State = AirplaySessionState.Streaming });
        await WaitUntilAsync(() => manager.GetDevices().Single().LastError is null);
        Assert.AreEqual(AirplaySessionState.Streaming, manager.GetDevices().Single().ConnectionState);
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

    [TestMethod]
    public async Task 意外断联后保留会话关联且错误挂在设备上()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");
        Assert.AreEqual("device-1", manager.CurrentConnectedDeviceId);

        // 播放中意外断联：原生层发错误事件，会话进入失败状态。
        native.Events.Enqueue(new() { Kind = AirplayEventKind.Error, ErrorCode = -5 });
        await WaitUntilAsync(() => manager.GetDevices().Single().ConnectionState == AirplaySessionState.Failed);

        var device = manager.GetDevices().Single();
        Assert.AreEqual(AirplaySessionState.Failed, device.ConnectionState);
        Assert.IsNotNull(device.LastError);
        // 会话仍关联该设备，自动重连恢复后状态可以继续回流到卡片。
        Assert.AreEqual("device-1", manager.CurrentConnectedDeviceId);
    }

    [TestMethod]
    public async Task 开关开启时连接成功自动静音且断开恢复()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        var mute = new FakeSystemMuteController();
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native, mute);
        await manager.InitializeAsync();
        await settings.UpdateGlobalSettingsAsync(value => value with { MuteComputerWhenConnected = true });
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");

        native.Events.Enqueue(new() { Kind = AirplayEventKind.State, State = AirplaySessionState.Streaming });
        await WaitUntilAsync(() => mute.Calls.Count > 0);
        CollectionAssert.AreEqual(new[] { true }, mute.Calls);

        await manager.DisconnectAsync("device-1");
        CollectionAssert.AreEqual(new[] { true, false }, mute.Calls);
    }

    [TestMethod]
    public async Task 开关开启时意外断联自动恢复静音()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        var mute = new FakeSystemMuteController();
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native, mute);
        await manager.InitializeAsync();
        await settings.UpdateGlobalSettingsAsync(value => value with { MuteComputerWhenConnected = true });
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");
        native.Events.Enqueue(new() { Kind = AirplayEventKind.State, State = AirplaySessionState.Streaming });
        await WaitUntilAsync(() => mute.Calls.Count > 0);

        native.Events.Enqueue(new() { Kind = AirplayEventKind.Error, ErrorCode = -5 });
        await WaitUntilAsync(() => mute.Calls.Count > 1);
        CollectionAssert.AreEqual(new[] { true, false }, mute.Calls);
    }

    [TestMethod]
    public async Task 退出时自动恢复本功能造成的静音()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        var mute = new FakeSystemMuteController();
        await using var settings = new DeviceSettingsService(SettingsPath);
        var manager = CreateManager(settings, native, mute);
        await manager.InitializeAsync();
        await settings.UpdateGlobalSettingsAsync(value => value with { MuteComputerWhenConnected = true });
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");
        native.Events.Enqueue(new() { Kind = AirplayEventKind.State, State = AirplaySessionState.Streaming });
        await WaitUntilAsync(() => mute.Calls.Count > 0);

        await manager.DisposeAsync();
        CollectionAssert.AreEqual(new[] { true, false }, mute.Calls);
    }

    [TestMethod]
    public async Task 开关关闭时连接成功不静音()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        var mute = new FakeSystemMuteController();
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native, mute);
        await manager.InitializeAsync();
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");

        native.Events.Enqueue(new() { Kind = AirplayEventKind.State, State = AirplaySessionState.Streaming });
        await WaitUntilAsync(() => manager.GetDevices().Single().ConnectionState == AirplaySessionState.Streaming);
        Assert.AreEqual(0, mute.Calls.Count);
    }

    [TestMethod]
    public async Task 连接前电脑已静音时不接管静音()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        var mute = new FakeSystemMuteController { InitialMuted = true };
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native, mute);
        await manager.InitializeAsync();
        await settings.UpdateGlobalSettingsAsync(value => value with { MuteComputerWhenConnected = true });
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");

        native.Events.Enqueue(new() { Kind = AirplayEventKind.State, State = AirplaySessionState.Streaming });
        await WaitUntilAsync(() => manager.GetDevices().Single().ConnectionState == AirplaySessionState.Streaming);
        Assert.AreEqual(0, mute.Calls.Count);
    }

    [TestMethod]
    public async Task 远端音量事件会持久化供下次启动恢复()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("device-1", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        await manager.DiscoverAsync();
        await manager.ConnectAsync("device-1");
        native.Events.Enqueue(new() { Kind = AirplayEventKind.Volume, VolumeDb = -30 });

        await WaitUntilAsync(() => settings.GetDeviceSettings("device-1").VolumeDb == -30f);
        Assert.IsTrue(settings.GetDeviceSettings("device-1").RememberVolume);
    }

    private static AirplayDeviceManager CreateManager(DeviceSettingsService settings, FakeNative native) => new(settings, new AirplaySessionService(native));
    private static AirplayDeviceManager CreateManager(DeviceSettingsService settings, FakeNative native, ISystemMuteController systemMute) => new(settings, new AirplaySessionService(native), systemMute);

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

    private sealed class FakeSystemMuteController : ISystemMuteController
    {
        public bool? InitialMuted { get; set; } = false;
        public List<bool> Calls { get; } = [];
        public bool? GetMuted() => InitialMuted;
        public void SetMuted(bool muted) => Calls.Add(muted);
    }
}
