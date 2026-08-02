using System.Text.Json;
using System.Text.Json.Serialization;
using AirBlade.Interop;
using AirBlade.Models;

namespace AirBlade.Services;

/// <summary>
/// 负责保存用户配置，并维护由发现结果生成的运行时设备状态。
/// </summary>
public sealed class DeviceSettingsService : IAsyncDisposable
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly string _settingsPath;
    private SettingsDocument _document = CreateDefaultDocument();
    private readonly Dictionary<string, AirplayDevice> _devices = new(StringComparer.Ordinal);
    private int _disposed;

    public DeviceSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AirBlade",
            "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default) => await LoadAsync(cancellationToken).ConfigureAwait(false);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SettingsDocument loaded;
            if (!File.Exists(_settingsPath))
            {
                loaded = CreateDefaultDocument();
            }
            else
            {
                try
                {
                    await using var stream = File.OpenRead(_settingsPath);
                    loaded = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                        ?? throw new JsonException("配置文件为空。");
                    Validate(loaded);
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidDataException or ArgumentException)
                {
                    PreserveCorruptFile();
                    loaded = CreateDefaultDocument();
                }
            }

            lock (_stateGate) _document = loaded;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public DeviceSettings GetDeviceSettings(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();
        lock (_stateGate)
            return _document.DeviceSettings.TryGetValue(deviceId, out var settings) ? settings : new DeviceSettings();
    }

    public async Task UpdateDeviceSettingsAsync(string deviceId, Func<DeviceSettings, DeviceSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(update);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                var current = _document.DeviceSettings.TryGetValue(deviceId, out var value) ? value : new DeviceSettings();
                var updated = update(current) ?? throw new ArgumentException("设备配置更新不能返回 null。", nameof(update));
                ValidateDeviceSettings(updated);
                _document.DeviceSettings[deviceId] = updated;
            }
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public GlobalSettings GetGlobalSettings()
    {
        ThrowIfDisposed();
        lock (_stateGate) return _document.GlobalSettings;
    }

    public async Task UpdateGlobalSettingsAsync(Func<GlobalSettings, GlobalSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                var updated = update(_document.GlobalSettings) ?? throw new ArgumentException("全局配置更新不能返回 null。", nameof(update));
                ValidateGlobalSettings(updated);
                _document.GlobalSettings = updated;
            }
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public AirplayDevice ApplyDiscoveredDevice(AirplayDiscoveredDevice discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        if (string.IsNullOrWhiteSpace(discovered.DeviceId)) throw new ArgumentException("发现结果缺少设备标识。", nameof(discovered));
        if (string.IsNullOrWhiteSpace(discovered.Address) || discovered.Port == 0) throw new ArgumentException("发现结果缺少有效地址或端口。", nameof(discovered));
        ThrowIfDisposed();
        lock (_stateGate)
        {
            if (!_devices.TryGetValue(discovered.DeviceId, out var device))
            {
                device = new AirplayDevice(discovered.DeviceId, discovered.DisplayName, discovered.Address, discovered.Port);
                _devices.Add(discovered.DeviceId, device);
            }
            device.UpdateDiscovery(discovered.DisplayName, discovered.Address, discovered.Port);
            return device;
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        SettingsDocument snapshot;
        lock (_stateGate) snapshot = _document;
        var directory = Path.GetDirectoryName(_settingsPath) ?? throw new InvalidOperationException("配置文件路径缺少目录。");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var content = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw new IOException($"无法保存配置文件“{_settingsPath}”。内存中的配置已保留。", exception);
        }
    }

    private void PreserveCorruptFile()
    {
        var directory = Path.GetDirectoryName(_settingsPath) ?? throw new InvalidOperationException("配置文件路径缺少目录。");
        var backupPath = Path.Combine(directory, $"{Path.GetFileName(_settingsPath)}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json");
        File.Move(_settingsPath, backupPath);
    }

    private static SettingsDocument CreateDefaultDocument() => new();

    private static void Validate(SettingsDocument document)
    {
        if (document.Version != CurrentVersion) throw new InvalidDataException("配置文件版本不受支持。");
        ArgumentNullException.ThrowIfNull(document.GlobalSettings);
        ArgumentNullException.ThrowIfNull(document.DeviceSettings);
        ValidateGlobalSettings(document.GlobalSettings);
        foreach (var (deviceId, settings) in document.DeviceSettings)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) throw new InvalidDataException("设备配置包含空设备标识。");
            ArgumentNullException.ThrowIfNull(settings);
            ValidateDeviceSettings(settings);
        }
    }

    private static void ValidateDeviceSettings(DeviceSettings settings)
    {
        if (!Enum.IsDefined(settings.ConnectionPolicy)) throw new InvalidDataException("设备连接策略无效。");
        ValidateVolume(settings.VolumeDb, "设备音量");
    }

    private static void ValidateGlobalSettings(GlobalSettings settings)
    {
        if (!Enum.IsDefined(settings.DefaultConnectionPolicy)) throw new InvalidDataException("默认连接策略无效。");
        ValidateVolume(settings.DefaultVolumeDb, "默认音量");
        if (settings.DiscoveryTimeout <= TimeSpan.Zero || settings.DiscoveryTimeout > TimeSpan.FromSeconds(30))
            throw new InvalidDataException("发现超时时间必须在 1 毫秒到 30 秒之间。");
    }

    private static void ValidateVolume(float volumeDb, string name)
    {
        if (float.IsNaN(volumeDb) || float.IsInfinity(volumeDb) || volumeDb is < -144 or > 0)
            throw new InvalidDataException($"{name}必须在 -144 到 0 dB 之间。");
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(DeviceSettingsService));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class SettingsDocument
    {
        public int Version { get; init; } = CurrentVersion;
        public GlobalSettings GlobalSettings { get; set; } = new();
        public Dictionary<string, DeviceSettings> DeviceSettings { get; init; } = new(StringComparer.Ordinal);
    }
}
