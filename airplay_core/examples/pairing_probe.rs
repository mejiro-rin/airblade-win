use airplay_core::pairing_client::complete_transient_pairing;
use std::{net::SocketAddr, str::FromStr};

fn main() {
    let address = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "192.168.3.24:7000".to_owned());
    let address =
        SocketAddr::from_str(&address).expect("地址格式应为 IP:端口，例如 192.168.3.24:7000");
    match complete_transient_pairing(address) {
        Ok(keys) => println!(
            "HomePod transient 配对成功：共享密钥 {} 字节，控制写密钥 {} 字节，控制读密钥 {} 字节。",
            keys.shared_secret.len(),
            keys.control_write_key.len(),
            keys.control_read_key.len()
        ),
        Err(error) => {
            eprintln!("配对 M1-M4 失败：{error}");
            std::process::exit(1);
        }
    }
}
