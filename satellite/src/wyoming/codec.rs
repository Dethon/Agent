use crate::wyoming::event::{WyomingEvent, PROTOCOL_VERSION};
use serde_json::{Map, Value};
use tokio::io::{AsyncBufReadExt, AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, BufReader};

/// Read one Wyoming event. Returns Ok(None) on clean EOF.
pub async fn read_event<R>(reader: &mut R) -> anyhow::Result<Option<WyomingEvent>>
where
    R: AsyncRead + Unpin,
{
    // NOTE: callers should wrap the socket in a BufReader once and reuse it; this helper
    // reads a line then exact bytes. For simplicity we accept any AsyncRead and buffer the line.
    let mut buf = BufReader::new(reader);
    let mut line = String::new();
    let n = buf.read_line(&mut line).await?;
    if n == 0 {
        return Ok(None);
    }
    let header: Value = serde_json::from_str(line.trim_end())?;
    let event_type = header.get("type").and_then(|v| v.as_str()).unwrap_or_default().to_string();
    let data_length = header.get("data_length").and_then(|v| v.as_u64()).unwrap_or(0) as usize;
    let payload_length = header.get("payload_length").and_then(|v| v.as_u64()).unwrap_or(0) as usize;

    // Prefer inline `data`; fall back to reading data_length body bytes.
    let mut data: Option<Value> = match header.get("data") {
        Some(Value::Object(m)) => Some(Value::Object(m.clone())),
        _ => None,
    };
    if data.is_none() && data_length > 0 {
        let mut db = vec![0u8; data_length];
        buf.read_exact(&mut db).await?;
        data = Some(serde_json::from_slice(&db)?);
    } else if data.is_some() && data_length > 0 {
        // Some peers (incl. the hub) also write the data bytes after the header; consume them.
        let mut db = vec![0u8; data_length];
        buf.read_exact(&mut db).await?;
    }

    let mut payload = vec![0u8; payload_length];
    if payload_length > 0 {
        buf.read_exact(&mut payload).await?;
    }
    Ok(Some(WyomingEvent { event_type, data, payload }))
}

/// Write one Wyoming event: header line + (optional) data bytes + (optional) payload.
pub async fn write_event<W>(writer: &mut W, event: &WyomingEvent) -> anyhow::Result<()>
where
    W: AsyncWrite + Unpin,
{
    let data_bytes: Vec<u8> = match &event.data {
        Some(v) => serde_json::to_vec(v)?,
        None => Vec::new(),
    };
    let mut header = Map::new();
    header.insert("type".into(), Value::String(event.event_type.clone()));
    header.insert("version".into(), Value::String(PROTOCOL_VERSION.into()));
    if let Some(v) = &event.data {
        header.insert("data".into(), v.clone());
    }
    if !data_bytes.is_empty() {
        header.insert("data_length".into(), Value::from(data_bytes.len()));
    }
    if !event.payload.is_empty() {
        header.insert("payload_length".into(), Value::from(event.payload.len()));
    }
    let mut line = serde_json::to_vec(&Value::Object(header))?;
    line.push(b'\n');
    writer.write_all(&line).await?;
    if !data_bytes.is_empty() {
        writer.write_all(&data_bytes).await?;
    }
    if !event.payload.is_empty() {
        writer.write_all(&event.payload).await?;
    }
    writer.flush().await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::wyoming::WyomingEvent;
    use serde_json::json;

    #[tokio::test]
    async fn roundtrip_header_only() {
        let (mut a, mut b) = tokio::io::duplex(4096);
        write_event(&mut a, &WyomingEvent::new("run-satellite")).await.unwrap();
        let got = read_event(&mut b).await.unwrap().unwrap();
        assert_eq!(got.event_type, "run-satellite");
        assert!(got.payload.is_empty());
    }

    #[tokio::test]
    async fn roundtrip_data_and_payload() {
        let (mut a, mut b) = tokio::io::duplex(4096);
        let e = WyomingEvent::audio_chunk(16000, 2, 1, vec![9, 8, 7, 6, 5]);
        write_event(&mut a, &e).await.unwrap();
        let got = read_event(&mut b).await.unwrap().unwrap();
        assert_eq!(got.event_type, "audio-chunk");
        assert_eq!(got.data.unwrap(), json!({"rate":16000,"width":2,"channels":1}));
        assert_eq!(got.payload, vec![9, 8, 7, 6, 5]);
    }

    #[tokio::test]
    async fn eof_returns_none() {
        let (a, mut b) = tokio::io::duplex(16);
        drop(a);
        assert!(read_event(&mut b).await.unwrap().is_none());
    }
}
