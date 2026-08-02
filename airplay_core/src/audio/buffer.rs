use ringbuf::traits::Split;
use ringbuf::{HeapCons, HeapProd, HeapRb};

pub fn create_ring(capacity: usize) -> (HeapProd<f32>, HeapCons<f32>) {
    let rb = HeapRb::<f32>::new(capacity);
    rb.split()
}
