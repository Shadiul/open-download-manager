//! The SQLite state file: jobs, each segment's progress, and settings.
//!
//! Segment progress is written about once a second and on pause, so after a crash at most the
//! last second of data per segment is fetched again.

use std::path::Path;
use std::sync::Mutex;

use rusqlite::{Connection, OptionalExtension, params};

use crate::job::JobRecord;
use crate::lock;
use crate::segments::SegmentRow;

pub type Result<T> = rusqlite::Result<T>;

pub struct Store {
    conn: Mutex<Connection>,
}

const SCHEMA: &str = "
    PRAGMA journal_mode = WAL;
    PRAGMA synchronous = NORMAL;
    CREATE TABLE IF NOT EXISTS jobs (id INTEGER PRIMARY KEY, data TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS segments (
        job_id INTEGER NOT NULL, start INTEGER NOT NULL, cursor INTEGER NOT NULL, end_ INTEGER NOT NULL
    );
    CREATE INDEX IF NOT EXISTS segments_job ON segments (job_id);
    CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
";

impl Store {
    pub fn open(path: &Path) -> Result<Self> {
        Self::init(Connection::open(path)?)
    }

    pub fn open_in_memory() -> Result<Self> {
        Self::init(Connection::open_in_memory()?)
    }

    fn init(conn: Connection) -> Result<Self> {
        conn.execute_batch(SCHEMA)?;
        Ok(Self { conn: Mutex::new(conn) })
    }

    /// All jobs, oldest first. Rows that no longer parse are skipped.
    pub fn load_jobs(&self) -> Result<Vec<JobRecord>> {
        let conn = lock(&self.conn);
        let mut stmt = conn.prepare("SELECT data FROM jobs ORDER BY id")?;
        let rows = stmt.query_map([], |r| r.get::<_, String>(0))?;
        Ok(rows.filter_map(|r| r.ok()).filter_map(|s| serde_json::from_str(&s).ok()).collect())
    }

    pub fn save_job(&self, record: &JobRecord) -> Result<()> {
        let data = serde_json::to_string(record).expect("job serializes");
        lock(&self.conn).execute(
            "INSERT INTO jobs (id, data) VALUES (?1, ?2) ON CONFLICT(id) DO UPDATE SET data = excluded.data",
            params![record.job.id as i64, data],
        )?;
        Ok(())
    }

    pub fn delete_job(&self, id: u64) -> Result<()> {
        let conn = lock(&self.conn);
        conn.execute("DELETE FROM jobs WHERE id = ?1", [id as i64])?;
        conn.execute("DELETE FROM segments WHERE job_id = ?1", [id as i64])?;
        Ok(())
    }

    /// Replaces the job's saved segments in one transaction.
    pub fn save_segments(&self, id: u64, segments: &[SegmentRow]) -> Result<()> {
        let mut conn = lock(&self.conn);
        let tx = conn.transaction()?;
        tx.execute("DELETE FROM segments WHERE job_id = ?1", [id as i64])?;
        {
            let mut insert =
                tx.prepare("INSERT INTO segments (job_id, start, cursor, end_) VALUES (?1, ?2, ?3, ?4)")?;
            for s in segments {
                insert.execute(params![id as i64, s.start as i64, s.cursor as i64, to_sql(s.end)])?;
            }
        }
        tx.commit()
    }

    pub fn load_segments(&self, id: u64) -> Result<Vec<SegmentRow>> {
        let conn = lock(&self.conn);
        let mut stmt =
            conn.prepare("SELECT start, cursor, end_ FROM segments WHERE job_id = ?1 ORDER BY start")?;
        let rows = stmt.query_map([id as i64], |r| {
            Ok(SegmentRow {
                start: r.get::<_, i64>(0)? as u64,
                cursor: r.get::<_, i64>(1)? as u64,
                end: from_sql(r.get::<_, i64>(2)?),
            })
        })?;
        rows.collect()
    }

    pub fn get(&self, key: &str) -> Result<Option<String>> {
        lock(&self.conn).query_row("SELECT value FROM kv WHERE key = ?1", [key], |r| r.get(0)).optional()
    }

    pub fn set(&self, key: &str, value: &str) -> Result<()> {
        lock(&self.conn).execute(
            "INSERT INTO kv (key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            params![key, value],
        )?;
        Ok(())
    }
}

// An unknown end (u64::MAX) does not fit in SQLite's signed integers; store it as -1.
fn to_sql(v: u64) -> i64 {
    i64::try_from(v).unwrap_or(-1)
}

fn from_sql(v: i64) -> u64 {
    u64::try_from(v).unwrap_or(u64::MAX)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::job::Job;

    #[test]
    fn jobs_segments_and_settings_round_trip() {
        let store = Store::open_in_memory().unwrap();
        let mut rec = JobRecord {
            job: Job { id: 7, url: "https://e.com/a".into(), ..Default::default() },
            ..Default::default()
        };
        store.save_job(&rec).unwrap();
        rec.job.downloaded = 42;
        store.save_job(&rec).unwrap();
        assert_eq!(store.load_jobs().unwrap(), vec![rec]);

        let segs = [
            SegmentRow { start: 0, cursor: 10, end: 100 },
            SegmentRow { start: 100, cursor: 100, end: u64::MAX },
        ];
        store.save_segments(7, &segs).unwrap();
        assert_eq!(store.load_segments(7).unwrap(), segs);

        store.set("settings", "{}").unwrap();
        assert_eq!(store.get("settings").unwrap().as_deref(), Some("{}"));

        store.delete_job(7).unwrap();
        assert!(store.load_jobs().unwrap().is_empty());
        assert!(store.load_segments(7).unwrap().is_empty());
    }
}
