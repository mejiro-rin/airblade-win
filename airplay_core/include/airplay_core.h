#ifndef AIRPLAY_CORE_H
#define AIRPLAY_CORE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// 连接策略。
#define AIRPLAY_CONNECTION_MANUAL 0
#define AIRPLAY_CONNECTION_AUTOMATIC 1

// 事件类型。
#define AIRPLAY_EVENT_NONE 0
#define AIRPLAY_EVENT_STATE 1
#define AIRPLAY_EVENT_VOLUME 2
#define AIRPLAY_EVENT_ERROR 3

// 会话状态。DISCONNECTED 是供上层显示使用的 STOPPED 别名。
#define AIRPLAY_STATE_IDLE 0
#define AIRPLAY_STATE_PAIRING 1
#define AIRPLAY_STATE_CONNECTING 2
#define AIRPLAY_STATE_STREAMING 3
#define AIRPLAY_STATE_STOPPED 4
#define AIRPLAY_STATE_DISCONNECTED AIRPLAY_STATE_STOPPED
#define AIRPLAY_STATE_FAILED 5

// 导出函数返回值和错误事件使用同一套错误码。
#define AIRPLAY_OK 0
#define AIRPLAY_ERR_ARGUMENT -1
#define AIRPLAY_ERR_STATE -2
#define AIRPLAY_ERR_PROTOCOL -3
#define AIRPLAY_ERR_AUTHENTICATION -4
#define AIRPLAY_ERR_NETWORK -5
#define AIRPLAY_ERR_DISCOVERY -6
#define AIRPLAY_ERR_PAIRING_STATUS -7
#define AIRPLAY_ERR_PAIRING_TLV -8
#define AIRPLAY_ERR_RTSP_STATUS -9
#define AIRPLAY_ERR_RTSP_BODY -10
#define AIRPLAY_ERR_WINDOWS_AUDIO -11
#define AIRPLAY_ERR_PANIC -127

// 事件 kind：0 表示当前无事件，1 表示状态，2 表示远端音量，3 表示错误。
typedef struct AirplayEvent {
    int32_t kind;
    int32_t state;
    float volume_db;
    int32_t error_code;
} AirplayEvent;

// 发现结果全部使用以 \0 结尾的 UTF-8 文本，过长内容会安全截断。
typedef struct AirplayTargetInfo {
    char device_id[32];
    char display_name[128];
    char address[64];
    uint16_t port;
} AirplayTargetInfo;

int32_t airplay_core_init(void);
const char *airplay_core_version(void);

uint64_t airplay_session_create(void);
// 连接策略：0=手动（默认，断链后不抢连），1=自动（仅暂时网络故障有限重试）。
int32_t airplay_session_set_connection_policy(uint64_t handle, int32_t policy);
int32_t airplay_session_connect(uint64_t handle, const char *address);
int32_t airplay_session_disconnect(uint64_t handle);
// 以下两个旧名称保留 ABI 兼容，分别等同于 connect 和 disconnect。
int32_t airplay_session_start(uint64_t handle, const char *address);
int32_t airplay_session_stop(uint64_t handle);
int32_t airplay_session_set_volume(uint64_t handle, float volume_db);
int32_t airplay_session_poll_event(uint64_t handle, AirplayEvent *event);
int32_t airplay_session_destroy(uint64_t handle);

// out_count 返回实际发现数；返回数大于 capacity 时，调用方可以扩大数组后再次发现。
int32_t airplay_discover_homepods(
    uint32_t timeout_ms,
    AirplayTargetInfo *targets,
    size_t capacity,
    size_t *out_count
);

// 当前事件结构不持有堆内存；保留释放函数是为了以后扩展 ABI 时不破坏调用方。
int32_t airplay_event_release(AirplayEvent *event);

#ifdef __cplusplus
}
#endif

#endif
