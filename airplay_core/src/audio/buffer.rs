//! 采集线程与发送线程共享的 PCM 采样队列。
//! 容量固定且线程安全：队列满时自动丢弃最旧采样、保留最新，
//! 保证发送端处理的是接近实时的声音，不会把旧音频积压成持续时差。

use std::collections::VecDeque;
use std::sync::{Arc, Mutex, MutexGuard};

#[derive(Debug, Clone)]
pub struct AudioSampleBuffer {
    inner: Arc<Mutex<VecDeque<f32>>>,
    capacity: usize,
}

impl AudioSampleBuffer {
    pub fn new(capacity: usize) -> Self {
        Self {
            inner: Arc::new(Mutex::new(VecDeque::with_capacity(capacity))),
            capacity,
        }
    }

    fn queue(&self) -> MutexGuard<'_, VecDeque<f32>> {
        // 采集线程不应 panic；即使发生，也继续使用损坏的队列而不是
        // 让发送线程跟着 panic，保证 FFI 边界稳定。
        self.inner
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// 从队尾批量写入采样；队列满时先丢弃队头最旧的采样。
    pub fn push(&self, samples: &[f32]) {
        let mut queue = self.queue();
        for &sample in samples {
            if queue.len() == self.capacity {
                queue.pop_front();
            }
            queue.push_back(sample);
        }
    }

    /// 写入一段静音采样，用于 WASAPI 报告的静音缓冲。
    pub fn push_zeros(&self, count: usize) {
        let mut queue = self.queue();
        for _ in 0..count {
            if queue.len() == self.capacity {
                queue.pop_front();
            }
            queue.push_back(0.0);
        }
    }

    /// 取出队头采样，空队列返回 None。
    pub fn try_pop(&self) -> Option<f32> {
        self.queue().pop_front()
    }

    /// 一次性取走当前积压的全部采样，发送线程每轮调用一次。
    pub fn drain(&self) -> Vec<f32> {
        self.queue().drain(..).collect()
    }

    /// 丢弃当前积压的全部采样，用于建流完成后清掉旧音频。
    pub fn clear(&self) {
        self.queue().clear();
    }

    /// 当前积压的采样数（诊断用，便于观测发送端落后量）。
    pub fn len(&self) -> usize {
        self.queue().len()
    }

    /// 当前是否为空。
    pub fn is_empty(&self) -> bool {
        self.queue().is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keeps_newest_when_full() {
        let buffer = AudioSampleBuffer::new(4);
        buffer.push(&[1.0, 2.0, 3.0, 4.0]);
        buffer.push(&[5.0]);
        assert_eq!(buffer.drain(), vec![2.0, 3.0, 4.0, 5.0]);
    }

    #[test]
    fn clear_empties_queue() {
        let buffer = AudioSampleBuffer::new(4);
        buffer.push(&[1.0, 2.0]);
        buffer.clear();
        assert!(buffer.try_pop().is_none());
        assert_eq!(buffer.len(), 0);
    }
}
