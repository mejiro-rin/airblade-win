using AirBlade.Interop;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace AirBlade.Services;

public sealed record AirplayDiscoveredDevice(string DeviceId, string DisplayName, string Address, ushort Port)
{
    public string Endpoint => $"{Address}:{Port}";
}

public sealed record AirplaySessionSnapshot(AirplaySessionState State, float? VolumeDb, AirplayCoreException? LastError);
public sealed class AirplayStateChangedEventArgs : EventArgs
{
    public AirplayStateChangedEventArgs(AirplaySessionSnapshot current) => Current = current;
    public AirplaySessionSnapshot Current { get; }
}

public sealed class AirplayVolumeChangedEventArgs : EventArgs
{
    public AirplayVolumeChangedEventArgs(float volumeDb) => VolumeDb = volumeDb;
    public float VolumeDb { get; }
}

public sealed class AirplaySessionService : IAsyncDisposable
{
    private readonly IAirplayCoreNative _native;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _pollCancellation = new();
    private readonly object _stateGate = new();
    private SafeAirplaySessionHandle? _handle;
    private Task? _pollTask;
    private AirplaySessionSnapshot _snapshot = new(AirplaySessionState.Idle, null, null);
    private int _disconnectRequested;
    private int _disposed;

    public AirplaySessionService() : this(new PInvokeAirplayCoreNative()) { }
    internal AirplaySessionService(IAirplayCoreNative native) => _native = native;
    public AirplaySessionSnapshot Current => GetSnapshot();
    public event EventHandler<AirplayStateChangedEventArgs>? StateChanged;
    public event EventHandler<AirplayVolumeChangedEventArgs>? VolumeChanged;
    public event EventHandler<AirplayCoreException>? ErrorOccurred;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_handle is not null) return;
            _native.EnsureLoaded();
            var value = _native.CreateSession();
            if (value == 0) throw new AirplayCoreException(LocalizationService.Current["Error.CreateSessionFailed"], null);
            _handle = new SafeAirplaySessionHandle(value, _native);
            _pollTask = PollAsync(_pollCancellation.Token);
            AirplaySessionRegistry.Register(this);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AirplayDiscoveredDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout), LocalizationService.Current["Error.DiscoveryTimeoutRange"]);
        cancellationToken.ThrowIfCancellationRequested();
        _native.EnsureLoaded();
        var results = await Task.Run(() =>
        {
            var values = CreateTargetBuffer(32);
            PInvokeAirplayCoreNative.ThrowIfFailed(_native.Discover((uint)timeout.TotalMilliseconds, values, out var count));
            return values.Take((int)Math.Min(count, (nuint)values.Length)).Select(ToDevice).ToArray();
        }, CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    public async Task ConnectAsync(string endpoint, AirplayConnectionPolicy policy = AirplayConnectionPolicy.Manual, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var handle = RequireHandle();
            var state = GetSnapshot().State;
            if (state is AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming)
                throw new InvalidOperationException(LocalizationService.Current["Error.SessionBusy"]);
            PInvokeAirplayCoreNative.ThrowIfFailed(_native.SetConnectionPolicy(handle.Value, policy));
            PInvokeAirplayCoreNative.ThrowIfFailed(_native.Connect(handle.Value, endpoint));
            Interlocked.Exchange(ref _disconnectRequested, 0);
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _disconnectRequested, 1);
            if (_handle is not null && !_handle.IsInvalid) PInvokeAirplayCoreNative.ThrowIfFailed(_native.Disconnect(_handle.Value));
        }
        finally { _gate.Release(); }
    }

    public async Task SetVolumeAsync(float volumeDb, CancellationToken cancellationToken = default)
    {
        if (volumeDb is < -144 or > 0) throw new ArgumentOutOfRangeException(nameof(volumeDb), LocalizationService.Current["Error.VolumeRange"]);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { PInvokeAirplayCoreNative.ThrowIfFailed(_native.SetVolume(RequireHandle().Value, volumeDb)); }
        finally { _gate.Release(); }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                AirplayEvent airplayEvent;
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_handle is null || _handle.IsInvalid) return;
                    PInvokeAirplayCoreNative.ThrowIfFailed(_native.PollEvent(_handle.Value, out airplayEvent));
                }
                finally { _gate.Release(); }
                ProcessEvent(airplayEvent);
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (AirplayCoreException exception) { PublishError(exception); }
    }

    private void ProcessEvent(AirplayEvent airplayEvent)
    {
        switch (airplayEvent.Kind)
        {
            case AirplayEventKind.State:
                if (Volatile.Read(ref _disconnectRequested) != 0 && airplayEvent.State is AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming) return;
                // 会话重新进入活跃状态说明已恢复正常，清除此前残留的错误，
                // 避免连接成功后旧错误一直显示在设备卡片上。
                var isActive = airplayEvent.State is AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming;
                PublishState(new(airplayEvent.State, GetSnapshot().VolumeDb, isActive ? null : GetSnapshot().LastError));
                break;
            case AirplayEventKind.Volume:
                lock (_stateGate) _snapshot = _snapshot with { VolumeDb = airplayEvent.VolumeDb };
                VolumeChanged?.Invoke(this, new(airplayEvent.VolumeDb));
                break;
            case AirplayEventKind.Error: PublishError(AirplayCoreException.FromCode(airplayEvent.ErrorCode)); break;
        }
    }
    private void PublishState(AirplaySessionSnapshot snapshot) { lock (_stateGate) _snapshot = snapshot; StateChanged?.Invoke(this, new(snapshot)); }
    private void PublishError(AirplayCoreException exception)
    {
        AirplaySessionSnapshot snapshot;
        lock (_stateGate) { _snapshot = _snapshot with { State = AirplaySessionState.Failed, LastError = exception }; snapshot = _snapshot; }
        StateChanged?.Invoke(this, new(snapshot));
        ErrorOccurred?.Invoke(this, exception);
    }
    private AirplaySessionSnapshot GetSnapshot() { lock (_stateGate) return _snapshot; }
    private SafeAirplaySessionHandle RequireHandle() => _handle is { IsInvalid: false } handle ? handle : throw new InvalidOperationException(LocalizationService.Current["Error.InitializeFirst"]);
    private static AirplayTargetInfo[] CreateTargetBuffer(int count) => Enumerable.Range(0, count).Select(_ => new AirplayTargetInfo { DeviceId = new byte[32], DisplayName = new byte[128], Address = new byte[64] }).ToArray();
    private static AirplayDiscoveredDevice ToDevice(AirplayTargetInfo value) => new(ReadText(value.DeviceId), ReadText(value.DisplayName), ReadText(value.Address), value.Port);
    private static string ReadText(byte[] value) => Encoding.UTF8.GetString(value, 0, Array.IndexOf(value, (byte)0) is var end && end >= 0 ? end : value.Length);
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(AirplaySessionService)); }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pollCancellation.Cancel();
        if (_pollTask is not null) await _pollTask.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_handle is not null && !_handle.IsInvalid) _ = _native.Disconnect(_handle.Value);
            _handle?.Dispose();
            _handle = null;
        }
        finally { _gate.Release(); _pollCancellation.Dispose(); _gate.Dispose(); AirplaySessionRegistry.Unregister(this); }
    }

    private sealed class SafeAirplaySessionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly IAirplayCoreNative _native;
        public SafeAirplaySessionHandle(ulong value, IAirplayCoreNative native) : base(true) { _native = native; SetHandle(unchecked((IntPtr)(long)value)); }
        public ulong Value => unchecked((ulong)handle.ToInt64());
        protected override bool ReleaseHandle() => _native.Destroy(Value) == 0;
    }
}
