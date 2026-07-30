#[unsafe(no_mangle)]
pub extern "C" fn airplay_core_init() -> i32 {
    println!("[Rust Core] AirBlade Core Engine initialized successfully!");
    0
}