#ifndef AIRPLAY_CORE_H
#define AIRPLAY_CORE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

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
