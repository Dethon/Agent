use tokio::io::{AsyncReadExt, BufReader};
use tokio::process::{Child, ChildStdout};

pub const CHUNK_SAMPLES: usize = 1280; // 80 ms @ 16 kHz

/// Spawns a mic command (e.g. `arecord -D <dev> -r 16000 -c 1 -f S16_LE -t raw`) and yields
/// fixed 80 ms chunks of raw S16LE bytes from its stdout. Bytes stay bytes end-to-end: the
/// streaming path forwards them to the hub verbatim, and only the wake detector decodes i16
/// (bytes_to_samples) — the old bytes->i16->bytes round-trip per chunk is gone.
pub struct MicCapture {
    _child: Child,
    out: BufReader<ChildStdout>,
}

/// Decode raw little-endian S16 PCM into samples (the wake detector's input format).
pub fn bytes_to_samples(bytes: &[u8]) -> Vec<i16> {
    bytes.chunks_exact(2).map(|b| i16::from_le_bytes([b[0], b[1]])).collect()
}

impl MicCapture {
    pub fn spawn(mic_command: &str) -> anyhow::Result<Self> {
        let mut child = crate::audio::build_command(mic_command)
            .stdout(std::process::Stdio::piped())
            .kill_on_drop(true)
            .spawn()?;
        let out = BufReader::new(child.stdout.take().expect("piped stdout"));
        Ok(Self { _child: child, out })
    }

    /// Returns Ok(None) when the mic stream ends (EOF). This does NOT
    /// distinguish a finished stream from a silently failed/absent device —
    /// callers treat it as fatal for the connection (the state machine logs
    /// and tears down). No handshake/timeout logic by design (v1).
    pub async fn next_chunk(&mut self) -> anyhow::Result<Option<Vec<u8>>> {
        let mut bytes = vec![0u8; CHUNK_SAMPLES * 2];
        let mut filled = 0;
        while filled < bytes.len() {
            let n = self.out.read(&mut bytes[filled..]).await?;
            if n == 0 {
                return Ok(None); // EOF (partial trailing data discarded)
            }
            filled += n;
        }
        Ok(Some(bytes))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn yields_80ms_byte_chunks_from_a_raw_stream() {
        // 3 chunks worth of int16 LE = 3*1280*2 bytes of zeros via `head -c`.
        let bytes = 3 * 1280 * 2;
        let cmd = format!("head -c {bytes} /dev/zero");
        let mut cap = MicCapture::spawn(&cmd).unwrap();
        let mut chunks = 0;
        while let Some(chunk) = cap.next_chunk().await.unwrap() {
            assert_eq!(chunk.len(), CHUNK_SAMPLES * 2, "raw LE bytes, no i16 round-trip");
            chunks += 1;
        }
        assert_eq!(chunks, 3);
    }

    #[tokio::test]
    async fn discards_partial_trailing_data() {
        // 2.5 chunks worth of int16 LE: 2 full chunks then a half chunk
        // that must be discarded at EOF.
        let bytes = 2 * 1280 * 2 + 1280;
        let cmd = format!("head -c {bytes} /dev/zero");
        let mut cap = MicCapture::spawn(&cmd).unwrap();
        let mut chunks = 0;
        while let Some(chunk) = cap.next_chunk().await.unwrap() {
            assert_eq!(chunk.len(), CHUNK_SAMPLES * 2);
            chunks += 1;
        }
        assert_eq!(chunks, 2);
    }

    #[test]
    fn bytes_to_samples_decodes_le_i16() {
        assert_eq!(bytes_to_samples(&[0x01, 0x00, 0xFF, 0xFF, 0x00, 0x80]), vec![1, -1, i16::MIN]);
    }
}
