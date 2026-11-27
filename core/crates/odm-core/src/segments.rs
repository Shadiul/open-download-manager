//! Planning the byte ranges each connection downloads, and dynamic splitting.
//!
//! A download starts with one range per connection. When a connection finishes its range, it
//! takes the second half of the largest range still in progress, so every connection stays busy
//! to the end instead of waiting on one slow tail.

/// Default number of connections per file (user setting, 1 to 32).
pub const DEFAULT_CONNECTIONS: usize = 8;
/// Most connections allowed per file.
pub const MAX_CONNECTIONS: usize = 32;
/// Ranges smaller than this are not split further.
pub const DEFAULT_MIN_SPLIT: u64 = 1024 * 1024;

/// A half-open byte range `[start, end)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ByteRange {
    pub start: u64,
    pub end: u64,
}

impl ByteRange {
    pub fn new(start: u64, end: u64) -> Self {
        debug_assert!(start <= end, "range {start}..{end} is backwards");
        Self { start, end }
    }

    pub fn len(&self) -> u64 {
        self.end - self.start
    }

    pub fn is_empty(&self) -> bool {
        self.start == self.end
    }

    /// The value for an HTTP `Range` header, which uses an inclusive end.
    pub fn header_value(&self) -> String {
        format!("bytes={}-{}", self.start, self.end - 1)
    }
}

/// One segment's saved progress: it owns `[start, end)` and has fetched `[start, cursor)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct SegmentRow {
    pub start: u64,
    pub cursor: u64,
    pub end: u64,
}

impl SegmentRow {
    pub fn fresh(range: ByteRange) -> Self {
        Self { start: range.start, cursor: range.start, end: range.end }
    }

    pub fn is_done(&self) -> bool {
        self.cursor >= self.end
    }
}

/// Splits a file of `total` bytes into contiguous ranges, one per connection, with no range
/// shorter than `min_split` unless the whole file is.
pub fn initial_plan(total: u64, connections: usize, min_split: u64) -> Vec<ByteRange> {
    if total == 0 {
        return Vec::new();
    }
    let by_size = (total / min_split.max(1)).max(1);
    let count = (connections.clamp(1, MAX_CONNECTIONS) as u64).min(by_size);
    let chunk = total / count;
    (0..count)
        .map(|i| {
            let start = i * chunk;
            let end = if i == count - 1 { total } else { start + chunk };
            ByteRange::new(start, end)
        })
        .collect()
}

/// Picks the largest of the `remaining` ranges (the bytes each active connection still has to
/// fetch) and splits off its second half for an idle connection.
///
/// Returns the index of the range to shrink and the new range. The caller sets that
/// connection's end to the new range's start. Returns `None` when no range is at least twice
/// `min_split`, so both halves stay at or above it.
pub fn split_largest(remaining: &[ByteRange], min_split: u64) -> Option<(usize, ByteRange)> {
    let (index, largest) = remaining.iter().enumerate().max_by_key(|(_, r)| r.len())?;
    if largest.len() < min_split.saturating_mul(2).max(2) {
        return None;
    }
    let middle = largest.start + largest.len() / 2;
    Some((index, ByteRange::new(middle, largest.end)))
}

#[cfg(test)]
mod tests {
    use super::*;

    const MB: u64 = 1024 * 1024;

    fn assert_covers(plan: &[ByteRange], total: u64) {
        assert_eq!(plan.first().map(|r| r.start), Some(0));
        assert_eq!(plan.last().map(|r| r.end), Some(total));
        for pair in plan.windows(2) {
            assert_eq!(pair[0].end, pair[1].start, "gap or overlap in {plan:?}");
        }
    }

    #[test]
    fn plan_uses_every_connection_on_a_large_file() {
        let total = 100 * MB + 7;
        let plan = initial_plan(total, 8, MB);
        assert_eq!(plan.len(), 8);
        assert_covers(&plan, total);
    }

    #[test]
    fn plan_uses_fewer_connections_on_a_small_file() {
        assert_eq!(initial_plan(3 * MB, 8, MB).len(), 3);
        assert_eq!(initial_plan(500, 8, MB), vec![ByteRange::new(0, 500)]);
        assert!(initial_plan(0, 8, MB).is_empty());
    }

    #[test]
    fn plan_clamps_the_connection_count() {
        assert_eq!(initial_plan(1000 * MB, 100, MB).len(), MAX_CONNECTIONS);
        assert_eq!(initial_plan(1000 * MB, 0, MB).len(), 1);
    }

    #[test]
    fn split_takes_the_second_half_of_the_largest_range() {
        let remaining =
            [ByteRange::new(0, 4 * MB), ByteRange::new(10 * MB, 20 * MB), ByteRange::new(30 * MB, 33 * MB)];
        assert_eq!(split_largest(&remaining, MB), Some((1, ByteRange::new(15 * MB, 20 * MB))));
    }

    #[test]
    fn split_stops_below_twice_the_minimum() {
        let remaining = [ByteRange::new(0, 2 * MB - 1)];
        assert_eq!(split_largest(&remaining, MB), None);
        assert_eq!(split_largest(&[], MB), None);
    }

    #[test]
    fn range_header_uses_an_inclusive_end() {
        assert_eq!(ByteRange::new(0, 1024).header_value(), "bytes=0-1023");
    }
}
