namespace TestAtlas.Core.Storage;

/// <summary>
/// The public SQLite schema contract (spec §9, G5). Version is stamped into
/// <c>PRAGMA user_version</c>; any breaking change increments it and the changelog states the
/// migration. Third-party indexers emitting this schema reuse downstream consumers unchanged.
/// </summary>
public static class MapSchema
{
    public const int Version = 1;

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

        CREATE TABLE diagnostics (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            severity TEXT NOT NULL,
            code     TEXT NOT NULL,
            message  TEXT NOT NULL,
            location TEXT
        );

        CREATE INDEX ix_classes_project ON classes(project_id);
        CREATE INDEX ix_methods_class   ON methods(class_id);
        CREATE INDEX ix_methods_project ON methods(project_id);
        """;

    // meta keys
    public const string MetaToolVersion = "tool_version";
    public const string MetaGeneratedUtc = "generated_utc";
    public const string MetaSolutionPath = "solution_path";
    public const string MetaInputHash = "input_hash";
}
