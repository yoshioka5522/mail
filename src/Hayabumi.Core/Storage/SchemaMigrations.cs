using Microsoft.Data.Sqlite;

namespace Hayabumi.Core.Storage;

/// <summary>
/// スキーマのバージョン管理。既存のマイグレーションは変更せず、新しいものを末尾に追加すること。
/// </summary>
internal static class SchemaMigrations
{
    private static readonly string[] s_migrations =
    [
        // 1: 初期スキーマ
        """
        CREATE TABLE accounts (
            id                  INTEGER PRIMARY KEY,
            display_name        TEXT    NOT NULL,
            email_address       TEXT    NOT NULL,
            incoming_protocol   INTEGER NOT NULL,
            incoming_host       TEXT    NOT NULL,
            incoming_port       INTEGER NOT NULL,
            incoming_security   INTEGER NOT NULL,
            outgoing_host       TEXT    NOT NULL,
            outgoing_port       INTEGER NOT NULL,
            outgoing_security   INTEGER NOT NULL,
            user_name           TEXT    NOT NULL,
            auth_method         INTEGER NOT NULL,
            sort_order          INTEGER NOT NULL DEFAULT 0,
            created_at          INTEGER NOT NULL
        );

        CREATE TABLE folders (
            id              INTEGER PRIMARY KEY,
            account_id      INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            full_name       TEXT    NOT NULL,
            display_name    TEXT    NOT NULL,
            delimiter       TEXT,
            role            INTEGER NOT NULL DEFAULT 0,
            uid_validity    INTEGER,
            highest_modseq  INTEGER,
            unread_count    INTEGER NOT NULL DEFAULT 0,
            total_count     INTEGER NOT NULL DEFAULT 0,
            UNIQUE (account_id, full_name)
        );

        CREATE TABLE messages (
            id              INTEGER PRIMARY KEY,
            account_id      INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            folder_id       INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            uid             INTEGER,
            message_id      TEXT,
            in_reply_to     TEXT,
            subject         TEXT    NOT NULL DEFAULT '',
            from_name       TEXT    NOT NULL DEFAULT '',
            from_address    TEXT    NOT NULL DEFAULT '',
            to_text         TEXT    NOT NULL DEFAULT '',
            cc_text         TEXT    NOT NULL DEFAULT '',
            date_utc        INTEGER NOT NULL,
            size            INTEGER NOT NULL DEFAULT 0,
            flags           INTEGER NOT NULL DEFAULT 0,
            keywords        TEXT    NOT NULL DEFAULT '',
            modseq          INTEGER,
            has_attachments INTEGER NOT NULL DEFAULT 0,
            preview         TEXT    NOT NULL DEFAULT '',
            body_state      INTEGER NOT NULL DEFAULT 0,
            file_path       TEXT,
            UNIQUE (folder_id, uid)
        );

        CREATE INDEX ix_messages_folder_date ON messages (folder_id, date_utc DESC, id DESC);
        CREATE INDEX ix_messages_folder_unread ON messages (folder_id) WHERE (flags & 1) = 0;
        CREATE INDEX ix_messages_message_id ON messages (message_id);
        CREATE INDEX ix_messages_body_state ON messages (folder_id, body_state, date_utc DESC);

        -- 全文検索。本文は .eml にあるため、インデックスのみを持つ（contentless）。
        -- 文字列は SearchTokenizer で分かち書きしてから登録する。
        CREATE VIRTUAL TABLE messages_fts USING fts5 (
            subject, sender, recipients, body,
            content = '',
            contentless_delete = 1,
            tokenize = 'unicode61 remove_diacritics 0'
        );

        -- オフライン中の操作を、接続時にサーバーへ反映するためのキュー
        CREATE TABLE pending_operations (
            id          INTEGER PRIMARY KEY,
            account_id  INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            kind        INTEGER NOT NULL,
            payload     TEXT    NOT NULL,
            created_at  INTEGER NOT NULL,
            attempts    INTEGER NOT NULL DEFAULT 0,
            last_error  TEXT
        );

        CREATE TABLE settings (
            key     TEXT PRIMARY KEY,
            value   TEXT NOT NULL
        );
        """,
    ];

    public static int LatestVersion => s_migrations.Length;

    public static void Apply(SqliteConnection connection)
    {
        var current = connection.Scalar<long>("PRAGMA user_version;");
        for (var version = (int)current; version < s_migrations.Length; version++)
        {
            using var transaction = connection.BeginWrite();
            connection.Execute(s_migrations[version]);
            connection.Execute($"PRAGMA user_version = {version + 1};");
            transaction.Commit();
        }
    }
}
