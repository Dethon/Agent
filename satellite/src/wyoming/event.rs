use serde_json::{json, Map, Value};

pub const PROTOCOL_VERSION: &str = "1.2"; // matches the hub's WyomingWriter

#[derive(Debug, Clone)]
pub struct WyomingEvent {
    pub event_type: String,
    pub data: Option<Value>,
    pub payload: Vec<u8>,
}

impl WyomingEvent {
    pub fn new(event_type: &str) -> Self {
        Self { event_type: event_type.to_string(), data: None, payload: Vec::new() }
    }
    pub fn with_data(event_type: &str, data: Value) -> Self {
        Self { event_type: event_type.to_string(), data: Some(data), payload: Vec::new() }
    }
    pub fn audio_chunk(rate: i64, width: i64, channels: i64, payload: Vec<u8>) -> Self {
        Self {
            event_type: "audio-chunk".to_string(),
            data: Some(json!({ "rate": rate, "width": width, "channels": channels })),
            payload,
        }
    }
    pub fn data_obj(&self) -> Map<String, Value> {
        match &self.data {
            Some(Value::Object(m)) => m.clone(),
            _ => Map::new(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn run_pipeline_has_type_and_no_payload() {
        let e = WyomingEvent::new("run-pipeline");
        assert_eq!(e.event_type, "run-pipeline");
        assert!(e.data.is_none());
        assert!(e.payload.is_empty());
    }
    #[test]
    fn audio_chunk_carries_data_and_payload() {
        let e = WyomingEvent::audio_chunk(16000, 2, 1, vec![1, 2, 3, 4]);
        assert_eq!(e.event_type, "audio-chunk");
        assert_eq!(e.data.as_ref().unwrap()["rate"], serde_json::json!(16000));
        assert_eq!(e.payload, vec![1, 2, 3, 4]);
    }
}
