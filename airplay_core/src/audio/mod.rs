pub mod alac;
pub mod buffer;
pub mod capture;
pub mod device;
pub mod processing;
pub mod wav_writer;

pub use alac::encode_uncompressed_stereo;
pub use buffer::create_ring;
pub use capture::{WasapiCapture, run_capture_loop};
pub use processing::{
    AIRPLAY_SAMPLE_RATE, ALAC_FRAMES_PER_PACKET, InputPcmFormat, PcmConverter, SampleEncoding,
};
pub use wav_writer::write_to_wav;
