//! 面向 C#/C 的稳定接口：只暴露不透明会话句柄、固定大小目标结构和轮询事件。
//! 所有导出函数都拦截 Rust panic，不能让异常穿透 DLL 边界。

use crate::{
    discovery::discover_homepods,
    engine::{ConnectionPolicy, EngineEvent, SenderEngine},
    error::{CoreError, abi_error_code},
    session::{Session, SessionEvent, SessionState},
};
use std::{
    collections::HashMap,
    ffi::{CStr, c_char},
    net::SocketAddr,
    panic::{AssertUnwindSafe, catch_unwind},
    str::FromStr,
    sync::{Mutex, OnceLock},
    time::Duration,
};

const OK: i32 = 0;
#[cfg(test)]
const ERR_ARGUMENT: i32 = -1;
const ERR_PANIC: i32 = -127;

#[repr(C)]
pub struct AirplayEvent {
    pub kind: i32,
    pub state: i32,
    pub volume_db: f32,
    pub error_code: i32,
}

#[repr(C)]
pub struct AirplayTargetInfo {
    pub device_id: [c_char; 32],
    pub display_name: [c_char; 128],
    pub address: [c_char; 64],
    pub port: u16,
}

/// C# 在启动时用于验证结构体 ABI 的布局描述。
/// 所有数值均为字节单位，字段顺序是 ABI 的一部分。
#[repr(C)]
pub struct AirplayAbiLayout {
    pub event_size: u32,
    pub event_align: u32,
    pub event_kind_offset: u32,
    pub event_state_offset: u32,
    pub event_volume_db_offset: u32,
    pub event_error_code_offset: u32,
    pub target_size: u32,
    pub target_align: u32,
    pub target_device_id_offset: u32,
    pub target_display_name_offset: u32,
    pub target_address_offset: u32,
    pub target_port_offset: u32,
}

#[derive(Default)]
struct Registry {
    next: u64,
    sessions: HashMap<u64, Session>,
    engines: HashMap<u64, SenderEngine>,
    policies: HashMap<u64, ConnectionPolicy>,
}

static REGISTRY: OnceLock<Mutex<Registry>> = OnceLock::new();

fn registry() -> &'static Mutex<Registry> {
    REGISTRY.get_or_init(|| {
        Mutex::new(Registry {
            next: 1,
            ..Default::default()
        })
    })
}

fn code(error: CoreError) -> i32 {
    abi_error_code(&error)
}

fn guarded(f: impl FnOnce() -> Result<i32, CoreError>) -> i32 {
    catch_unwind(AssertUnwindSafe(f))
        .unwrap_or(Ok(ERR_PANIC))
        .unwrap_or_else(code)
}

#[unsafe(no_mangle)]
pub extern "C" fn airplay_core_init() -> i32 {
    guarded(|| Ok(OK))
}

#[unsafe(no_mangle)]
pub extern "C" fn airplay_core_version() -> *const c_char {
    static VERSION: &[u8] = concat!(env!("CARGO_PKG_VERSION"), "\0").as_bytes();
    catch_unwind(|| VERSION.as_ptr() as *const c_char).unwrap_or(std::ptr::null())
}

#[unsafe(no_mangle)]
/// 返回 C ABI 结构体布局，供受管调用方在首次加载时验证。
///
/// # Safety
/// `layout` 必须指向当前调用期间有效且可写的 `AirplayAbiLayout`。
pub unsafe extern "C" fn airplay_core_get_abi_layout(layout: *mut AirplayAbiLayout) -> i32 {
    guarded(|| {
        if layout.is_null() {
            return Err(CoreError::InvalidArgument);
        }
        unsafe {
            layout.write(AirplayAbiLayout {
                event_size: std::mem::size_of::<AirplayEvent>() as u32,
                event_align: std::mem::align_of::<AirplayEvent>() as u32,
                event_kind_offset: std::mem::offset_of!(AirplayEvent, kind) as u32,
                event_state_offset: std::mem::offset_of!(AirplayEvent, state) as u32,
                event_volume_db_offset: std::mem::offset_of!(AirplayEvent, volume_db) as u32,
                event_error_code_offset: std::mem::offset_of!(AirplayEvent, error_code) as u32,
                target_size: std::mem::size_of::<AirplayTargetInfo>() as u32,
                target_align: std::mem::align_of::<AirplayTargetInfo>() as u32,
                target_device_id_offset: std::mem::offset_of!(AirplayTargetInfo, device_id) as u32,
                target_display_name_offset: std::mem::offset_of!(AirplayTargetInfo, display_name)
                    as u32,
                target_address_offset: std::mem::offset_of!(AirplayTargetInfo, address) as u32,
                target_port_offset: std::mem::offset_of!(AirplayTargetInfo, port) as u32,
            });
        }
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn airplay_session_create() -> u64 {
    catch_unwind(AssertUnwindSafe(|| {
        let mut registry = registry().lock().expect("会话注册表已损坏");
        let handle = registry.next;
        registry.next = registry.next.checked_add(1).unwrap_or(1);
        registry.sessions.insert(handle, Session::default());
        registry.policies.insert(handle, ConnectionPolicy::Manual);
        handle
    }))
    .unwrap_or(0)
}

#[unsafe(no_mangle)]
pub extern "C" fn airplay_session_destroy(handle: u64) -> i32 {
    guarded(|| {
        let (mut engine, mut session) = {
            let mut registry = registry()
                .lock()
                .map_err(|_| CoreError::InvalidState("registry poisoned"))?;
            let engine = registry.engines.remove(&handle);
            registry.policies.remove(&handle);
            let session = registry
                .sessions
                .remove(&handle)
                .ok_or(CoreError::InvalidArgument)?;
            (engine, session)
        };
        if let Some(engine) = engine.as_mut() {
            engine.stop();
        }
        session.stop();
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
/// 启动指定会话的发送引擎。
///
/// # Safety
/// `address` 必须指向当前调用期间有效、以 `\0` 结尾的 UTF-8 地址字符串。
pub unsafe extern "C" fn airplay_session_start(handle: u64, address: *const c_char) -> i32 {
    unsafe { airplay_session_connect(handle, address) }
}

#[unsafe(no_mangle)]
/// 设置会话连接策略。0 为手动（默认），1 为有限自动重试。
pub extern "C" fn airplay_session_set_connection_policy(handle: u64, policy: i32) -> i32 {
    guarded(|| {
        let mut registry = registry()
            .lock()
            .map_err(|_| CoreError::InvalidState("registry poisoned"))?;
        if !registry.sessions.contains_key(&handle) {
            return Err(CoreError::InvalidArgument);
        }
        if registry.engines.contains_key(&handle) {
            return Err(CoreError::InvalidState(
                "cannot change policy while connected",
            ));
        }
        registry
            .policies
            .insert(handle, ConnectionPolicy::try_from(policy)?);
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
/// 用户明确请求连接；发现本身不会触发此调用。
///
/// # Safety
/// `address` 必须指向当前调用期间有效、以 `\0` 结尾的 UTF-8 地址字符串。
pub unsafe extern "C" fn airplay_session_connect(handle: u64, address: *const c_char) -> i32 {
    guarded(|| connect_session(handle, address))
}

fn connect_session(handle: u64, address: *const c_char) -> Result<i32, CoreError> {
    if address.is_null() {
        return Err(CoreError::InvalidArgument);
    }
    let address = unsafe { CStr::from_ptr(address) }
        .to_str()
        .map_err(|_| CoreError::InvalidArgument)?;
    let address = SocketAddr::from_str(address).map_err(|_| CoreError::InvalidArgument)?;
    let policy = {
        let registry = registry()
            .lock()
            .map_err(|_| CoreError::InvalidState("registry poisoned"))?;
        let session = registry
            .sessions
            .get(&handle)
            .ok_or(CoreError::InvalidArgument)?;
        if !matches!(
            session.state(),
            SessionState::Idle | SessionState::Stopped | SessionState::Failed
        ) || registry.engines.contains_key(&handle)
        {
            return Err(CoreError::InvalidState("session already connected"));
        }
        *registry
            .policies
            .get(&handle)
            .unwrap_or(&ConnectionPolicy::Manual)
    };
    let engine = SenderEngine::start(address, policy)?;
    let mut registry = registry()
        .lock()
        .map_err(|_| CoreError::InvalidState("registry poisoned"))?;
    let session = registry
        .sessions
        .get_mut(&handle)
        .ok_or(CoreError::InvalidArgument)?;
    session.begin_connection()?;
    registry.engines.insert(handle, engine);
    Ok(OK)
}

#[unsafe(no_mangle)]
pub extern "C" fn airplay_session_stop(handle: u64) -> i32 {
    airplay_session_disconnect(handle)
}

#[unsafe(no_mangle)]
/// 用户主动断开。该操作会停止后台线程，因此任何待执行的自动重试都会被取消。
pub extern "C" fn airplay_session_disconnect(handle: u64) -> i32 {
    guarded(|| {
        let mut engine = {
            let mut registry = registry()
                .lock()
                .map_err(|_| CoreError::InvalidState("registry poisoned"))?;
            registry.engines.remove(&handle)
        };
        if let Some(engine) = engine.as_mut() {
            engine.stop();
        }
        registry()
            .lock()
            .map_err(|_| CoreError::InvalidState("registry poisoned"))?
            .sessions
            .get_mut(&handle)
            .ok_or(CoreError::InvalidArgument)?
            .stop();
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn airplay_session_set_volume(handle: u64, volume_db: f32) -> i32 {
    guarded(|| {
        let mut registry = registry()
            .lock()
            .map_err(|_| CoreError::InvalidState("registry poisoned"))?;
        registry
            .sessions
            .get_mut(&handle)
            .ok_or(CoreError::InvalidArgument)?
            .set_local_volume(volume_db)?;
        registry
            .engines
            .get(&handle)
            .ok_or(CoreError::InvalidState("sender engine not started"))?
            .set_volume(volume_db)?;
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
/// 取出队列中的下一个事件；没有事件时写入 kind=0。
///
/// # Safety
/// `event` 必须指向一个当前调用期间可写的 `AirplayEvent`。
pub unsafe extern "C" fn airplay_session_poll_event(handle: u64, event: *mut AirplayEvent) -> i32 {
    guarded(|| {
        if event.is_null() {
            return Err(CoreError::InvalidArgument);
        }
        let mut registry = registry()
            .lock()
            .map_err(|_| CoreError::InvalidState("registry poisoned"))?;
        let engine_events = registry
            .engines
            .get(&handle)
            .map(SenderEngine::drain_events)
            .unwrap_or_default();
        // 后台线程结束后立即移除引擎，下一次显式 connect 才能创建新连接。
        if engine_events
            .iter()
            .any(|event| matches!(event, EngineEvent::Stopped))
        {
            registry.engines.remove(&handle);
        }
        let session = registry
            .sessions
            .get_mut(&handle)
            .ok_or(CoreError::InvalidArgument)?;
        apply_engine_events(session, engine_events);
        unsafe { event.write(map_event(session.pop_event())) };
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
/// 发现局域网中的 HomePod，并把结果写入调用方数组。
///
/// # Safety
/// `out_count` 必须可写；当 `capacity` 大于 0 时，`targets` 必须指向至少
/// `capacity` 个连续、可写的 `AirplayTargetInfo`。
pub unsafe extern "C" fn airplay_discover_homepods(
    timeout_ms: u32,
    targets: *mut AirplayTargetInfo,
    capacity: usize,
    out_count: *mut usize,
) -> i32 {
    guarded(|| {
        if timeout_ms == 0 || timeout_ms > 30_000 || out_count.is_null() {
            return Err(CoreError::InvalidArgument);
        }
        if capacity > 0 && targets.is_null() {
            return Err(CoreError::InvalidArgument);
        }
        let found = discover_homepods(Duration::from_millis(timeout_ms as u64))?;
        unsafe { out_count.write(found.len()) };
        for (index, target) in found.iter().take(capacity).enumerate() {
            let address = target
                .addresses
                .iter()
                .find(|address| address.is_ipv4())
                .or_else(|| target.addresses.first())
                .map(ToString::to_string)
                .unwrap_or_default();
            let mut output = AirplayTargetInfo {
                device_id: [0; 32],
                display_name: [0; 128],
                address: [0; 64],
                port: target.airplay_port.unwrap_or(target.port),
            };
            copy_text(&mut output.device_id, &target.device_id);
            copy_text(&mut output.display_name, &target.display_name);
            copy_text(&mut output.address, &address);
            unsafe { targets.add(index).write(output) };
        }
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
/// 清空一个已经消费的事件结构。
///
/// # Safety
/// `event` 必须指向一个当前调用期间可写的 `AirplayEvent`。
pub unsafe extern "C" fn airplay_event_release(event: *mut AirplayEvent) -> i32 {
    guarded(|| {
        if event.is_null() {
            return Err(CoreError::InvalidArgument);
        }
        unsafe { event.write(map_event(None)) };
        Ok(OK)
    })
}

fn apply_engine_events(session: &mut Session, events: Vec<EngineEvent>) {
    for event in events {
        match event {
            EngineEvent::Paired if session.state() == SessionState::Pairing => {
                let _ = session.pairing_verified();
            }
            EngineEvent::Streaming if session.state() == SessionState::Connecting => {
                let _ = session.stream_ready();
            }
            EngineEvent::Volume(value) => {
                let _ = session.receive_remote_volume(value);
            }
            EngineEvent::Reconnecting => session.reconnect(),
            EngineEvent::Error(value) => session.fail(value),
            EngineEvent::Stopped if session.state() != SessionState::Failed => session.stop(),
            _ => {}
        }
    }
}

fn copy_text<const N: usize>(destination: &mut [c_char; N], value: &str) {
    let bytes = value.as_bytes();
    let length = bytes.len().min(N.saturating_sub(1));
    for index in 0..length {
        destination[index] = bytes[index] as c_char;
    }
}

fn map_event(event: Option<SessionEvent>) -> AirplayEvent {
    match event {
        None => AirplayEvent {
            kind: 0,
            state: 0,
            volume_db: 0.0,
            error_code: 0,
        },
        Some(SessionEvent::State(state)) => AirplayEvent {
            kind: 1,
            state: state as i32,
            volume_db: 0.0,
            error_code: 0,
        },
        Some(SessionEvent::Volume(value)) => AirplayEvent {
            kind: 2,
            state: 0,
            volume_db: value,
            error_code: 0,
        },
        Some(SessionEvent::Error(value)) => AirplayEvent {
            kind: 3,
            state: 0,
            volume_db: 0.0,
            error_code: value,
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ffi_is_panic_safe() {
        assert_eq!(
            guarded(|| -> Result<i32, CoreError> { panic!("test") }),
            ERR_PANIC
        );
    }

    #[test]
    fn fixed_ffi_text_is_terminated_and_truncated() {
        let mut output = [0 as c_char; 5];
        copy_text(&mut output, "abcdef");
        assert_eq!(
            output,
            [
                b'a' as c_char,
                b'b' as c_char,
                b'c' as c_char,
                b'd' as c_char,
                0
            ]
        );
    }

    #[test]
    fn ffi_struct_layout_is_stable() {
        assert_eq!(std::mem::size_of::<AirplayEvent>(), 16);
        assert_eq!(std::mem::align_of::<AirplayEvent>(), 4);
        assert_eq!(std::mem::size_of::<AirplayTargetInfo>(), 226);
        assert_eq!(std::mem::align_of::<AirplayTargetInfo>(), 2);
        assert_eq!(std::mem::size_of::<AirplayAbiLayout>(), 48);
    }

    #[test]
    fn engine_events_keep_failed_as_terminal_state() {
        let mut session = Session::default();
        session.begin_pairing().unwrap();
        apply_engine_events(
            &mut session,
            vec![EngineEvent::Error(-5), EngineEvent::Stopped],
        );
        assert_eq!(session.state(), SessionState::Failed);
    }

    #[test]
    fn reconnect_event_precedes_next_connecting_state() {
        let mut session = Session::default();
        session.begin_pairing().unwrap();
        session.pairing_verified().unwrap();
        session.stream_ready().unwrap();
        apply_engine_events(&mut session, vec![EngineEvent::Reconnecting]);
        assert_eq!(session.state(), SessionState::Connecting);
        assert_eq!(
            session.pop_event(),
            Some(SessionEvent::State(SessionState::Pairing))
        );
        assert_eq!(
            session.pop_event(),
            Some(SessionEvent::State(SessionState::Connecting))
        );
        assert_eq!(
            session.pop_event(),
            Some(SessionEvent::State(SessionState::Streaming))
        );
        assert_eq!(
            session.pop_event(),
            Some(SessionEvent::State(SessionState::Connecting))
        );
    }

    #[test]
    fn explicit_disconnect_ignores_late_reconnect_event() {
        let mut session = Session::default();
        session.begin_pairing().unwrap();
        session.stop();
        apply_engine_events(&mut session, vec![EngineEvent::Reconnecting]);
        assert_eq!(session.state(), SessionState::Stopped);
    }

    #[test]
    fn policy_and_disconnect_exports_are_panic_isolated() {
        let handle = airplay_session_create();
        assert_eq!(airplay_session_set_connection_policy(handle, 1), OK);
        assert_eq!(
            airplay_session_set_connection_policy(handle, 9),
            ERR_ARGUMENT
        );
        assert_eq!(airplay_session_disconnect(handle), OK);
        assert_eq!(airplay_session_destroy(handle), OK);
    }

    #[test]
    fn exported_functions_reject_invalid_arguments_without_panicking() {
        assert_eq!(airplay_core_init(), OK);
        assert!(!airplay_core_version().is_null());
        assert_eq!(
            unsafe { airplay_core_get_abi_layout(std::ptr::null_mut()) },
            ERR_ARGUMENT
        );
        assert_eq!(
            unsafe { airplay_event_release(std::ptr::null_mut()) },
            ERR_ARGUMENT
        );
        assert_eq!(
            unsafe { airplay_discover_homepods(0, std::ptr::null_mut(), 0, std::ptr::null_mut()) },
            ERR_ARGUMENT
        );
        let mut event = map_event(None);
        assert_eq!(
            unsafe { airplay_session_poll_event(u64::MAX, &mut event) },
            ERR_ARGUMENT
        );
    }

    #[test]
    fn session_handle_can_be_created_and_destroyed() {
        let handle = airplay_session_create();
        assert_ne!(handle, 0);
        assert_eq!(airplay_session_destroy(handle), OK);
        assert_eq!(airplay_session_destroy(handle), ERR_ARGUMENT);
    }
}
