//! 通过局域网 mDNS 发现可作为 AirPlay 音频接收端的 HomePod。
use std::{
    collections::HashMap,
    net::IpAddr,
    time::{Duration, Instant},
};

use mdns_sd::{ResolvedService, ServiceDaemon, ServiceEvent};

use crate::error::{CoreError, Result};

pub const RAOP_SERVICE_TYPE: &str = "_raop._tcp.local.";
pub const AIRPLAY_SERVICE_TYPE: &str = "_airplay._tcp.local.";

/// 连接与配对所需的接收端信息。立体声组将来同样用此结构表示。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AirPlayTarget {
    pub device_id: String,
    pub display_name: String,
    pub hostname: String,
    pub addresses: Vec<IpAddr>,
    pub port: u16,
    pub airplay_port: Option<u16>,
    pub model: Option<String>,
    pub features: Option<String>,
    pub status_flags: Option<String>,
}

/// 在指定时间内收集所有 AirPlay 音频接收端广播的 `_raop._tcp.local.` 服务。
/// 先保留所有已解析的服务，不能因为某台设备缺少 `model` TXT 字段而被隐藏。
/// 此实现直接收发 mDNS 数据包，不要求安装或运行 Bonjour。
pub fn discover_airplay_targets(timeout: Duration) -> Result<Vec<AirPlayTarget>> {
    if timeout.is_zero() {
        return Err(CoreError::InvalidArgument);
    }
    let daemon = ServiceDaemon::new().map_err(|e| CoreError::Discovery(e.to_string()))?;
    let raop_receiver = daemon
        .browse(RAOP_SERVICE_TYPE)
        .map_err(|e| CoreError::Discovery(e.to_string()))?;
    let airplay_receiver = daemon
        .browse(AIRPLAY_SERVICE_TYPE)
        .map_err(|e| CoreError::Discovery(e.to_string()))?;
    let deadline = Instant::now() + timeout;
    let mut raop_targets = HashMap::new();
    let mut airplay_metadata = HashMap::new();
    while let Some(remaining) = deadline.checked_duration_since(Instant::now()) {
        let wait = remaining.min(Duration::from_millis(100));
        if let Ok(ServiceEvent::ServiceResolved(info)) = raop_receiver.recv_timeout(wait) {
            let target = target_from_raop(&info);
            raop_targets.insert(target.device_id.clone(), target);
        }
        while let Ok(event) = airplay_receiver.try_recv() {
            if let ServiceEvent::ServiceResolved(info) = event
                && let Some(metadata) = metadata_from_airplay(&info)
            {
                airplay_metadata.insert(metadata.device_id.clone(), metadata);
            }
        }
    }
    let _ = daemon.stop_browse(RAOP_SERVICE_TYPE);
    let _ = daemon.stop_browse(AIRPLAY_SERVICE_TYPE);
    let _ = daemon.shutdown();
    Ok(raop_targets
        .into_values()
        .map(|mut target| {
            if let Some(metadata) = airplay_metadata.get(&target.device_id) {
                target.airplay_port = Some(metadata.port);
                target.model = metadata.model.clone().or(target.model);
                target.features = metadata.features.clone().or(target.features);
                target.status_flags = metadata.status_flags.clone().or(target.status_flags);
            }
            target
        })
        .collect())
}

/// 仅返回已明确标记为 HomePod 的目标。
pub fn discover_homepods(timeout: Duration) -> Result<Vec<AirPlayTarget>> {
    Ok(discover_airplay_targets(timeout)?
        .into_iter()
        .filter(is_homepod)
        .collect())
}

fn target_from_raop(info: &ResolvedService) -> AirPlayTarget {
    let instance = info
        .get_fullname()
        .trim_end_matches('.')
        .split("._raop._tcp")
        .next()
        .unwrap_or_default();
    let (device_id, display_name) = instance.split_once('@').unwrap_or((instance, instance));
    let properties = info.get_properties();
    let mut addresses: Vec<_> = info
        .get_addresses()
        .iter()
        .map(|ip| ip.to_ip_addr())
        .collect();
    addresses.sort();
    AirPlayTarget {
        device_id: device_id.to_owned(),
        display_name: display_name.to_owned(),
        hostname: info.get_hostname().trim_end_matches('.').to_owned(),
        addresses,
        port: info.get_port(),
        airplay_port: None,
        model: txt(properties, "model"),
        features: txt(properties, "features"),
        status_flags: txt(properties, "sf"),
    }
}

struct AirPlayMetadata {
    device_id: String,
    port: u16,
    model: Option<String>,
    features: Option<String>,
    status_flags: Option<String>,
}

/// `_airplay` 广播中的设备 ID 使用冒号分隔，RAOP 实例名没有冒号；统一后才能关联。
fn normalize_device_id(value: &str) -> String {
    value
        .chars()
        .filter(|c| c.is_ascii_hexdigit())
        .flat_map(char::to_uppercase)
        .collect()
}

fn metadata_from_airplay(info: &ResolvedService) -> Option<AirPlayMetadata> {
    let properties = info.get_properties();
    let device_id = normalize_device_id(properties.get_property_val_str("deviceid")?);
    if device_id.is_empty() {
        return None;
    }
    Some(AirPlayMetadata {
        device_id,
        port: info.get_port(),
        model: txt(properties, "model"),
        features: txt(properties, "features"),
        status_flags: txt(properties, "sf"),
    })
}

fn txt(properties: &mdns_sd::TxtProperties, key: &str) -> Option<String> {
    properties
        .get_property_val_str(key)
        .map(str::to_owned)
        .filter(|s| !s.is_empty())
}

pub fn is_homepod(target: &AirPlayTarget) -> bool {
    target
        .model
        .as_deref()
        .is_some_and(|model| model.starts_with("AudioAccessory"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn raop_instance_splits_device_id_and_name() {
        let full = "AABBCCDDEEFF@客厅 HomePod._raop._tcp.local";
        let instance = full.split("._raop._tcp").next().unwrap();
        let (id, name) = instance.split_once('@').unwrap();
        assert_eq!(id, "AABBCCDDEEFF");
        assert_eq!(name, "客厅 HomePod");
    }
    #[test]
    fn homepod_models_are_selected() {
        let target = AirPlayTarget {
            device_id: "id".into(),
            display_name: "HomePod".into(),
            hostname: "host".into(),
            addresses: Vec::new(),
            port: 7000,
            airplay_port: Some(7001),
            model: Some("AudioAccessory5,1".into()),
            features: None,
            status_flags: None,
        };
        assert!(is_homepod(&target));
    }
    #[test]
    fn device_ids_normalize_before_matching() {
        assert_eq!(normalize_device_id("06:B1:CC:F7:1E:CB"), "06B1CCF71ECB");
    }
}
