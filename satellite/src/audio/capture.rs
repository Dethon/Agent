use tokio::io::{AsyncReadExt, BufReader};
use tokio::process::{Child, ChildStdout, Command};

pub const CHUNK_SAMPLES: usize = 1280; // 80 ms @ 16 kHz

/// Spawns a mic command (e.g. `arecord -D <dev> -r 16000 -c 1 -f S16_LE -t raw`)
/// and yields fixed 1280-sample i16 chunks from its stdout.
pub struct MicCapture {
    _child: Child,
    out: BufReader<ChildStdout>,
}

impl MicCapture {
    pub fn spawn(mic_command: &str) -> anyhow::Result<Self> {
        let mut child = Command::new("sh")
            .arg("-c")
            .arg(mic_command)
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
    pub async fn next_chunk(&mut self) -> anyhow::Result<Option<Vec<i16>>> {
        let mut bytes = [0u8; CHUNK_SAMPLES * 2];
        let mut filled = 0;
        while filled < bytes.len() {
            let n = self.out.read(&mut bytes[filled..]).await?;
            if n == 0 {
                return Ok(None); // EOF (partial trailing data discarded)
            }
            filled += n;
        }
        let samples = bytes
            .chunks_exact(2)
            .map(|b| i16::from_le_bytes([b[0], b[1]]))
            .collect();
        Ok(Some(samples))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn yields_1280_sample_chunks_from_a_raw_stream() {
        // 3 chunks worth of int16 LE = 3*1280*2 bytes of zeros via `head -c`.
        let bytes = 3 * 1280 * 2;
        let cmd = format!("head -c {bytes} /dev/zero");
        let mut cap = MicCapture::spawn(&cmd).unwrap();
        let mut chunks = 0;
        while let Some(chunk) = cap.next_chunk().await.unwrap() {
            assert_eq!(chunk.len(), 1280);
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
            assert_eq!(chunk.len(), 1280);
            chunks += 1;
        }
        assert_eq!(chunks, 2);
    }
}
