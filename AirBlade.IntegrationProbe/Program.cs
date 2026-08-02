using AirBlade.Interop;
using AirBlade.Services;

Console.WriteLine("开始发现 HomePod（5 秒）...");
await using var session = new AirplaySessionService();
session.StateChanged += (_, eventArgs) => Console.WriteLine($"状态：{eventArgs.Current.State}");
session.VolumeChanged += (_, eventArgs) => Console.WriteLine($"远端音量：{eventArgs.VolumeDb:F1} dB");
session.ErrorOccurred += (_, exception) => Console.Error.WriteLine(exception.Message);

try
{
    await session.InitializeAsync();
    var devices = await session.DiscoverAsync(TimeSpan.FromSeconds(5));
    if (devices.Count == 0) { Console.WriteLine("未发现 HomePod。"); return; }
    for (var index = 0; index < devices.Count; index++) Console.WriteLine($"[{index}] {devices[index].DisplayName} {devices[index].Endpoint}");
    Console.Write("输入设备编号（直接回车取消）：");
    if (!int.TryParse(Console.ReadLine(), out var selected) || selected < 0 || selected >= devices.Count) return;
    await session.ConnectAsync(devices[selected].Endpoint, AirplayConnectionPolicy.Manual);
    Console.WriteLine("已发起连接；按 Enter 主动断开并释放。");
    await Task.Run(Console.ReadLine);
    await session.DisconnectAsync();
}
catch (AirplayCoreException exception) { Console.Error.WriteLine(exception.Message); }
