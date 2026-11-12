using Hayabumi.Core.Storage;
using MimeKit;

namespace Hayabumi.Core.Contacts;

public sealed record Contact
{
    public long Id { get; init; }
    public required string Address { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Company { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;

    /// <summary>アドレス帳に登録したもの（false は送受信の履歴から自動で集めたもの）。</summary>
    public bool IsSaved { get; init; }

    public int UseCount { get; init; }

    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

public sealed record ContactGroup(long Id, string Name, IReadOnlyList<Contact> Members);

/// <summary>入力候補の 1 件（連絡先、またはグループ）。</summary>
public sealed record ContactSuggestion(string Label, string Value, bool IsGroup);

/// <summary>
/// アドレス帳。宛先の入力候補は、登録した連絡先と送受信の履歴から作る。
/// </summary>
public sealed class ContactRepository(MailDatabase database)
{
    /// <summary>送信した宛先は、受信した差出人より強く候補に出す。</summary>
    public const int SentWeight = 3;

    public void RecordUsage(IEnumerable<MailboxAddress> addresses, bool sent)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();
        using var command = connection.CreateCommand(
            """
            INSERT INTO contacts (address, name, use_count, last_used) VALUES ($a, $n, $w, $t)
            ON CONFLICT (address) DO UPDATE SET
                use_count = use_count + $w,
                last_used = $t,
                name = CASE WHEN name = '' THEN excluded.name ELSE name END;
            """);
        var a = command.Parameters.AddWithValue("$a", string.Empty);
        var n = command.Parameters.AddWithValue("$n", string.Empty);
        command.Parameters.AddWithValue("$w", sent ? SentWeight : 1);
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixMs());
        foreach (var mailbox in addresses.Where(m => !string.IsNullOrWhiteSpace(m.Address)))
        {
            a.Value = mailbox.Address.Trim().ToLowerInvariant();
            n.Value = mailbox.Name?.Trim() ?? string.Empty;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>宛先の入力候補。アドレスの前方一致、名前・会社名の部分一致で探す。</summary>
    public IReadOnlyList<ContactSuggestion> Suggest(string text, int limit = 10)
    {
        var query = text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return [];
        }

        var like = EscapeLike(query);
        var result = new List<ContactSuggestion>();
        using var connection = database.Open();

        using (var groups = connection.CreateCommand(
            """
            SELECT g.name, GROUP_CONCAT(CASE WHEN c.name = '' THEN c.address ELSE '"' || REPLACE(c.name, '"', '') || '" <' || c.address || '>' END, ', ')
            FROM contact_groups g JOIN contact_group_members m ON m.group_id = g.id JOIN contacts c ON c.id = m.contact_id
            WHERE g.name LIKE $p ESCAPE '\'
            GROUP BY g.id ORDER BY g.name LIMIT 3;
            """,
            ("$p", "%" + like + "%")))
        using (var reader = groups.ExecuteReader())
        {
            while (reader.Read())
            {
                result.Add(new ContactSuggestion($"グループ: {reader.GetString(0)}", reader.GetString(1), IsGroup: true));
            }
        }

        using var command = connection.CreateCommand(
            """
            SELECT address, name FROM contacts
            WHERE address LIKE $prefix ESCAPE '\' OR name LIKE $contains ESCAPE '\' OR company LIKE $contains ESCAPE '\'
               OR address LIKE $domain ESCAPE '\'
            ORDER BY is_saved DESC, use_count DESC, last_used DESC
            LIMIT $limit;
            """,
            ("$prefix", like + "%"), ("$contains", "%" + like + "%"), ("$domain", "%@" + like + "%"), ("$limit", limit));
        using var contacts = command.ExecuteReader();
        while (contacts.Read())
        {
            var address = contacts.GetString(0);
            var name = contacts.GetString(1);
            var value = Compose.DraftFactory.Format(new MailboxAddress(name, address));
            result.Add(new ContactSuggestion(value, value, IsGroup: false));
        }

        return result;
    }

    public IReadOnlyList<Contact> GetSaved() => Query("WHERE is_saved = 1 ORDER BY name COLLATE NOCASE, address");

    public IReadOnlyList<Contact> GetAll() => Query("ORDER BY is_saved DESC, name COLLATE NOCASE, address");

    public Contact? FindByAddress(string address) => Query("WHERE address = $a", ("$a", address.Trim().ToLowerInvariant())).FirstOrDefault();

    /// <summary>アドレス帳に登録（更新）する。</summary>
    public Contact Save(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var address = contact.Address.Trim().ToLowerInvariant();
        using var connection = database.Open();
        var id = connection.Scalar<long>(
            """
            INSERT INTO contacts (address, name, company, note, is_saved) VALUES ($a, $n, $c, $note, 1)
            ON CONFLICT (address) DO UPDATE SET name = excluded.name, company = excluded.company, note = excluded.note, is_saved = 1
            RETURNING id;
            """,
            ("$a", address), ("$n", contact.Name.Trim()), ("$c", contact.Company.Trim()), ("$note", contact.Note));
        if (contact.Id != 0 && contact.Id != id)
        {
            // アドレスを変更した場合は古いものを消す
            connection.Execute("DELETE FROM contacts WHERE id = $id;", ("$id", contact.Id));
        }

        return contact with { Id = id, Address = address, IsSaved = true };
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM contacts WHERE id = $id;", ("$id", id));
    }

    public IReadOnlyList<ContactGroup> GetGroups()
    {
        using var connection = database.Open();
        var groups = new List<(long Id, string Name)>();
        using (var command = connection.CreateCommand("SELECT id, name FROM contact_groups ORDER BY name;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                groups.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        return [.. groups.Select(g => new ContactGroup(g.Id, g.Name,
            Query("WHERE id IN (SELECT contact_id FROM contact_group_members WHERE group_id = $g) ORDER BY name, address", ("$g", g.Id))))];
    }

    public ContactGroup SaveGroup(long id, string name, IEnumerable<long> contactIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(contactIds);
        using var connection = database.Open();
        using var transaction = connection.BeginWrite();
        if (id == 0)
        {
            id = connection.Scalar<long>("INSERT INTO contact_groups (name) VALUES ($n) RETURNING id;", ("$n", name.Trim()));
        }
        else
        {
            connection.Execute("UPDATE contact_groups SET name = $n WHERE id = $id;", ("$n", name.Trim()), ("$id", id));
            connection.Execute("DELETE FROM contact_group_members WHERE group_id = $id;", ("$id", id));
        }

        foreach (var contactId in contactIds.Distinct())
        {
            connection.Execute("INSERT INTO contact_group_members (group_id, contact_id) VALUES ($g, $c);", ("$g", id), ("$c", contactId));
        }

        transaction.Commit();
        return GetGroups().Single(g => g.Id == id);
    }

    public void DeleteGroup(long id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM contact_groups WHERE id = $id;", ("$id", id));
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private List<Contact> Query(string where, params (string, object?)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(
            $"SELECT id, address, name, company, note, is_saved, use_count FROM contacts {where};", parameters);
        using var reader = command.ExecuteReader();
        var result = new List<Contact>();
        while (reader.Read())
        {
            result.Add(new Contact
            {
                Id = reader.GetInt64(0),
                Address = reader.GetString(1),
                Name = reader.GetString(2),
                Company = reader.GetString(3),
                Note = reader.GetString(4),
                IsSaved = reader.GetInt32(5) != 0,
                UseCount = reader.GetInt32(6),
            });
        }

        return result;
    }
}
