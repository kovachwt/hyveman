-- SERVER.md §9.2: cooldown applies between *notifications* for the same dedup key, and must
-- survive a server restart (an in-memory map would re-notify once after every restart).
-- Additive-only per §6.5.
ALTER TABLE alerts ADD COLUMN last_notified_at TEXT;
