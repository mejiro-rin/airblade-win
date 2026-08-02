namespace AirBlade.Interop;

public sealed class AirplayCoreException : Exception
{
    public AirplayErrorCode? ErrorCode { get; }
    internal AirplayCoreException(string message, AirplayErrorCode? code, Exception? inner = null) : base(message, inner) => ErrorCode = code;
    public static AirplayCoreException FromCode(int code) => new($"AirPlay 原生操作失败：{Describe(code)}（{code}）。", Enum.IsDefined(typeof(AirplayErrorCode), code) ? (AirplayErrorCode)code : null);
    public static string Describe(int code) => code switch
    {
        -1 => "参数无效", -2 => "当前会话状态不允许此操作", -3 => "协议数据无效", -4 => "接收器认证失败",
        -5 => "网络连接失败", -6 => "设备发现失败", -7 => "接收器拒绝配对", -8 => "配对数据无效",
        -9 => "接收器拒绝 RTSP 请求", -10 => "接收器返回无效响应", -11 => "Windows 音频采集失败", -127 => "Rust 原生层发生内部异常", _ => "未知原生错误"
    };
}
