-- ============================================================
-- Nexus Board — PostgreSQL schema (multi-user)
-- schema: kanban   |   master tables: tm_   |   data tables: td_
-- Run once on the same DB your PostgreSqlDbConnection points to.
-- Safe to re-run (IF NOT EXISTS). A default board is auto-created
-- by the repository on first load, so no seed rows are needed here.
-- ============================================================

CREATE SCHEMA IF NOT EXISTS kanban;

-- ===================== MASTER (tm_) =====================

-- Projects
CREATE TABLE IF NOT EXISTS kanban.tm_project (
    id          VARCHAR(40)  PRIMARY KEY,
    name        VARCHAR(200) NOT NULL,
    emoji       VARCHAR(20),
    color       VARCHAR(20),
    sort_order  INTEGER      NOT NULL DEFAULT 0
);

-- Columns / lanes (per project)
CREATE TABLE IF NOT EXISTS kanban.tm_column (
    id          VARCHAR(40)  PRIMARY KEY,
    project_id  VARCHAR(40)  NOT NULL REFERENCES kanban.tm_project(id) ON DELETE CASCADE,
    name        VARCHAR(200) NOT NULL,
    color       VARCHAR(20),
    sort_order  INTEGER      NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_tm_column_project ON kanban.tm_column(project_id);

-- Categories / coloured labels (per project)
CREATE TABLE IF NOT EXISTS kanban.tm_category (
    id          VARCHAR(40)  PRIMARY KEY,
    project_id  VARCHAR(40)  NOT NULL REFERENCES kanban.tm_project(id) ON DELETE CASCADE,
    name        VARCHAR(200) NOT NULL,
    color       VARCHAR(20),
    sort_order  INTEGER      NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_tm_category_project ON kanban.tm_category(project_id);

-- ===================== DATA (td_) =====================

-- Tasks / cards. row_version powers optimistic-concurrency control.
CREATE TABLE IF NOT EXISTS kanban.td_task (
    id                 VARCHAR(40)  PRIMARY KEY,
    project_id         VARCHAR(40)  NOT NULL REFERENCES kanban.tm_project(id) ON DELETE CASCADE,
    column_id          VARCHAR(40),
    category_id        VARCHAR(40),
    title              VARCHAR(500) NOT NULL,
    description        TEXT,
    priority           VARCHAR(20),
    due                VARCHAR(20),
    sort_order         INTEGER      NOT NULL DEFAULT 0,
    row_version        INTEGER      NOT NULL DEFAULT 1,   -- bumped on every field edit
    created_by         VARCHAR(150),
    created_at         VARCHAR(40),
    updated_by         VARCHAR(150),
    updated_at         VARCHAR(40)
);
CREATE INDEX IF NOT EXISTS ix_td_task_project ON kanban.td_task(project_id);

-- Subtasks (checklist)
CREATE TABLE IF NOT EXISTS kanban.td_subtask (
    id          VARCHAR(40)  PRIMARY KEY,
    task_id     VARCHAR(40)  NOT NULL REFERENCES kanban.td_task(id) ON DELETE CASCADE,
    label       VARCHAR(500) NOT NULL,
    is_done     BOOLEAN      NOT NULL DEFAULT FALSE,
    sort_order  INTEGER      NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_td_subtask_task ON kanban.td_subtask(task_id);

-- Comments (author = display name of whoever posted)
CREATE TABLE IF NOT EXISTS kanban.td_comment (
    id          VARCHAR(40)  PRIMARY KEY,
    task_id     VARCHAR(40)  NOT NULL REFERENCES kanban.td_task(id) ON DELETE CASCADE,
    body        TEXT         NOT NULL,
    author      VARCHAR(150),
    created_at  VARCHAR(40),
    sort_order  INTEGER      NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_td_comment_task ON kanban.td_comment(task_id);

-- Links / files
CREATE TABLE IF NOT EXISTS kanban.td_link (
    id          VARCHAR(40)  PRIMARY KEY,
    task_id     VARCHAR(40)  NOT NULL REFERENCES kanban.td_task(id) ON DELETE CASCADE,
    name        VARCHAR(400),
    url         TEXT,
    path        TEXT,
    is_file     BOOLEAN      NOT NULL DEFAULT FALSE,
    sort_order  INTEGER      NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_td_link_task ON kanban.td_link(task_id);

-- Activity log (who did what). id is server-assigned & monotonic;
-- the page polls "give me activities with id > lastSeen".
CREATE TABLE IF NOT EXISTS kanban.td_activity (
    id           BIGSERIAL    PRIMARY KEY,
    project_id   VARCHAR(40),
    actor        VARCHAR(150),       -- username (from Session)
    actor_name   VARCHAR(150),       -- display name
    action       VARCHAR(30),        -- create/update/move/delete/done/comment/link/project/column/category
    entity_type  VARCHAR(30),        -- task/project/column/category/subtask/comment/link
    entity_id    VARCHAR(40),
    summary      TEXT,               -- human-readable Thai line
    created_at   VARCHAR(40)
);
CREATE INDEX IF NOT EXISTS ix_td_activity_id ON kanban.td_activity(id);
