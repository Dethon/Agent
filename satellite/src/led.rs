//! Activity LED: the state machine publishes semantic LedState values on a watch channel;
//! a per-connection render task owns the hardware backend and maps states to light.
//! V1 policy: Idle -> off, everything else -> steady on.

/// Semantic satellite phase, published by the state machine. The render task — never the
/// state machine — decides what each phase looks like, so future blink patterns touch
/// only this module.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LedState { Idle, Listening, Thinking, Speaking }

/// V1 render constants: one fixed look, change here only. (--led-color is deferred.)
const LED_COUNT: usize = 3;                  // the HAT has exactly 3 APA102-2020s
const LED_COLOR: (u8, u8, u8) = (0, 0, 255); // RGB: blue
const LED_BRIGHTNESS: u8 = 8;                // APA102 global brightness, of 31

/// Full APA102 update for `n` daisy-chained LEDs, all set to the same color.
/// Layout: 32-bit zero start frame; per LED `0xE0|brightness(5-bit), B, G, R`;
/// 32-bit zero end frame (sufficient clock pulses for n <= 64; doubles as the SK9822 latch).
fn apa102_frame((r, g, b): (u8, u8, u8), brightness: u8, n: usize) -> Vec<u8> {
    let mut out = vec![0u8; 4];
    for _ in 0..n {
        out.extend_from_slice(&[0xE0 | (brightness & 0x1F), b, g, r]);
    }
    out.extend_from_slice(&[0, 0, 0, 0]);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden bytes: 4-byte zero start frame, per-LED 0xE0|brightness then B,G,R,
    // 4-byte zero end frame (>= n/2 clock pulses for n<=64, doubles as SK9822 latch).
    #[test]
    fn apa102_frame_golden_bytes() {
        let f = apa102_frame((0, 0, 255), 8, 3);
        assert_eq!(f.len(), 20);
        assert_eq!(&f[..4], &[0, 0, 0, 0]);
        for led in 0..3 {
            assert_eq!(&f[4 + led * 4..8 + led * 4], &[0xE8, 255, 0, 0]); // 0xE0|8, B, G, R
        }
        assert_eq!(&f[16..], &[0, 0, 0, 0]);
    }

    #[test]
    fn apa102_frame_masks_brightness_to_5_bits() {
        let f = apa102_frame((1, 2, 3), 0xFF, 1);
        assert_eq!(f[4], 0xFF); // 0xE0 | (0xFF & 0x1F) = 0xFF
        assert_eq!(&f[5..8], &[3, 2, 1]); // B, G, R order
    }
}
