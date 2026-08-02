using AirBlade.Interop;
using AirBlade.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AirBlade.Tests;

[TestClass]
public sealed class AirplaySessionServiceTests
{
    [TestMethod]
    public async Task 初始化并轮询状态事件()
    {
        var native = new FakeNative();
        native.Events.Enqueue(State(AirplaySessionState.Pairing));
        native.Events.Enqueue(State(AirplaySessionState.Connecting));
        native.Events.Enqueue(State(AirplaySessionState.Streaming));
        await using var service = new AirplaySessionService(native);
        var states = new List<AirplaySessionState>();
        service.StateChanged += (_, value) => states.Add(value.Current.State);
        await service.InitializeAsync();
        await WaitUntilAsync(() => states.Contains(AirplaySessionState.Streaming));
        CollectionAssert.AreEqual(new[] { AirplaySessionState.Pairing, AirplaySessionState.Connecting, AirplaySessionState.Streaming }, states);
    }

    [TestMethod]
    public async Task 主动断开后不会由迟到重连事件改变状态()
    {
        var native = new FakeNative();
        await using var service = new AirplaySessionService(native);
        await service.InitializeAsync();
        await service.ConnectAsync("127.0.0.1:7000");
        await service.DisconnectAsync();
        native.Events.Enqueue(State(AirplaySessionState.Connecting));
        native.Events.Enqueue(State(AirplaySessionState.Disconnected));
        await WaitUntilAsync(() => service.Current.State == AirplaySessionState.Disconnected);
        Assert.AreEqual(1, native.DisconnectCount);
    }

    [TestMethod]
    public async Task 重复释放只销毁一次()
    {
        var native = new FakeNative();
        var service = new AirplaySessionService(native);
        await service.InitializeAsync();
        await Task.WhenAll(service.DisposeAsync().AsTask(), service.DisposeAsync().AsTask());
        Assert.AreEqual(1, native.DestroyCount);
    }

    [TestMethod]
    public void ABI结构体大小与字段偏移固定()
    {
        Assert.AreEqual(16, System.Runtime.InteropServices.Marshal.SizeOf<AirplayEvent>());
        Assert.AreEqual(226, System.Runtime.InteropServices.Marshal.SizeOf<AirplayTargetInfo>());
        Assert.AreEqual((IntPtr)224, System.Runtime.InteropServices.Marshal.OffsetOf<AirplayTargetInfo>(nameof(AirplayTargetInfo.Port)));
    }

    [TestMethod]
    public async Task 原生初始化错误会向调用方返回()
    {
        var native = new FakeNative { LoadException = AirplayCoreException.FromCode(-5) };
        await using var service = new AirplaySessionService(native);
        var exception = await Assert.ThrowsExceptionAsync<AirplayCoreException>(() => service.InitializeAsync());
        Assert.AreEqual(AirplayErrorCode.Network, exception.ErrorCode);
    }

    [TestMethod]
    public async Task 注册表会统一释放活动会话()
    {
        var first = new FakeNative();
        var second = new FakeNative();
        var one = new AirplaySessionService(first);
        var two = new AirplaySessionService(second);
        await one.InitializeAsync();
        await two.InitializeAsync();
        await AirplaySessionRegistry.DisposeAllAsync();
        Assert.AreEqual(1, first.DestroyCount);
        Assert.AreEqual(1, second.DestroyCount);
    }

    [TestMethod]
    public void 错误码可翻译为中文信息()
    {
        Assert.AreEqual("网络连接失败", AirplayCoreException.Describe(-5));
        Assert.AreEqual("Rust 原生层发生内部异常", AirplayCoreException.Describe(-127));
    }

    private static AirplayEvent State(AirplaySessionState state) => new() { Kind = AirplayEventKind.State, State = state };
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 50 && !predicate(); index++) await Task.Delay(20);
        Assert.IsTrue(predicate(), "等待异步事件超时。");
    }

    private sealed class FakeNative : IAirplayCoreNative
    {
        public Queue<AirplayEvent> Events { get; } = new();
        public int DisconnectCount { get; private set; }
        public int DestroyCount { get; private set; }
        public Exception? LoadException { get; init; }
        public void EnsureLoaded() { if (LoadException is not null) throw LoadException; }
        public ulong CreateSession() => 1;
        public int SetConnectionPolicy(ulong handle, AirplayConnectionPolicy policy) => 0;
        public int Connect(ulong handle, string endpoint) => 0;
        public int Disconnect(ulong handle) { DisconnectCount++; return 0; }
        public int SetVolume(ulong handle, float volumeDb) => 0;
        public int PollEvent(ulong handle, out AirplayEvent airplayEvent) { airplayEvent = Events.Count > 0 ? Events.Dequeue() : default; return 0; }
        public int Destroy(ulong handle) { DestroyCount++; return 0; }
        public int Discover(uint timeoutMs, AirplayTargetInfo[] targets, out nuint count) { count = 0; return 0; }
    }
}
