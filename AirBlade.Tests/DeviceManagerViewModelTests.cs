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
    public void 音量百分比与分贝线性转换()
    {
        Assert.AreEqual(-144f, DeviceItemViewModel.ToVolumeDb(0));
        Assert.AreEqual(-72f, DeviceItemViewModel.ToVolumeDb(50));
        Assert.AreEqual(0f, DeviceItemViewModel.ToVolumeDb(100));
        Assert.AreEqual(0d, DeviceItemViewModel.ToPercent(-144f), 0.001);
        Assert.AreEqual(50d, DeviceItemViewModel.ToPercent(-72f), 0.001);
        Assert.AreEqual(100d, DeviceItemViewModel.ToPercent(0f), 0.001);
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
        Assert.AreEqual(-36f, settings.GetDeviceSettings("office").VolumeDb);
        Assert.IsTrue(settings.GetDeviceSettings("office").RememberVolume);
    }

    private static AirplayDeviceManager CreateManager(DeviceSettingsService settings, FakeNative native) => new(settings, new AirplaySessionService(native));

    private sealed class FakeNative : IAirplayCoreNative
    {
        public Queue<IReadOnlyList<AirplayDiscoveredDevice>> DiscoveryRounds { get; } = new();
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
