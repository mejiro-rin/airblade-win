using AirBlade.Interop;
using AirBlade.Models;
using AirBlade.Services;
using AirBlade.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AirBlade.Tests;

[TestClass]
public sealed class DeviceManagerViewModelTests
{
    private string _directory = null!;
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [TestInitialize]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), $"AirBlade.ViewModelTests.{Guid.NewGuid():N}");

    [TestCleanup]
    public void CleanUp()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task 设备变化会刷新快照并更新可读状态()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("living-room", "客厅", "192.168.1.10", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        using var viewModel = new DeviceManagerViewModel(manager);

        await manager.DiscoverAsync();

        var device = viewModel.Devices.Single();
        Assert.AreEqual("客厅", device.DisplayName);
        Assert.AreEqual("192.168.1.10:7000", device.Endpoint);
        Assert.AreEqual("已发现", device.DiscoveryStatusText);
        Assert.AreEqual("未连接", device.ConnectionStatusText);
    }

    [TestMethod]
    public async Task 快捷列表过滤隐藏设备且完整列表可以恢复显示()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("bedroom", "卧室", "192.168.1.20", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        using var quick = new DeviceManagerViewModel(manager);
        using var full = new DeviceManagerViewModel(manager, includeHidden: true);
        await manager.DiscoverAsync();

        var device = full.Devices.Single();
        await full.ToggleHiddenAsync(device);

        Assert.AreEqual(0, quick.Devices.Count);
        Assert.AreEqual(1, full.Devices.Count);
        Assert.IsTrue(full.Devices.Single().IsHidden);

        await full.ToggleHiddenAsync(full.Devices.Single());

        Assert.AreEqual(1, quick.Devices.Count);
        Assert.IsFalse(quick.Devices.Single().IsHidden);
    }

    [TestMethod]
    public async Task 命令根据连接状态更新可用性()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("office", "书房", "192.168.1.30", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        using var viewModel = new DeviceManagerViewModel(manager);
        await manager.DiscoverAsync();
        var device = viewModel.Devices.Single();

        Assert.IsTrue(viewModel.ConnectCommand.CanExecute(device));
        Assert.IsFalse(viewModel.DisconnectCommand.CanExecute(device));
        Assert.IsTrue(viewModel.SetVolumeCommand.CanExecute(device));
        Assert.IsTrue(device.CanAdjustVolume);

        await viewModel.ConnectAsync(device);

        Assert.IsFalse(viewModel.ConnectCommand.CanExecute(device));
        Assert.IsTrue(viewModel.DisconnectCommand.CanExecute(device));
        Assert.IsTrue(viewModel.SetVolumeCommand.CanExecute(device));
        Assert.IsTrue(device.CanAdjustVolume);

        await viewModel.DisconnectAsync(device);

        Assert.IsTrue(viewModel.ConnectCommand.CanExecute(device));
        Assert.IsFalse(viewModel.DisconnectCommand.CanExecute(device));
        Assert.IsTrue(viewModel.SetVolumeCommand.CanExecute(device));
        Assert.IsTrue(device.CanAdjustVolume);
    }

    [TestMethod]
    public async Task 操作异常同时展示在页面和设备行()
    {
        var native = new FakeNative { ConnectResult = -5 };
        native.DiscoveryRounds.Enqueue([new("kitchen", "厨房", "192.168.1.40", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        using var viewModel = new DeviceManagerViewModel(manager);
        await manager.DiscoverAsync();
        var device = viewModel.Devices.Single();

        await viewModel.ConnectAsync(device);

        Assert.IsNotNull(viewModel.ErrorMessage);
        Assert.IsNotNull(device.ErrorMessage);
        StringAssert.Contains(viewModel.ErrorMessage, "网络连接失败");
        StringAssert.Contains(device.ErrorMessage, "网络连接失败");
    }

    [TestMethod]
    public void 音量百分比与分贝转换_零为静音且一到一百映射有效范围()
    {
        // 0 保留给 AirPlay 静音哨兵 -144 dB。
        Assert.AreEqual(-144f, DeviceItemViewModel.ToVolumeDb(0));
        // 1-100 线性覆盖有效范围 -30.24..0 dB（滑块 16 对应的分贝值到 0）。
        Assert.AreEqual(-15.12f, DeviceItemViewModel.ToVolumeDb(50));
        Assert.AreEqual(0f, DeviceItemViewModel.ToVolumeDb(100));
        Assert.AreEqual(0d, DeviceItemViewModel.ToPercent(-144f), 0.001);
        Assert.AreEqual(50d, DeviceItemViewModel.ToPercent(-15.12f), 0.001);
        Assert.AreEqual(100d, DeviceItemViewModel.ToPercent(0f), 0.001);
        // 低于有效范围下限的旧音量统一显示为最小可听档 1。
        Assert.AreEqual(1d, DeviceItemViewModel.ToPercent(-30.24f), 0.001);
    }

    [TestMethod]
    public async Task 连接前预设音量只持久化不下发且默认值为百分之五十()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("office", "书房", "192.168.1.30", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        using var viewModel = new DeviceManagerViewModel(manager);
        await manager.DiscoverAsync();
        var device = viewModel.Devices.Single();

        Assert.AreEqual(DeviceItemViewModel.DefaultVolumePercent, device.EditableVolume);

        device.EditableVolume = 75;
        await viewModel.SetVolumeAsync(device);

        CollectionAssert.AreEqual(Array.Empty<float>(), native.SetVolumes);
        Assert.AreEqual(-7.56f, settings.GetDeviceSettings("office").VolumeDb);
        Assert.IsTrue(settings.GetDeviceSettings("office").RememberVolume);
    }

    [TestMethod]
    public async Task 发现扫描忙碌期间调音量会排队执行而不是被丢弃()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("office", "书房", "192.168.1.30", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        using var viewModel = new DeviceManagerViewModel(manager);
        await manager.DiscoverAsync();
        var device = viewModel.Devices.Single();

        // 先连接，确保音量会实际下发到会话（连接时会应用默认音量）。
        await viewModel.ConnectAsync(device);
        CollectionAssert.AreEqual(new[] { -15.12f }, native.SetVolumes);

        // 第二次扫描挂起，模拟发现正在执行（界面处于忙碌状态）。
        var gate = new TaskCompletionSource();
        native.DiscoveryRounds.Enqueue([new("office", "书房", "192.168.1.30", 7000)]);
        native.DiscoveryGate = gate.Task;
        var scanning = viewModel.DiscoverAsync();

        // 扫描期间调整音量：不能静默丢弃，应排队等扫描结束后再下发最新值。
        device.EditableVolume = 75;
        var volumeTask = viewModel.SetVolumeAsync(device);
        gate.SetResult();
        await volumeTask;
        await scanning;

        CollectionAssert.AreEqual(new[] { -15.12f, -7.56f }, native.SetVolumes);
        Assert.AreEqual(-7.56f, settings.GetDeviceSettings("office").VolumeDb);
    }

    [TestMethod]
    public async Task 自动连接开关更新会持久化并刷新界面状态()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("office", "书房", "192.168.1.30", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        using var viewModel = new DeviceManagerViewModel(manager);
        await manager.DiscoverAsync();
        var device = viewModel.Devices.Single();

        Assert.IsFalse(device.AutoConnect);
        await viewModel.SetAutoConnectAsync(device, true);

        Assert.IsTrue(settings.GetDeviceSettings("office").AutoConnect);
        Assert.IsTrue(device.AutoConnect);
    }

    [TestMethod]
    public async Task 记忆设备卡片渲染时滑块即显示记忆音量()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("office", "书房", "192.168.1.30", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await settings.InitializeAsync();
        await settings.UpdateDeviceSettingsAsync("office", value => value with { RememberVolume = true, VolumeDb = -6 });
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        using var viewModel = new DeviceManagerViewModel(manager, includeHidden: true);
        await manager.DiscoverAsync();

        var device = viewModel.Devices.Single();
        Assert.AreEqual(DeviceItemViewModel.ToPercent(-6f), device.EditableVolume, 0.001);
    }

    [TestMethod]
    public void 拖动音量时快照刷新不回写滑块()
    {
        var snapshot = new AirplayDeviceSnapshot("office", "书房", "192.168.1.30", 7000, AirplaySessionState.Idle, -6f, null, true, false, false);
        var device = new DeviceItemViewModel(snapshot);
        device.EditableVolume = 80;
        device.IsAdjustingVolume = true;

        // 拖动中即使收到旧音量快照，滑块也保持用户当前拖动位置。
        device.Update(snapshot with { VolumeDb = -6f }, isCurrentDevice: false);
        Assert.AreEqual(80, device.EditableVolume);

        // 松手后恢复同步，快照回写生效。
        device.IsAdjustingVolume = false;
        device.Update(snapshot with { VolumeDb = -6f }, isCurrentDevice: false);
        Assert.AreEqual(DeviceItemViewModel.ToPercent(-6f), device.EditableVolume, 0.001);
    }

    [TestMethod]
    public async Task 忘记设备后卡片从设备页消失且记忆被清除()
    {
        var native = new FakeNative();
        native.DiscoveryRounds.Enqueue([new("office", "书房", "192.168.1.30", 7000)]);
        await using var settings = new DeviceSettingsService(SettingsPath);
        await using var manager = CreateManager(settings, native);
        await manager.InitializeAsync();
        using var viewModel = new DeviceManagerViewModel(manager, includeHidden: true);
        await manager.DiscoverAsync();
        var device = viewModel.Devices.Single();

        await viewModel.RemoveDeviceAsync(device);

        Assert.AreEqual(0, viewModel.Devices.Count);
        Assert.IsNull(settings.GetDeviceSettings("office").Address);
    }

    private static AirplayDeviceManager CreateManager(DeviceSettingsService settings, FakeNative native) => new(settings, new AirplaySessionService(native));

    private sealed class FakeNative : IAirplayCoreNative
    {
        public Queue<IReadOnlyList<AirplayDiscoveredDevice>> DiscoveryRounds { get; } = new();
        public Task? DiscoveryGate { get; set; }
        public int ConnectResult { get; init; }
        public List<float> SetVolumes { get; } = [];
        public void EnsureLoaded() { }
        public ulong CreateSession() => 1;
        public int SetConnectionPolicy(ulong handle, AirplayConnectionPolicy policy) => 0;
        public int Connect(ulong handle, string endpoint) => ConnectResult;
        public int Disconnect(ulong handle) => 0;
        public int SetVolume(ulong handle, float volumeDb) { SetVolumes.Add(volumeDb); return 0; }
        public int PollEvent(ulong handle, out AirplayEvent airplayEvent) { airplayEvent = default; return 0; }
        public int Destroy(ulong handle) => 0;
        public int Discover(uint timeoutMs, AirplayTargetInfo[] targets, out nuint count)
        {
            DiscoveryGate?.GetAwaiter().GetResult();
            var results = DiscoveryRounds.Count > 0 ? DiscoveryRounds.Dequeue() : [];
            for (var index = 0; index < results.Count && index < targets.Length; index++)
            {
                targets[index].DeviceId = Text(results[index].DeviceId, 32);
                targets[index].DisplayName = Text(results[index].DisplayName, 128);
                targets[index].Address = Text(results[index].Address, 64);
                targets[index].Port = results[index].Port;
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
