using Hayabumi.Core.Contacts;
using MimeKit;

namespace Hayabumi.Core.Tests;

public sealed class ContactTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly ContactRepository _contacts;

    public ContactTests() => _contacts = new ContactRepository(_db.Database);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Usage_is_collected_and_ranked()
    {
        _contacts.RecordUsage([new MailboxAddress("山田 太郎", "Yamada@Partner.jp")], sent: false);
        _contacts.RecordUsage([new MailboxAddress("", "yamashita@partner.jp")], sent: true);
        _contacts.RecordUsage([new MailboxAddress("別名", "yamada@partner.jp")], sent: false);

        var yamada = _contacts.FindByAddress("YAMADA@partner.jp")!;
        Assert.Equal("山田 太郎", yamada.Name);
        Assert.Equal(2, yamada.UseCount);
        Assert.False(yamada.IsSaved);

        // 送信先（重み 3）が受信 2 回より上に来る
        Assert.Equal(["yamashita@partner.jp", "山田 太郎 <yamada@partner.jp>"], _contacts.Suggest("yama").Select(s => s.Value));
        Assert.Single(_contacts.Suggest("太郎"));
        Assert.Equal(2, _contacts.Suggest("partner").Count);
        Assert.Empty(_contacts.Suggest("%"));
    }

    [Fact]
    public void Saved_contacts_come_first_and_groups_expand()
    {
        _contacts.RecordUsage([new MailboxAddress("", "sato@example.jp")], sent: true);
        var tanaka = _contacts.Save(new Contact { Address = "Tanaka@Example.jp", Name = "田中, 一郎", Company = "サンプル商事" });
        var sato = _contacts.FindByAddress("sato@example.jp")!;

        Assert.Equal("tanaka@example.jp", _contacts.Suggest("example")[0].Value[^18..^1]);
        Assert.Single(_contacts.Suggest("サンプル"));
        Assert.Single(_contacts.GetSaved());

        var group = _contacts.SaveGroup(0, "営業部", [tanaka.Id, sato.Id]);
        Assert.Equal(2, group.Members.Count);

        var suggestion = _contacts.Suggest("営業")[0];
        Assert.True(suggestion.IsGroup);
        var addresses = Hayabumi.Core.Compose.MessageBuilder.ParseAddresses("宛先", suggestion.Value).Select(m => m.Address).Order();
        Assert.Equal(["sato@example.jp", "tanaka@example.jp"], addresses);

        _contacts.Delete(tanaka.Id);
        Assert.Single(_contacts.GetGroups()[0].Members);
        _contacts.DeleteGroup(group.Id);
        Assert.Empty(_contacts.GetGroups());
    }
}
