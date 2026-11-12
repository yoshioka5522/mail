using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Hayabumi.Core.Compose;
using Hayabumi.Core.Contacts;

namespace Hayabumi.App.Views;

public sealed partial class GroupMember(Contact contact, bool isMember) : ObservableObject
{
    public Contact Contact { get; } = contact;

    [ObservableProperty]
    private bool _isMember = isMember;
}

public partial class AddressBookWindow : Window
{
    private readonly ContactRepository _contacts;
    private Contact? _selected;
    private ContactGroup? _selectedGroup;

    public AddressBookWindow(ContactRepository contacts)
    {
        _contacts = contacts;
        InitializeComponent();
        ReloadContacts();
        ReloadGroups();
    }

    /// <summary>選んだ連絡先宛てのメールを作る（引数は宛先）。</summary>
    public event EventHandler<string>? ComposeRequested;

    private void ReloadContacts()
    {
        var filter = FilterBox.Text.Trim();
        var all = SavedOnlyBox.IsChecked == true ? _contacts.GetSaved() : _contacts.GetAll();
        ContactList.ItemsSource = string.IsNullOrEmpty(filter)
            ? all
            : all.Where(c => c.Address.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.Company.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded || ContactList is not null)
        {
            ReloadContacts();
        }
    }

    private void OnContactSelected(object sender, RoutedEventArgs e)
    {
        _selected = ContactList.SelectedItem as Contact;
        AddressBox.Text = _selected?.Address ?? string.Empty;
        NameBox.Text = _selected?.Name ?? string.Empty;
        CompanyBox.Text = _selected?.Company ?? string.Empty;
        NoteBox.Text = _selected?.Note ?? string.Empty;
    }

    private void OnNewContactClick(object sender, RoutedEventArgs e)
    {
        ContactList.SelectedItem = null;
        _selected = null;
        AddressBox.Text = NameBox.Text = CompanyBox.Text = NoteBox.Text = string.Empty;
        AddressBox.Focus();
    }

    private void OnSaveContactClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var address = MessageBuilder.ParseAddresses("メールアドレス", AddressBox.Text).SingleOrDefault()?.Address
                ?? throw new AddressParseException("メールアドレス", AddressBox.Text);
            _selected = _contacts.Save(new Contact
            {
                Id = _selected?.Id ?? 0,
                Address = address,
                Name = NameBox.Text,
                Company = CompanyBox.Text,
                Note = NoteBox.Text,
            });
            ReloadContactsKeepingSelection();
        }
        catch (Exception ex) when (ex is AddressParseException or InvalidOperationException)
        {
            MessageBox.Show(this, "メールアドレスを正しく入力してください（1 件だけ）。", "アドレス帳", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReloadContactsKeepingSelection()
    {
        var id = _selected?.Id;
        ReloadContacts();
        ContactList.SelectedItem = ContactList.Items.OfType<Contact>().FirstOrDefault(c => c.Id == id);
        ReloadGroups();
    }

    private void OnDeleteContactClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null ||
            MessageBox.Show(this, $"{_selected.Display} を削除しますか？", "アドレス帳", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _contacts.Delete(_selected.Id);
        OnNewContactClick(sender, e);
        ReloadContacts();
        ReloadGroups();
    }

    private void OnComposeClick(object sender, RoutedEventArgs e)
    {
        var selected = ContactList.SelectedItems.OfType<Contact>().ToList();
        if (selected.Count > 0)
        {
            ComposeRequested?.Invoke(this, string.Join(", ", selected.Select(c => DraftFactory.Format(new MimeKit.MailboxAddress(c.Name, c.Address)))));
        }
    }

    private void ReloadGroups()
    {
        var id = _selectedGroup?.Id;
        var groups = _contacts.GetGroups();
        GroupList.ItemsSource = groups;
        GroupList.SelectedItem = groups.FirstOrDefault(g => g.Id == id);
        if (GroupList.SelectedItem is null)
        {
            ShowGroup(null);
        }
    }

    private void OnGroupSelected(object sender, RoutedEventArgs e) => ShowGroup(GroupList.SelectedItem as ContactGroup);

    private void ShowGroup(ContactGroup? group)
    {
        _selectedGroup = group;
        GroupNameBox.Text = group?.Name ?? string.Empty;
        var members = group?.Members.Select(m => m.Id).ToHashSet() ?? [];
        MemberList.ItemsSource = _contacts.GetSaved().Select(c => new GroupMember(c, members.Contains(c.Id))).ToList();
    }

    private void OnNewGroupClick(object sender, RoutedEventArgs e)
    {
        GroupList.SelectedItem = null;
        ShowGroup(null);
        GroupNameBox.Focus();
    }

    private void OnSaveGroupClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GroupNameBox.Text))
        {
            MessageBox.Show(this, "グループ名を入力してください。", "アドレス帳", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var members = MemberList.Items.OfType<GroupMember>().Where(m => m.IsMember).Select(m => m.Contact.Id);
        try
        {
            _selectedGroup = _contacts.SaveGroup(_selectedGroup?.Id ?? 0, GroupNameBox.Text, members);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show(this, "同じ名前のグループがあります。", "アドレス帳", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ReloadGroups();
    }

    private void OnDeleteGroupClick(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup is null ||
            MessageBox.Show(this, $"グループ「{_selectedGroup.Name}」を削除しますか？（連絡先は残ります）", "アドレス帳", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _contacts.DeleteGroup(_selectedGroup.Id);
        _selectedGroup = null;
        ReloadGroups();
    }
}
