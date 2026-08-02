using System.Runtime.InteropServices;

namespace AirBlade.Interop;

internal static class AirplayAbiVerifier
{
    internal static void Verify(AirplayAbiLayout native)
    {
        VerifyType<AirplayEvent>(native.EventSize, native.EventAlign, (nameof(AirplayEvent.Kind), native.EventKindOffset), (nameof(AirplayEvent.State), native.EventStateOffset), (nameof(AirplayEvent.VolumeDb), native.EventVolumeDbOffset), (nameof(AirplayEvent.ErrorCode), native.EventErrorCodeOffset));
        VerifyType<AirplayTargetInfo>(native.TargetSize, native.TargetAlign, (nameof(AirplayTargetInfo.DeviceId), native.TargetDeviceIdOffset), (nameof(AirplayTargetInfo.DisplayName), native.TargetDisplayNameOffset), (nameof(AirplayTargetInfo.Address), native.TargetAddressOffset), (nameof(AirplayTargetInfo.Port), native.TargetPortOffset));
    }
    private static void VerifyType<T>(uint size, uint align, params (string Field, uint Offset)[] fields) where T : struct
    {
        if ((uint)Marshal.SizeOf<T>() != size) throw new AirplayCoreException($"C# 与 Rust 的 {typeof(T).Name} 大小不一致。", null);
        var managedAlign = fields.Select(x => Marshal.OffsetOf<T>(x.Field).ToInt32()).Where(x => x > 0).Aggregate(Marshal.SizeOf<T>(), (current, offset) => GreatestCommonDivisor(current, offset));
        if (managedAlign != align) throw new AirplayCoreException($"C# 与 Rust 的 {typeof(T).Name} 对齐不一致。", null);
        foreach (var field in fields) if ((uint)Marshal.OffsetOf<T>(field.Field).ToInt64() != field.Offset) throw new AirplayCoreException($"C# 与 Rust 的 {typeof(T).Name}.{field.Field} 偏移不一致。", null);
    }
    private static int GreatestCommonDivisor(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}
