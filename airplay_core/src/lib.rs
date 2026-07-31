pub mod audio;

// 原有的FFI导出函数
#[unsafe(no_mangle)]
pub extern "C" fn airplay_core_init() -> i32 {
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn airplay_core_version() -> *const std::os::raw::c_char {
    static VERSION: &str = "0.1.0\0";
    VERSION.as_ptr() as *const std::os::raw::c_char
}