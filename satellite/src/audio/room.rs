use std::collections::VecDeque;

/// How loud the room is when nobody is talking to the satellite.
///
/// The hub's end-of-utterance gate needs a noise floor, and it can only measure one from audio
/// inside the capture it is gating — which, when the user runs straight from the wake word into
/// their command, is nothing but the user. Measured in the field at 6x the true room, that floor
/// armed the hub's noisy-room regime in a silent office and the turn ended mid-sentence. This
/// satellite has what the hub lacks: it hears the room continuously while idle, and that audio
/// contains neither the capture nor anything the hub can otherwise reach.
///
/// The statistic is the hub's own, so the two numbers mean the same thing: the minimum of smoothed
/// ENERGY over a trailing window. Smoothing first is what makes it survive bursty backgrounds — TV
/// dialog drops to room silence for 100-400 ms between phrases, and a raw per-chunk minimum
/// latches onto those lulls and reports a quiet room with the TV on.
///
/// The wake word needs no special handling despite sitting at the very end of the window: a
/// minimum cannot be raised by loud audio. What the reading must not include is the turn itself,
/// so the state machine resets this when a turn starts — every reading then describes idle audio
/// measured since the last turn ended, and a satellite that has not been idle long enough reports
/// nothing rather than something stale.
pub struct RoomLevel {
    smoothing_chunks: usize,
    window_chunks: usize,
    energies: VecDeque<f64>,
    smoothed_db: VecDeque<f64>,
}

/// Mic chunks are a fixed 80 ms (16 kHz, 1280 samples) — the audio contract the whole crate is
/// built on — so windows are counted in chunks rather than tracked in time.
const CHUNK_MS: usize = 80;

impl RoomLevel {
    pub fn new(smoothing_ms: usize, window_ms: usize) -> Self {
        Self {
            smoothing_chunks: (smoothing_ms / CHUNK_MS).max(1),
            window_chunks: (window_ms / CHUNK_MS).max(1),
            energies: VecDeque::new(),
            smoothed_db: VecDeque::new(),
        }
    }

    pub fn push(&mut self, samples: &[i16]) {
        if samples.is_empty() {
            return;
        }
        let energy = samples.iter().map(|&v| v as f64 * v as f64).sum::<f64>() / samples.len() as f64;
        self.energies.push_back(energy);
        while self.energies.len() > self.smoothing_chunks {
            self.energies.pop_front();
        }
        if self.energies.len() < self.smoothing_chunks {
            return;
        }
        let mean = self.energies.iter().sum::<f64>() / self.energies.len() as f64;
        self.smoothed_db.push_back(10.0 * mean.max(1.0).log10());
        while self.smoothed_db.len() > self.window_chunks {
            self.smoothed_db.pop_front();
        }
    }

    /// Discards everything measured so far. Called when a turn starts: from here the mic carries
    /// the user and, on a music unit, ducked playback — neither is the room.
    pub fn reset(&mut self) {
        self.energies.clear();
        self.smoothed_db.clear();
    }

    /// i16-amplitude units, directly comparable with `wake_rms` and with the hub's own floor.
    /// `None` until a full window of idle audio stands behind the reading.
    pub fn rms(&self) -> Option<f32> {
        if self.smoothed_db.len() < self.window_chunks {
            return None;
        }
        let min = self.smoothed_db.iter().copied().reduce(f64::min)?;
        Some(10f64.powf(min / 20.0) as f32)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn feed(room: &mut RoomLevel, level: i16, chunks: usize) {
        for _ in 0..chunks {
            room.push(&vec![level; 1280]);
        }
    }

    #[test]
    fn reports_the_quiet_room_under_loud_bursts() {
        let mut room = RoomLevel::new(480, 1600);
        feed(&mut room, 60, 25); // the room
        feed(&mut room, 8000, 10); // someone talks in the room, a door, the wake word itself

        let rms = room.rms().unwrap();
        assert!((rms - 60.0).abs() < 2.0, "expected the room (60), got {rms}");
    }

    #[test]
    fn bursty_background_with_sub_window_lulls_does_not_read_as_a_quiet_room() {
        // TV dialog pauses for 100-400 ms between phrases. Smoothing energy over ~500 ms is what
        // keeps those lulls from being reported as the room — the same fix the hub's floor needed
        // (field measurement 2026-07-20: a raw minimum read 72-97 RMS with the TV on).
        let tv = |room: &mut RoomLevel| {
            for _ in 0..8 {
                feed(room, 2000, 4); // phrase
                feed(room, 30, 2); // 160 ms lull
            }
        };

        let mut smoothed = RoomLevel::new(480, 1600);
        tv(&mut smoothed);
        let mut raw = RoomLevel::new(80, 1600); // one chunk per smoothing window: no smoothing
        tv(&mut raw);

        assert!(smoothed.rms().unwrap() > 900.0, "TV read as room: {:?}", smoothed.rms());
        assert!(raw.rms().unwrap() < 100.0, "the raw minimum is what smoothing exists to beat");
    }

    #[test]
    fn no_reading_until_a_full_window_of_idle_audio() {
        let mut room = RoomLevel::new(480, 1600);
        assert_eq!(room.rms(), None);

        feed(&mut room, 60, 12); // 960 ms: short of the window
        assert_eq!(room.rms(), None);

        feed(&mut room, 60, 20);
        assert!(room.rms().is_some());
    }

    #[test]
    fn reset_drops_the_reading_so_a_turns_audio_is_never_the_room() {
        let mut room = RoomLevel::new(480, 1600);
        feed(&mut room, 60, 30);
        assert!(room.rms().is_some());

        room.reset();

        assert_eq!(room.rms(), None);
    }

    #[test]
    fn digital_silence_reads_as_silence_not_as_negative_infinity() {
        let mut room = RoomLevel::new(480, 1600);
        feed(&mut room, 0, 30);

        assert_eq!(room.rms(), Some(1.0));
    }
}
