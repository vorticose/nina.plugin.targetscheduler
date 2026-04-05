/*
*/

ALTER TABLE project ADD COLUMN maintainexposureratio INTEGER NOT NULL DEFAULT 0;

PRAGMA user_version = 24;
