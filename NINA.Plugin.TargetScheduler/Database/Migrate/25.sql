/*
*/

ALTER TABLE profilepreference ADD COLUMN colorizeProjects INTEGER NOT NULL DEFAULT 0;
ALTER TABLE profilepreference ADD COLUMN showActiveOnly INTEGER NOT NULL DEFAULT 0;
ALTER TABLE profilepreference ADD COLUMN lastExpandedProfileId TEXT NOT NULL DEFAULT '';

PRAGMA user_version = 25;
