namespace TestAtlas.Core.Storage;

/// <summary>
/// The public SQLite schema contract (spec §9, G5). Version is stamped into
/// <c>PRAGMA user_version</c>; any breaking change increments it and the changelog states the
/// migration. Third-party indexers emitting this schema reuse downstream consumers unchanged.
/// </summary>
public static class MapSchema
{
    // v4 (slice 4): adds the endpoints table + calls_endpoint edges (HTTP calls in test code).
    public const int Version = 4;

    /// <summary>DDL for a fresh map file. Ordered; safe to run inside one transaction.</summary>
    public const string CreateSql = """
        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE projects (
            id               INTEGER PRIMARY KEY,
            name             TEXT NOT NULL,
            path             TEXT NOT NULL,
            target_framework TEXT,
            kind             TEXT NOT NULL
        );

        CREATE TABLE classes (
            id         INTEGER PRIMARY KEY,
            project_id INTEGER NOT NULL REFERENCES projects(id),
            name       TEXT NOT NULL,
            namespace  TEXT NOT NULL,
            base_type  TEXT,
            kind       TEXT NOT NULL,
            file_path  TEXT NOT NULL,
            line_start INTEGER NOT NULL,
            line_end   INTEGER NOT NULL
        );

        CREATE TABLE methods (
            id         INTEGER PRIMARY KEY,
            class_id   INTEGER NOT NULL REFERENCES classes(id),
            project_id INTEGER NOT NULL REFERENCES projects(id),
            name       TEXT NOT NULL,
            signature  TEXT NOT NULL,
            visibility TEXT NOT NULL,
            kind       TEXT NOT NULL,
            file_path  TEXT NOT NULL,
            line_start INTEGER NOT NULL,
            line_end   INTEGER NOT NULL
        );

        CREATE TABLE step_definitions (
            id              INTEGER PRIMARY KEY,
            method_id       INTEGER NOT NULL REFERENCES methods(id),
            class_id        INTEGER NOT NULL REFERENCES classes(id),
            project_id      INTEGER NOT NULL REFERENCES projects(id),
            keyword         TEXT NOT NULL,
            expression      TEXT NOT NULL,
            expression_kind TEXT NOT NULL,
            parameters      TEXT,
            file_path       TEXT NOT NULL,
            line_start      INTEGER NOT NULL
        );

        CREATE TABLE features (
            id          INTEGER PRIMARY KEY,
            project_id  INTEGER NOT NULL REFERENCES projects(id),
            name        TEXT NOT NULL,
            description TEXT,
            tags        TEXT,
            file_path   TEXT NOT NULL
        );

        CREATE TABLE scenarios (
            id                INTEGER PRIMARY KEY,
            feature_id        INTEGER NOT NULL REFERENCES features(id),
            project_id        INTEGER NOT NULL REFERENCES projects(id),
            name              TEXT NOT NULL,
            kind              TEXT NOT NULL,
            tags              TEXT,
            example_row_count INTEGER NOT NULL,
            file_path         TEXT NOT NULL,
            line_start        INTEGER NOT NULL
        );

        CREATE TABLE scenario_steps (
            id            INTEGER PRIMARY KEY,
            scenario_id   INTEGER NOT NULL REFERENCES scenarios(id),
            project_id    INTEGER NOT NULL REFERENCES projects(id),
            keyword       TEXT NOT NULL,
            text          TEXT NOT NULL,
            ordinal       INTEGER NOT NULL,
            has_docstring INTEGER NOT NULL,
            has_table     INTEGER NOT NULL,
            file_path     TEXT NOT NULL,
            line_start    INTEGER NOT NULL
        );

        CREATE TABLE endpoints (
            id    INTEGER PRIMARY KEY,
            verb  TEXT NOT NULL,
            route TEXT NOT NULL
        );

        CREATE TABLE edges (
            id         INTEGER PRIMARY KEY,
            from_kind  TEXT NOT NULL,
            from_id    INTEGER NOT NULL,
            to_kind    TEXT NOT NULL,
            to_id      INTEGER,
            edge_kind  TEXT NOT NULL,
            confidence TEXT
        );

        CREATE TABLE diagnostics (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            severity TEXT NOT NULL,
            code     TEXT NOT NULL,
            message  TEXT NOT NULL,
            location TEXT
        );

        CREATE VIRTUAL TABLE search_steps USING fts5(expression, method_name, class_name);
        CREATE VIRTUAL TABLE search_scenarios USING fts5(feature_name, scenario_name, step_text, tags);

        CREATE INDEX ix_classes_project  ON classes(project_id);
        CREATE INDEX ix_methods_class    ON methods(class_id);
        CREATE INDEX ix_methods_project  ON methods(project_id);
        CREATE INDEX ix_stepdefs_method  ON step_definitions(method_id);
        CREATE INDEX ix_stepdefs_project ON step_definitions(project_id);
        CREATE INDEX ix_scenarios_feature ON scenarios(feature_id);
        CREATE INDEX ix_steps_scenario    ON scenario_steps(scenario_id);
        CREATE INDEX ix_edges_from         ON edges(from_kind, from_id);
        CREATE INDEX ix_edges_kind         ON edges(edge_kind);
        """;

    // meta keys
    public const string MetaToolVersion = "tool_version";
    public const string MetaGeneratedUtc = "generated_utc";
    public const string MetaSolutionPath = "solution_path";
    public const string MetaInputHash = "input_hash";
}
