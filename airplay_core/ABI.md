# airplay_core C ABI 0.4

本接口面向 C#、C 和其他上层调用方。核心只负责设备发现、AirPlay 会话、音频采集发送和运行期连接策略，不读取或保存 UI 配置。

## 生命周期

标准调用顺序：

1. 调用 `airplay_core_init`。
2. 调用 `airplay_discover_homepods` 获取设备信息。发现不会自动连接。
3. 调用 `airplay_session_create` 创建句柄。
4. 可选调用 `airplay_session_set_connection_policy`；默认是手动策略。
5. 用户明确连接时调用 `airplay_session_connect`。
6. 定期调用 `airplay_session_poll_event`，直到取得 `AIRPLAY_EVENT_NONE`。
7. 收到 `AIRPLAY_STATE_STREAMING` 后才能调用 `airplay_session_set_volume`。
8. 用户主动断开时调用 `airplay_session_disconnect`。
9. 不再使用会话时调用 `airplay_session_destroy`。

`airplay_session_start` 和 `airplay_session_stop` 是兼容旧调用方的别名，分别等同于 `connect` 和 `disconnect`。

## 连接策略

- `AIRPLAY_CONNECTION_MANUAL`：默认值。意外断链后释放协议与采集资源，进入 `FAILED`，等待下一次显式 `connect`。
- `AIRPLAY_CONNECTION_AUTOMATIC`：只对超时、地址暂不可用、网络不可达和主机不可达进行最多三次退避重试。

认证、配对、协议、加密、参数、身份、RTSP 拒绝和连接重置均不重试。连接重置可能表示 HomePod 被其他发送端接管，因此自动策略也会停止，避免争抢播放权。

主动 `disconnect` 会停止后台线程并取消待执行的重试。迟到的重连事件不能恢复已经停止的会话。

## 事件顺序

首次连接成功的状态顺序：

```text
PAIRING → CONNECTING → STREAMING
```

手动模式发生错误时：

```text
FAILED → ERROR
```

自动模式遇到允许重试的网络错误时产生 `CONNECTING`，重试成功后产生 `STREAMING`；用户在终止状态显式再次连接时，重新产生完整的 `PAIRING → CONNECTING → STREAMING`。最终停止重试时产生 `FAILED → ERROR`。

上层必须持续轮询到 `kind == AIRPLAY_EVENT_NONE`，以免遗漏同一轮产生的多个事件。

## 内存与线程约束

- 句柄为不透明的 `uint64_t`，只能传回本库。
- 所有字符串都是以 `\0` 结尾的 UTF-8。
- `AirplayTargetInfo` 使用固定数组，不要求调用方释放。
- `AirplayEvent` 当前不含堆内存；调用后仍建议执行 `airplay_event_release`，为未来 ABI 扩展保留兼容性。
- 同一句柄的控制调用应由上层串行执行；事件轮询也应固定在一个后台任务中。
- 所有导出函数都拦截 Rust panic。`AIRPLAY_ERR_PANIC` 表示核心内部发生了未预期异常。

## 配置归属

上层应以发现结果中的 `device_id` 为键保存自动连接、连接策略、记忆音量和隐藏状态。地址可能随 DHCP 改变，不能作为持久化主键。核心只接受本次会话参数，不持久化用户偏好。

完整常量、结构体和函数签名见 `include/airplay_core.h`，C# 对照见 `include/AirplayCoreNative.cs`。
