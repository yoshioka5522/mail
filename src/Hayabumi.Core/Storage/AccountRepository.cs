using Hayabumi.Core.Accounts;
using Microsoft.Data.Sqlite;

namespace Hayabumi.Core.Storage;

public sealed class AccountRepository(MailDatabase database)
{
    private const string SelectColumns = """
        SELECT id, display_name, email_address, incoming_protocol, incoming_host, incoming_port, incoming_security,
               outgoing_host, outgoing_port, outgoing_security, user_name, auth_method, sort_order
        FROM accounts
        """;

    public Account Add(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var connection = database.Open();
        var id = connection.Scalar<long>(
            """
            INSERT INTO accounts (display_name, email_address, incoming_protocol, incoming_host, incoming_port, incoming_security,
                                  outgoing_host, outgoing_port, outgoing_security, user_name, auth_method, sort_order, created_at)
            VALUES ($dn, $ea, $ip, $ih, $iport, $is, $oh, $oport, $os, $un, $am, $so, $ca)
            RETURNING id;
            """,
            ("$dn", account.DisplayName), ("$ea", account.EmailAddress), ("$ip", (int)account.IncomingProtocol),
            ("$ih", account.Incoming.Host), ("$iport", account.Incoming.Port), ("$is", (int)account.Incoming.Security),
            ("$oh", account.Outgoing.Host), ("$oport", account.Outgoing.Port), ("$os", (int)account.Outgoing.Security),
            ("$un", account.UserName), ("$am", (int)account.AuthMethod), ("$so", account.SortOrder),
            ("$ca", DateTimeOffset.UtcNow.ToUnixMs()));
        return account with { Id = id };
    }

    public void Update(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE accounts SET display_name = $dn, email_address = $ea, incoming_protocol = $ip, incoming_host = $ih,
                incoming_port = $iport, incoming_security = $is, outgoing_host = $oh, outgoing_port = $oport,
                outgoing_security = $os, user_name = $un, auth_method = $am, sort_order = $so
            WHERE id = $id;
            """,
            ("$dn", account.DisplayName), ("$ea", account.EmailAddress), ("$ip", (int)account.IncomingProtocol),
            ("$ih", account.Incoming.Host), ("$iport", account.Incoming.Port), ("$is", (int)account.Incoming.Security),
            ("$oh", account.Outgoing.Host), ("$oport", account.Outgoing.Port), ("$os", (int)account.Outgoing.Security),
            ("$un", account.UserName), ("$am", (int)account.AuthMethod), ("$so", account.SortOrder), ("$id", account.Id));
    }

    public IReadOnlyList<Account> GetAll()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(SelectColumns + " ORDER BY sort_order, id;");
        using var reader = command.ExecuteReader();
        var result = new List<Account>();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public Account? Get(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(SelectColumns + " WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();
        // contentless の FTS は外部キーで連鎖削除されないため、先に削除する
        connection.Execute("DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM messages WHERE account_id = $id);", ("$id", id));
        connection.Execute("DELETE FROM accounts WHERE id = $id;", ("$id", id));
        transaction.Commit();
    }

    private static Account Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        DisplayName = r.GetString(1),
        EmailAddress = r.GetString(2),
        IncomingProtocol = (IncomingProtocol)r.GetInt32(3),
        Incoming = new ServerEndpoint(r.GetString(4), r.GetInt32(5), (ConnectionSecurity)r.GetInt32(6)),
        Outgoing = new ServerEndpoint(r.GetString(7), r.GetInt32(8), (ConnectionSecurity)r.GetInt32(9)),
        UserName = r.GetString(10),
        AuthMethod = (AuthMethod)r.GetInt32(11),
        SortOrder = r.GetInt32(12),
    };
}
