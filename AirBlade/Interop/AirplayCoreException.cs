using AirBlade.Services;

namespace AirBlade.Interop;

public sealed class AirplayCoreException : Exception
{
    public AirplayErrorCode? ErrorCode { get; }
    internal AirplayCoreException(string message, AirplayErrorCode? code, Exception? inner = null) : base(message, inner) => ErrorCode = code;
    public static AirplayCoreException FromCode(int code) => new(
        LocalizationService.Current.Format("Error.NativeOperationFailed", Describe(code), code),
        Enum.IsDefined(typeof(AirplayErrorCode), code) ? (AirplayErrorCode)code : null);
    public static string Describe(int code) => code switch
    {
        -1 => t["Error.InvalidArgument"], -2 => t["Error.InvalidState"], -3 => t["Error.InvalidProtocolData"], -4 => t["Error.ReceiverAuthFailed"],
        -5 => t["Error.NetworkFailed"], -6 => t["Error.DiscoveryFailed"], -7 => t["Error.PairingRejected"], -8 => t["Error.InvalidPairingData"],
        -9 => t["Error.RtspRejected"], -10 => t["Error.InvalidResponse"], -11 => t["Error.AudioCaptureFailed"], -127 => t["Error.NativeInternal"], _ => t["Error.UnknownNative"]
    };
    private static LocalizationService t => LocalizationService.Current;
}
