use std::collections::VecDeque;

use crate::error::{CoreError, Result};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum SessionState {
    Idle = 0,
    Pairing = 1,
    Connecting = 2,
    Streaming = 3,
    Stopped = 4,
    Failed = 5,
}

#[derive(Debug, Clone, PartialEq)]
pub enum SessionEvent {
    State(SessionState),
    Volume(f32),
    Error(i32),
}

/// 面向协议的会话状态机。
/// 传输层和配对层只能按已验证的状态顺序推进；FFI 调用方不能伪造“已开始播放”状态。
pub struct Session {
    state: SessionState,
    volume_db: f32,
    events: VecDeque<SessionEvent>,
}

impl Default for Session {
    fn default() -> Self {
        Self {
            state: SessionState::Idle,
            volume_db: 0.0,
            events: VecDeque::new(),
        }
    }
}

impl Session {
    pub fn state(&self) -> SessionState {
        self.state
    }
    pub fn begin_pairing(&mut self) -> Result<()> {
        self.transition(SessionState::Idle, SessionState::Pairing)
    }
    /// 用户显式发起连接，可以从初始、已断开或失败状态重新开始配对。
    pub fn begin_connection(&mut self) -> Result<()> {
        if !matches!(
            self.state,
            SessionState::Idle | SessionState::Stopped | SessionState::Failed
        ) {
            return Err(CoreError::InvalidState("session already connected"));
        }
        self.state = SessionState::Pairing;
        self.events.push_back(SessionEvent::State(self.state));
        Ok(())
    }
    pub fn pairing_verified(&mut self) -> Result<()> {
        self.transition(SessionState::Pairing, SessionState::Connecting)
    }
    pub fn stream_ready(&mut self) -> Result<()> {
        self.transition(SessionState::Connecting, SessionState::Streaming)
    }
    pub fn stop(&mut self) {
        if self.state != SessionState::Stopped {
            self.state = SessionState::Stopped;
            self.events.push_back(SessionEvent::State(self.state));
        }
    }
    pub fn fail(&mut self, code: i32) {
        self.state = SessionState::Failed;
        self.events.push_back(SessionEvent::State(self.state));
        self.events.push_back(SessionEvent::Error(code));
    }
    pub fn reconnect(&mut self) {
        if self.state != SessionState::Stopped {
            self.state = SessionState::Connecting;
            self.events.push_back(SessionEvent::State(self.state));
        }
    }
    pub fn set_local_volume(&mut self, db: f32) -> Result<()> {
        if !(-144.0..=0.0).contains(&db) {
            return Err(CoreError::InvalidArgument);
        }
        if self.state != SessionState::Streaming {
            return Err(CoreError::InvalidState("volume requires streaming"));
        }
        self.volume_db = db;
        Ok(())
    }
    pub fn receive_remote_volume(&mut self, db: f32) -> Result<()> {
        if !(-144.0..=0.0).contains(&db) {
            return Err(CoreError::Protocol("invalid volume"));
        }
        self.volume_db = db;
        self.events.push_back(SessionEvent::Volume(db));
        Ok(())
    }
    pub fn pop_event(&mut self) -> Option<SessionEvent> {
        self.events.pop_front()
    }
    fn transition(&mut self, expected: SessionState, next: SessionState) -> Result<()> {
        if self.state != expected {
            return Err(CoreError::InvalidState("unexpected protocol transition"));
        }
        self.state = next;
        self.events.push_back(SessionEvent::State(next));
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn event_order_and_volume_are_stable() {
        let mut s = Session::default();
        s.begin_pairing().unwrap();
        s.pairing_verified().unwrap();
        s.stream_ready().unwrap();
        s.receive_remote_volume(-16.).unwrap();
        assert_eq!(
            s.pop_event(),
            Some(SessionEvent::State(SessionState::Pairing))
        );
        assert_eq!(
            s.pop_event(),
            Some(SessionEvent::State(SessionState::Connecting))
        );
        assert_eq!(
            s.pop_event(),
            Some(SessionEvent::State(SessionState::Streaming))
        );
        assert_eq!(s.pop_event(), Some(SessionEvent::Volume(-16.)));
    }

    #[test]
    fn failed_session_can_report_reconnecting() {
        let mut session = Session::default();
        session.begin_pairing().unwrap();
        session.fail(-5);
        session.reconnect();
        assert_eq!(session.state(), SessionState::Connecting);
        assert_eq!(
            session.pop_event(),
            Some(SessionEvent::State(SessionState::Pairing))
        );
        assert_eq!(
            session.pop_event(),
            Some(SessionEvent::State(SessionState::Failed))
        );
        assert_eq!(session.pop_event(), Some(SessionEvent::Error(-5)));
        assert_eq!(
            session.pop_event(),
            Some(SessionEvent::State(SessionState::Connecting))
        );
    }

    #[test]
    fn explicit_connect_can_restart_stopped_session() {
        let mut session = Session::default();
        session.begin_pairing().unwrap();
        session.stop();
        session.begin_connection().unwrap();
        assert_eq!(session.state(), SessionState::Pairing);
    }
}
