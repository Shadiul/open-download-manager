//! A token-bucket speed limiter, shared by every connection it covers.

use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use crate::lock;

pub struct RateLimiter {
    /// Bytes per second; 0 means no limit.
    rate: AtomicU64,
    bucket: Mutex<Bucket>,
}

struct Bucket {
    /// May go below zero: a large chunk is let through at once and paid back by waiting.
    tokens: f64,
    last: Instant,
}

impl Default for RateLimiter {
    fn default() -> Self {
        Self::new(0)
    }
}

impl RateLimiter {
    pub fn new(rate: u64) -> Self {
        Self { rate: AtomicU64::new(rate), bucket: Mutex::new(Bucket { tokens: 0.0, last: Instant::now() }) }
    }

    pub fn set_rate(&self, rate: u64) {
        self.rate.store(rate, Ordering::Relaxed);
    }

    pub fn rate(&self) -> u64 {
        self.rate.load(Ordering::Relaxed)
    }

    /// Waits until `n` bytes may pass.
    pub async fn acquire(&self, n: u64) {
        loop {
            let wait = self.try_take(n, Instant::now());
            match wait {
                None => return,
                // Short sleeps, so a raised or removed limit takes effect quickly.
                Some(d) => tokio::time::sleep(d.min(Duration::from_millis(250))).await,
            }
        }
    }

    /// Takes `n` tokens if the bucket is not in debt, else returns how long to wait.
    fn try_take(&self, n: u64, now: Instant) -> Option<Duration> {
        let rate = self.rate();
        if rate == 0 {
            return None;
        }
        let rate = rate as f64;
        let mut b = lock(&self.bucket);
        let elapsed = now.saturating_duration_since(b.last).as_secs_f64();
        b.last = now;
        // At most a quarter second of burst.
        b.tokens = (b.tokens + elapsed * rate).min(rate / 4.0);
        if b.tokens >= 0.0 {
            b.tokens -= n as f64;
            None
        } else {
            Some(Duration::from_secs_f64(-b.tokens / rate))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unlimited_never_waits() {
        let l = RateLimiter::new(0);
        assert_eq!(l.try_take(u64::MAX, Instant::now()), None);
    }

    #[test]
    fn debt_is_paid_back_by_waiting() {
        let l = RateLimiter::new(1000);
        let t0 = Instant::now();
        assert_eq!(l.try_take(500, t0), None, "first chunk passes");
        let wait = l.try_take(500, t0).expect("now in debt");
        assert!((wait.as_secs_f64() - 0.5).abs() < 0.01, "{wait:?}");
        assert_eq!(l.try_take(500, t0 + Duration::from_millis(500)), None, "debt paid");
    }

    #[tokio::test]
    async fn holds_roughly_to_the_rate() {
        let l = RateLimiter::new(200_000);
        let start = Instant::now();
        for _ in 0..10 {
            l.acquire(20_000).await;
        }
        // 200 KB at 200 KB/s, with the first chunk free: about 0.9 s.
        let took = start.elapsed().as_secs_f64();
        assert!((0.7..1.5).contains(&took), "took {took}");
    }
}
