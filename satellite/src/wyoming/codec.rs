use crate::wyoming::event::{WyomingEvent, PROTOCOL_VERSION};
use serde_json::{Map, Value};
use tokio::io::{AsyncBufReadExt, AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, BufReader};

/// Read one Wyoming event. Returns Ok(None) on clean EOF.
#[allow(dead_code)] // test-only convenience; production reads via read_event_buffered
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

/// Cap on header-announced lengths, mirroring the hub reader's MaxFrameBytes (64 MiB):
/// a corrupt header must not drive an unbounded allocation.
const MAX_FRAME_BYTES: usize = 64 * 1024 * 1024;

/// Same framing as read_event, but over a persistent BufReader so buffered bytes survive
/// across calls — REQUIRED on a real socket (read_event re-wraps and would drop them).
/// Also skips blank header lines (the hub reader tolerates them; be symmetric).
pub async fn read_event_buffered<R>(buf: &mut BufReader<R>) -> anyhow::Result<Option<WyomingEvent>>
where
    R: AsyncRead + Unpin,
{
    let mut line = String::new();
    loop {
        line.clear();
        if buf.read_line(&mut line).await? == 0 {
            return Ok(None);
        }
        if !line.trim_end().is_empty() {
            break;
        }
    }
    let header: Value = serde_json::from_str(line.trim_end())?;
    let event_type = header.get("type").and_then(|v| v.as_str()).unwrap_or_default().to_string();
    let data_length = header.get("data_length").and_then(|v| v.as_u64()).unwrap_or(0) as usize;
    let payload_length = header.get("payload_length").and_then(|v| v.as_u64()).unwrap_or(0) as usize;
    anyhow::ensure!(
        data_length <= MAX_FRAME_BYTES && payload_length <= MAX_FRAME_BYTES,
        "frame too large: data_length={data_length} payload_length={payload_length}"
    );
    let inline = matches!(header.get("data"), Some(Value::Object(_)));
    let mut data = if inline { header.get("data").cloned() } else { None };
    if data_length > 0 {
        let mut db = vec![0u8; data_length];
        buf.read_exact(&mut db).await?;
        if !inline { data = Some(serde_json::from_slice(&db)?); }
    }
    let mut payload = vec![0u8; payload_length];
    if payload_length > 0 {
        buf.read_exact(&mut payload).await?;
    }
    Ok(Some(WyomingEvent { event_type, data, payload }))
}

/// Write one Wyoming event: header line + (optional) data bytes + (optional) payload, as ONE
/// contiguous buffer — one write syscall and (with NODELAY set) one TCP segment per frame
/// instead of three of each on the 12.5 frames/s mic hot path.
pub async fn write_event<W>(writer: &mut W, event: &WyomingEvent) -> anyhow::Result<()>
where
    W: AsyncWrite + Unpin,
{
    let frame = encode_frame(event)?;
    writer.write_all(&frame).await?;
    writer.flush().await?;
    Ok(())
}

/// `data` goes once, as the data_length body: the hub's reader parses the body whenever
/// data_length > 0 and ignores an inline header copy, and the hub's own writer emits exactly
/// this shape — the previous inline duplicate cost a Value deep-clone plus wire bytes per frame.
fn encode_frame(event: &WyomingEvent) -> anyhow::Result<Vec<u8>> {
    let data_bytes: Vec<u8> = match &event.data {
        Some(v) => serde_json::to_vec(v)?,
        None => Vec::new(),
    };
    let mut header = Map::new();
    header.insert("type".into(), Value::String(event.event_type.clone()));
    header.insert("version".into(), Value::String(PROTOCOL_VERSION.into()));
    if !data_bytes.is_empty() {
        header.insert("data_length".into(), Value::from(data_bytes.len()));
    }
    if !event.payload.is_empty() {
        header.insert("payload_length".into(), Value::from(event.payload.len()));
    }
    let mut frame = serde_json::to_vec(&Value::Object(header))?;
    frame.reserve(1 + data_bytes.len() + event.payload.len());
    frame.push(b'\n');
    frame.extend_from_slice(&data_bytes);
    frame.extend_from_slice(&event.payload);
    Ok(frame)
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

    // Wire-format pin: one frame = header line + data body + payload, with `data` sent ONCE
    // as the data_length body. That matches the hub's own WyomingWriter; its reader parses the
    // body whenever data_length > 0 and ignores any inline copy (WyomingReader.cs), so the old
    // duplicated inline `data` was pure wire+CPU overhead.
    #[tokio::test]
    async fn frames_carry_data_as_body_not_inline() {
        let (mut a, mut b) = tokio::io::duplex(1 << 16);
        let e = WyomingEvent::audio_chunk(16000, 2, 1, vec![1, 2, 3]);
        write_event(&mut a, &e).await.unwrap();
        drop(a);
        let mut raw = Vec::new();
        use tokio::io::AsyncReadExt;
        b.read_to_end(&mut raw).await.unwrap();
        let nl = raw.iter().position(|&c| c == b'\n').unwrap();
        let header: Value = serde_json::from_slice(&raw[..nl]).unwrap();
        assert!(header.get("data").is_none(), "data must go once, as the body");
        let dl = header["data_length"].as_u64().unwrap() as usize;
        let body: Value = serde_json::from_slice(&raw[nl + 1..nl + 1 + dl]).unwrap();
        assert_eq!(body, json!({"rate":16000,"width":2,"channels":1}));
        assert_eq!(&raw[nl + 1 + dl..], &[1, 2, 3]);
    }

    #[tokio::test]
    async fn eof_returns_none() {
        let (a, mut b) = tokio::io::duplex(16);
        drop(a);
        assert!(read_event(&mut b).await.unwrap().is_none());
    }

    #[tokio::test]
    async fn buffered_reads_survive_across_calls() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        // two events written back-to-back must both be readable through ONE BufReader
        write_event(&mut a, &WyomingEvent::new("run-satellite")).await.unwrap();
        write_event(&mut a, &WyomingEvent::audio_chunk(16000, 2, 1, vec![1, 2, 3])).await.unwrap();
        let mut buf = tokio::io::BufReader::new(b);
        let e1 = read_event_buffered(&mut buf).await.unwrap().unwrap();
        let e2 = read_event_buffered(&mut buf).await.unwrap().unwrap();
        assert_eq!(e1.event_type, "run-satellite");
        assert_eq!(e2.event_type, "audio-chunk");
        assert_eq!(e2.payload, vec![1, 2, 3]);
    }

    #[tokio::test]
    async fn truncated_payload_is_an_error_not_eof() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        use tokio::io::AsyncWriteExt;
        // header announces 100 payload bytes but the peer dies after 3
        a.write_all(b"{\"type\":\"audio-chunk\",\"payload_length\":100}\n").await.unwrap();
        a.write_all(&[1, 2, 3]).await.unwrap();
        drop(a);
        let mut buf = tokio::io::BufReader::new(b);
        assert!(read_event_buffered(&mut buf).await.is_err());
    }

    #[tokio::test]
    async fn oversized_frame_is_rejected() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        use tokio::io::AsyncWriteExt;
        a.write_all(b"{\"type\":\"audio-chunk\",\"payload_length\":999999999}\n").await.unwrap();
        drop(a);
        let mut buf = tokio::io::BufReader::new(b);
        assert!(read_event_buffered(&mut buf).await.is_err());
    }

    #[tokio::test]
    async fn blank_lines_are_skipped() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        use tokio::io::AsyncWriteExt;
        a.write_all(b"\n\n{\"type\":\"run-satellite\"}\n").await.unwrap();
        let mut buf = tokio::io::BufReader::new(b);
        let e = read_event_buffered(&mut buf).await.unwrap().unwrap();
        assert_eq!(e.event_type, "run-satellite");
    }
}
