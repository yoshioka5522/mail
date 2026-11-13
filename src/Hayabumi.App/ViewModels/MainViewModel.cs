using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hayabumi.App.Services;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Compose;
using Hayabumi.Core.Mime;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;

namespace Hayabumi.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _autoSyncTimer;
    private readonly DispatcherTimer _searchDebounce;
    private VirtualMessageList? _folderList;
    private CancellationTokenSource? _detailLoad;
    private bool _suppressSelectionReload;

    [ObservableProperty]
    private FolderNodeViewModel? _selectedFolder;

    [ObservableProperty]
    private IList? _messages;

    [ObservableProperty]
    private MessageItemViewModel? _selectedMessage;

    [ObservableProperty]
    private MessageDetailViewModel? _detail;

    [ObservableProperty]
    private bool _isDetailLoading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    private bool _isSyncing;

    [ObservableProperty]
    private string _statusText = "準備完了";

    [ObservableProperty]
    private string _listHeader = string.Empty;

    [ObservableProperty]
    private bool _isUndoVisible;

    [ObservableProperty]
    private string _undoText = string.Empty;

    private long? _undoOutboxId;
    private readonly DispatcherTimer _outboxTimer;
    private readonly DispatcherTimer _undoTimer;
    private readonly DispatcherTimer _flushTimer;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(services.AppSettings.SyncIntervalMinutes) };
        _autoSyncTimer.Tick += async (_, _) => await SyncAsync();
        _outboxTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _outboxTimer.Tick += async (_, _) => await ProcessOutboxAsync();
        _undoTimer = new DispatcherTimer();
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            IsUndoVisible = false;
            _undoOutboxId = null;
        };
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _flushTimer.Tick += async (_, _) =>
        {
            _flushTimer.Stop();
            await FlushPendingAsync();
        };
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ReloadList();
        };
    }

    public ObservableCollection<AccountNodeViewModel> Accounts { get; } = [];

    /// <summary>すべてのアカウントをまとめた表示（すべての受信トレイ・未読・フラグ付き）。</summary>
    public IReadOnlyList<FolderNodeViewModel> UnifiedFolders { get; } =
        [new(UnifiedView.AllInboxes), new(UnifiedView.Unread), new(UnifiedView.Flagged)];

    private IEnumerable<FolderNodeViewModel> AllFolderNodes => UnifiedFolders.Concat(Accounts.SelectMany(a => a.Folders));

    public bool HasAccounts => Accounts.Count > 0;

    public AppServices Services => _services;

    /// <summary>一覧で選んでいるメール（複数選択に対応。画面側が設定する）。</summary>
    public Func<IReadOnlyList<MessageItemViewModel>>? SelectionProvider { get; set; }

    private List<MessageItemViewModel> SelectedItems()
    {
        var items = SelectionProvider?.Invoke().Where(i => i.Id > 0).ToList() ?? [];
        if (items.Count == 0 && SelectedMessage is { Id: > 0 } single)
        {
            items.Add(single);
        }

        return items;
    }

    /// <summary>本文を表示する前に呼ばれる（画面側で WebView2 に表示する）。</summary>
    public event EventHandler<MessageDetailViewModel?>? DetailChanged;

    /// <summary>アカウント追加の画面を開くよう画面側に依頼する。</summary>
    public event EventHandler? AddAccountRequested;

    /// <summary>アカウントの一覧が変わった（IDLE の監視対象を更新するため）。</summary>
    public event EventHandler? AccountsChanged;

    /// <summary>メール作成画面を開くよう画面側に依頼する。</summary>
    public event EventHandler<Draft>? ComposeRequested;

    /// <summary>サンプルデータで画面を確認するとき（サーバーに接続しない）。</summary>
    public bool OfflineDemo { get; init; }

    public void Start()
    {
        CleanViewerDirectory();
        LoadAccounts();
        if (OfflineDemo)
        {
            SelectedMessage = Messages is { Count: > 0 } list ? list[0] as MessageItemViewModel : null;
            return;
        }

        if (!HasAccounts)
        {
            StatusText = "アカウントを追加してください";
            AddAccountRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _ = SyncAsync();
        }

        _autoSyncTimer.Start();
        _outboxTimer.Start();
        _ = CheckUpdatesQuietlyAsync();
    }

    /// <summary>設定画面で変えた値を反映する。</summary>
    public void ApplySettings()
    {
        _autoSyncTimer.Interval = TimeSpan.FromMinutes(_services.AppSettings.SyncIntervalMinutes);
        _services.Send.UndoDelay = TimeSpan.FromSeconds(_services.AppSettings.UndoSendSeconds);
        LoadAccounts();
    }

    public void LoadAccounts()
    {
        var selectedId = SelectedFolder?.Id;
        Accounts.Clear();
        foreach (var account in _services.Accounts.GetAll())
        {
            var node = new AccountNodeViewModel(account);
            foreach (var folder in _services.Folders.GetByAccount(account.Id))
            {
                node.Folders.Add(new FolderNodeViewModel(folder));
            }

            Accounts.Add(node);
        }

        OnPropertyChanged(nameof(HasAccounts));
        UpdateUnreadCount();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        // 複数のアカウントがあるときは、最初に「すべての受信トレイ」を表示する
        SelectedFolder = AllFolderNodes.FirstOrDefault(f => f.Id == selectedId)
            ?? (Accounts.Count > 1 ? UnifiedFolders[0] : null)
            ?? Accounts.SelectMany(a => a.Folders).FirstOrDefault(f => f.Folder.Role == FolderRole.Inbox)
            ?? Accounts.SelectMany(a => a.Folders).FirstOrDefault();
    }

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        if (_suppressSelectionReload)
        {
            return;
        }

        SearchText = string.Empty;
        ReloadList();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnSelectedMessageChanged(MessageItemViewModel? value)
    {
        if (!_suppressSelectionReload)
        {
            _ = LoadDetailAsync(value);
        }
    }

    /// <summary>表示し直さずに選択だけを変える（一覧の再読み込み後に選択を戻すときなど）。</summary>
    private void SetSelectionQuietly(Action action)
    {
        _suppressSelectionReload = true;
        try
        {
            action();
        }
        finally
        {
            _suppressSelectionReload = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private Task SyncAsync() => SyncAccountsAsync(Accounts.ToList(), inboxOnly: false);

    /// <summary>IDLE で新着を検知したときなどに、1 つのアカウントの受信トレイだけを同期する。</summary>
    public async Task SyncInboxAsync(long accountId)
    {
        var node = Accounts.FirstOrDefault(a => a.Account.Id == accountId);
        if (node is null)
        {
            return;
        }

        if (IsSyncing)
        {
            // 同期中なら、終わったあとにもう一度受信トレイを確認する
            _inboxResyncRequested.Add(accountId);
            return;
        }

        await SyncAccountsAsync([node], inboxOnly: true);
    }

    private readonly HashSet<long> _inboxResyncRequested = [];

    private async Task SyncAccountsAsync(List<AccountNodeViewModel> nodes, bool inboxOnly)
    {
        if (nodes.Count == 0 || !await _services.SyncLock.WaitAsync(0))
        {
            return;
        }

        IsSyncing = true;
        var errors = new List<string>();
        var newMail = new List<(AccountNodeViewModel Node, NewMessage Message)>();
        var added = 0;
        try
        {
            foreach (var node in nodes)
            {
                if (!inboxOnly)
                {
                    StatusText = $"{node.EmailAddress} を同期しています…";
                }

                var lastRefresh = DateTime.MinValue;
                var progress = new Progress<SyncProgress>(p =>
                {
                    if (p.Total > 0)
                    {
                        StatusText = $"{node.EmailAddress}: {p.FolderName} {p.Done:N0}/{p.Total:N0}";
                    }

                    // 初回同期は時間がかかるため、途中経過も 2 秒おきに一覧へ反映する
                    if (p.FolderId == 0 || DateTime.UtcNow - lastRefresh > TimeSpan.FromSeconds(2))
                    {
                        lastRefresh = DateTime.UtcNow;
                        RefreshFolders(node);
                        if (p.FolderId != 0 && (p.FolderId == SelectedFolder?.Id || SelectedFolder?.Unified is not null) && string.IsNullOrEmpty(SearchText))
                        {
                            RefreshListKeepingSelection();
                        }
                    }
                });
                try
                {
                    Func<Folder, bool>? filter = inboxOnly ? f => f.Role == FolderRole.Inbox : null;
                    var result = await Task.Run(() => _services.SyncAccountAsync(node.Account, progress, filter, CancellationToken.None));
                    added += result.Added;
                    errors.AddRange(result.Errors.Select(e => $"{node.EmailAddress}: {e}"));
                    newMail.AddRange(result.Folders
                        .Where(f => f.Role == FolderRole.Inbox && !f.IsInitial)
                        .SelectMany(f => f.NewUnread)
                        .Select(m => (node, m)));
                }
                catch (Exception ex)
                {
                    Log.Error($"同期に失敗しました: {node.EmailAddress}", ex);
                    errors.Add($"{node.EmailAddress}: {ex.Message}");
                }

                RefreshFolders(node);
            }
        }
        finally
        {
            IsSyncing = false;
            _services.SyncLock.Release();
        }

        if (string.IsNullOrEmpty(SearchText))
        {
            RefreshListKeepingSelection();
        }

        UpdateUnreadCount();
        if (newMail.Count > 0)
        {
            var latest = newMail.OrderByDescending(m => m.Message.Date).First().Message;
            var sender = string.IsNullOrWhiteSpace(latest.FromName) ? latest.FromAddress : latest.FromName;
            var title = newMail.Count == 1 ? $"新着メール: {sender}" : $"新着メール {newMail.Count} 通";
            NewMailArrived?.Invoke(this, (title, newMail.Count == 1 ? latest.Subject : $"{sender}: {latest.Subject}"));
        }

        var time = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (errors.Count > 0)
        {
            StatusText = $"{time} 同期でエラーが発生しました: {errors[0]}";
        }
        else if (added > 0)
        {
            StatusText = $"{time} {added:N0} 通の新着メールを受信しました";
        }
        else if (!inboxOnly)
        {
            StatusText = $"{time} 同期しました";
        }

        if (_inboxResyncRequested.Count > 0)
        {
            var pending = _inboxResyncRequested.ToList();
            _inboxResyncRequested.Clear();
            foreach (var id in pending)
            {
                await SyncInboxAsync(id);
            }
        }
    }

    /// <summary>新着メールがあった（タイトル, 本文）。通知の表示に使う。</summary>
    public event EventHandler<(string Title, string Text)>? NewMailArrived;

    /// <summary>全アカウントの受信トレイの未読数（タスクバーとトレイに表示する）。</summary>
    [ObservableProperty]
    private int _unreadInboxCount;

    private void UpdateUnreadCount()
    {
        UnreadInboxCount = Accounts.SelectMany(a => a.Folders).Where(f => f.Folder.Role == FolderRole.Inbox).Sum(f => f.UnreadCount);
        UnifiedFolders[0].UnreadCount = UnreadInboxCount;
        UnifiedFolders[1].UnreadCount = _services.Messages.CountUnified(UnifiedView.Unread);
    }

    private bool CanSync() => !IsSyncing;

    [RelayCommand]
    private void AddAccount() => AddAccountRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NewMessage()
    {
        var account = CurrentAccount();
        if (account is null)
        {
            AddAccountRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ComposeRequested?.Invoke(this, DraftFactory.CreateNew(account));
    }

    [RelayCommand]
    private void Reply() => CreateResponse(ComposeMode.Reply);

    [RelayCommand]
    private void ReplyAll() => CreateResponse(ComposeMode.ReplyAll);

    [RelayCommand]
    private void Forward() => CreateResponse(ComposeMode.Forward);

    [RelayCommand]
    private void CancelSend()
    {
        if (_undoOutboxId is not { } id)
        {
            return;
        }

        _undoTimer.Stop();
        IsUndoVisible = false;
        _undoOutboxId = null;
        var draft = _services.Send.Cancel(id);
        if (draft is null)
        {
            StatusText = "既に送信されたため、取り消せませんでした";
            return;
        }

        StatusText = "送信を取り消しました";
        ComposeRequested?.Invoke(this, draft);
    }

    /// <summary>作成画面で送信したとき（送信待ちに入ったとき）に呼ばれる。</summary>
    public void OnQueued(OutboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_services.Send.UndoDelay <= TimeSpan.Zero)
        {
            StatusText = "送信しています…";
            return;
        }

        _undoOutboxId = item.Id;
        UndoText = $"送信します（{_services.Send.UndoDelay.TotalSeconds:0} 秒以内なら取り消せます）";
        IsUndoVisible = true;
        _undoTimer.Interval = _services.Send.UndoDelay;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private Account? CurrentAccount()
    {
        var accountId = SelectedFolder?.Unified is null ? SelectedFolder?.Folder.AccountId : SelectedMessage?.Item.AccountId;
        return Accounts.FirstOrDefault(a => a.Account.Id == accountId)?.Account ?? Accounts.FirstOrDefault()?.Account;
    }

    private void CreateResponse(ComposeMode mode)
    {
        if (Detail is null || SelectedMessage is not { Id: > 0 } item)
        {
            return;
        }

        var account = Accounts.FirstOrDefault(a => a.Account.Id == item.Item.AccountId)?.Account;
        if (account is null)
        {
            return;
        }

        try
        {
            using var stream = _services.Files.OpenRead(Detail.FilePath);
            var original = MessageParser.Load(stream);
            Draft draft;
            if (mode == ComposeMode.Forward)
            {
                draft = _services.Drafts.Save(DraftFactory.CreateForward(account, original, item.Id, []));
                foreach (var (entity, info) in MessageParser.FindAttachments(original).Where(a => !a.Info.IsInline))
                {
                    draft = _services.Drafts.AddAttachment(draft, info.FileName, output =>
                    {
                        switch (entity)
                        {
                            case MimeKit.MimePart part when part.Content is not null:
                                part.Content.DecodeTo(output);
                                break;
                            case MimeKit.MessagePart rfc822 when rfc822.Message is not null:
                                rfc822.Message.WriteTo(output);
                                break;
                        }
                    });
                }
            }
            else
            {
                draft = DraftFactory.CreateReply(account, original, item.Id, mode == ComposeMode.ReplyAll);
            }

            ComposeRequested?.Invoke(this, draft);
        }
        catch (Exception ex)
        {
            Log.Error("返信・転送の作成に失敗しました", ex);
            StatusText = $"返信・転送を作成できませんでした: {ex.Message}";
        }
    }

    private async Task ProcessOutboxAsync()
    {
        var next = _services.Outbox.GetNextSendAt();
        if (next is null || next > DateTimeOffset.UtcNow)
        {
            return;
        }

        _outboxTimer.Stop();
        try
        {
            var results = await Task.Run(() => _services.Send.ProcessDueAsync());
            foreach (var result in results)
            {
                if (_undoOutboxId == result.OutboxId)
                {
                    IsUndoVisible = false;
                    _undoOutboxId = null;
                }

                if (result.Sent)
                {
                    StatusText = result.Error ?? "送信しました";
                }
                else
                {
                    var failed = _services.Outbox.Get(result.OutboxId)?.Failed == true;
                    StatusText = failed
                        ? $"送信できませんでした（［メッセージ］→［送信待ちのメール］から再送できます）: {result.Error}"
                        : $"送信できませんでした。しばらくしてから自動で再送します: {result.Error}";
                    Log.Error($"送信に失敗しました: {result.Error}", null);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("送信処理でエラーが発生しました", ex);
        }
        finally
        {
            _outboxTimer.Start();
        }
    }

    [RelayCommand]
    private void ToggleRead()
    {
        var items = SelectedItems();
        if (items.Count == 0)
        {
            return;
        }

        // 1 通でも未読があれば既読に、すべて既読なら未読にする
        var read = items.Any(i => i.IsUnread);
        _services.Actions.MarkRead([.. items.Select(i => i.Item)], read);
        foreach (var item in items)
        {
            item.IsUnread = !read;
        }

        RefreshFolderCounts();
    }

    [RelayCommand]
    private void ToggleFlag()
    {
        var items = SelectedItems();
        if (items.Count == 0)
        {
            return;
        }

        var flag = items.Any(i => !i.IsFlagged);
        _services.Actions.SetFlags([.. items.Select(i => i.Item)], MessageFlags.Flagged, flag);
        foreach (var item in items)
        {
            item.IsFlagged = flag;
        }

        RefreshFolderCounts();
    }

    [RelayCommand]
    private void Delete()
    {
        var items = SelectedItems();
        if (items.Count == 0)
        {
            return;
        }

        var index = Messages?.IndexOf(items[0]) ?? -1;
        try
        {
            _services.Actions.Delete([.. items.Select(i => i.Item)]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            StatusText = ex.Message;
            return;
        }

        AfterRemoval(index);
    }

    /// <summary>選んでいるメールを別のフォルダへ移動する。</summary>
    public void MoveSelected(Folder destination)
    {
        var items = SelectedItems();
        if (items.Count == 0)
        {
            return;
        }

        var index = Messages?.IndexOf(items[0]) ?? -1;
        try
        {
            _services.Actions.Move([.. items.Select(i => i.Item)], destination);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            StatusText = ex.Message;
            return;
        }

        StatusText = $"{items.Count} 通を「{destination.DisplayName}」へ移動しました";
        AfterRemoval(index);
    }

    /// <summary>移動先の候補（選んでいるメールと同じアカウントの、ほかのフォルダ）。</summary>
    public IReadOnlyList<FolderNodeViewModel> MoveTargets()
    {
        var current = SelectedItems().FirstOrDefault()?.Item;
        if (current is null)
        {
            return [];
        }

        return [.. Accounts.Where(a => a.Account.Id == current.AccountId).SelectMany(a => a.Folders).Where(f => f.Id != current.FolderId)];
    }

    private void AfterRemoval(int index)
    {
        RefreshFolderCounts();
        ReloadList();
        if (Messages is { Count: > 0 } list && index >= 0)
        {
            SelectedMessage = list[Math.Min(index, list.Count - 1)] as MessageItemViewModel;
        }
    }

    public async Task<string?> CreateFolderAsync(AccountNodeViewModel node, FolderNodeViewModel? parent, string name)
        => await RunFolderOperationAsync(node, () => _services.FolderOperations.CreateAsync(node.Account, parent?.Folder, name), $"フォルダ「{name}」を作りました");

    public async Task<string?> RenameFolderAsync(FolderNodeViewModel folder, string newName)
    {
        var node = Accounts.First(a => a.Account.Id == folder.Folder.AccountId);
        return await RunFolderOperationAsync(node, () => _services.FolderOperations.RenameAsync(node.Account, folder.Folder, newName), $"フォルダの名前を「{newName}」に変えました");
    }

    public async Task<string?> DeleteFolderAsync(FolderNodeViewModel folder)
    {
        var node = Accounts.First(a => a.Account.Id == folder.Folder.AccountId);
        return await RunFolderOperationAsync(node, async () =>
        {
            await _services.FolderOperations.DeleteAsync(node.Account, folder.Folder);
            return folder.Folder;
        }, $"フォルダ「{folder.Name}」を削除しました");
    }

    /// <returns>エラーがあればその説明。</returns>
    private async Task<string?> RunFolderOperationAsync(AccountNodeViewModel node, Func<Task<Folder>> operation, string done)
    {
        if (!await _services.SyncLock.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            return "同期中のため、しばらくしてからもう一度お試しください。";
        }

        try
        {
            await Task.Run(operation);
            StatusText = done;
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("フォルダの操作に失敗しました", ex);
            return ex.Message;
        }
        finally
        {
            _services.SyncLock.Release();
            RefreshFolders(node);
        }
    }

    [RelayCommand]
    private void ShowRemoteContent()
    {
        if (SelectedMessage is { } item)
        {
            _ = LoadDetailAsync(item, allowRemoteContent: true);
        }
    }

    public async Task<string> CheckUpdatesAsync()
    {
        try
        {
            var result = await _services.Updates.CheckAndDownloadAsync();
            return result switch
            {
                UpdateCheckResult.NotInstalled => "インストール版ではないため、更新を確認できません。",
                UpdateCheckResult.TokenRequired => "更新を確認するには、［ツール］→［更新用トークンの設定］でトークンを設定してください。",
                UpdateCheckResult.UpToDate => $"最新の版です（{_services.Updates.CurrentVersion}）。",
                UpdateCheckResult.Downloaded => $"新しい版（{_services.Updates.PendingVersion}）をダウンロードしました。",
                _ => string.Empty,
            };
        }
        catch (Exception ex)
        {
            Log.Error("更新の確認に失敗しました", ex);
            return $"更新を確認できませんでした: {ex.Message}";
        }
    }

    private async Task CheckUpdatesQuietlyAsync()
    {
        try
        {
            if (await _services.Updates.CheckAndDownloadAsync() == UpdateCheckResult.Downloaded)
            {
                StatusText = $"新しい版（{_services.Updates.PendingVersion}）を準備しました。［ヘルプ］→［更新を確認］から再起動すると適用されます";
            }
        }
        catch (Exception ex)
        {
            Log.Error("更新の自動確認に失敗しました", ex);
        }
    }

    public async Task OnAccountAddedAsync()
    {
        LoadAccounts();
        await SyncAsync();
        LoadAccounts();
    }

    private void ReloadList()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            long? accountId = SelectedFolder?.Unified is null ? SelectedFolder?.Folder.AccountId : null;
            var results = _services.Messages.Search(SearchText, accountId, limit: 1000);
            Messages = results.Select(r => new MessageItemViewModel(r)).ToList();
            ListHeader = $"「{SearchText.Trim()}」の検索結果 {results.Count:N0} 件" + (results.Count >= 1000 ? "（新しい 1,000 件）" : string.Empty);
            return;
        }

        if (SelectedFolder is null)
        {
            _folderList = null;
            Messages = null;
            ListHeader = string.Empty;
            return;
        }

        if (SelectedFolder.Unified is { } view)
        {
            var labels = Accounts.ToDictionary(a => a.Account.Id, a => a.EmailAddress);
            var unifiedCount = _services.Messages.CountUnified(view);
            _folderList = new VirtualMessageList(unifiedCount, (offset, limit) =>
                [.. _services.Messages.GetUnifiedPage(view, offset, limit).Select(m => new MessageItemViewModel(m, labels.GetValueOrDefault(m.AccountId)))]);
            Messages = _folderList;
            ListHeader = $"{SelectedFolder.Name}  {unifiedCount:N0} 通";
            return;
        }

        var folderId = SelectedFolder.Id;
        var count = _services.Messages.Count(folderId);
        _folderList = new VirtualMessageList(count, (offset, limit) =>
            [.. _services.Messages.GetPage(folderId, offset, limit).Select(m => new MessageItemViewModel(m))]);
        Messages = _folderList;
        ListHeader = $"{SelectedFolder.Name}  {count:N0} 通";
    }

    private void RefreshListKeepingSelection()
    {
        if (SelectedFolder is null || _folderList is null || !ReferenceEquals(Messages, _folderList))
        {
            ReloadList();
            return;
        }

        var selectedId = SelectedMessage?.Id;
        var count = SelectedFolder.Unified is { } view ? _services.Messages.CountUnified(view) : _services.Messages.Count(SelectedFolder.Id);
        _folderList.Reset(count);
        ListHeader = $"{SelectedFolder.Name}  {count:N0} 通";
        if (selectedId is not null && count > 0)
        {
            // 新着は先頭に入るため、選択していたメールは最初の数ページにあることが多い
            for (var i = 0; i < Math.Min(count, 1000); i++)
            {
                if (_folderList[i] is MessageItemViewModel m && m.Id == selectedId)
                {
                    SetSelectionQuietly(() => SelectedMessage = m);
                    break;
                }
            }
        }
    }

    private void RefreshFolders(AccountNodeViewModel node)
    {
        var folders = _services.Folders.GetByAccount(node.Account.Id);
        var existing = node.Folders.ToDictionary(f => f.Id);
        if (folders.Count != node.Folders.Count || folders.Any(f => !existing.ContainsKey(f.Id)))
        {
            var selectedId = SelectedFolder?.Id;
            node.Folders.Clear();
            foreach (var folder in folders)
            {
                node.Folders.Add(new FolderNodeViewModel(folder));
            }

            var same = AllFolderNodes.FirstOrDefault(f => f.Id == selectedId);
            if (same is not null)
            {
                // 同じフォルダの新しい項目に差し替えるだけなので、一覧は読み直さない
                SetSelectionQuietly(() => SelectedFolder = same);
            }
            else if (SelectedFolder is null || (SelectedFolder.Unified is null && SelectedFolder.Folder.AccountId == node.Account.Id))
            {
                SelectedFolder = node.Folders.FirstOrDefault(f => f.Folder.Role == FolderRole.Inbox) ?? node.Folders.FirstOrDefault();
            }

            OnPropertyChanged(nameof(SelectedFolder));
            return;
        }

        foreach (var folder in folders)
        {
            existing[folder.Id].Update(folder);
        }
    }

    /// <summary>既読・移動などの操作を、少し待ってからまとめてサーバーへ反映する。</summary>
    private void SchedulePendingFlush()
    {
        _flushTimer.Stop();
        _flushTimer.Start();
    }

    private async Task FlushPendingAsync()
    {
        if (!await _services.SyncLock.WaitAsync(0))
        {
            // 同期中なら、同期の最初に反映される
            return;
        }

        try
        {
            foreach (var node in Accounts.ToList())
            {
                try
                {
                    var errors = await Task.Run(() => _services.CreateAccountSynchronizer().FlushPendingAsync(node.Account));
                    if (errors.Count > 0)
                    {
                        StatusText = $"{node.EmailAddress}: {errors[0]}";
                    }
                }
                catch (Exception ex)
                {
                    // オフラインなど。次回の同期で反映する
                    Log.Info($"操作をサーバーに反映できませんでした（{node.EmailAddress}）: {ex.Message}");
                }
            }
        }
        finally
        {
            _services.SyncLock.Release();
        }
    }

    private void RefreshFolderCounts()
    {
        SchedulePendingFlush();

        // 同じメールがほかのフォルダ（統合ビューを含む）にもあるため、すべてのアカウントの件数を数え直す
        foreach (var node in Accounts)
        {
            RefreshFolders(node);
        }

        UpdateUnreadCount();
    }

    private async Task LoadDetailAsync(MessageItemViewModel? item, bool allowRemoteContent = false)
    {
        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
        _detailLoad = new CancellationTokenSource();
        var cancellationToken = _detailLoad.Token;

        if (item is null || item.Id == 0)
        {
            Detail = null;
            DetailChanged?.Invoke(this, null);
            return;
        }

        IsDetailLoading = true;
        try
        {
            var listItem = _services.Messages.Get(item.Id);
            if (listItem is null)
            {
                return;
            }

            if (listItem.FilePath is null || !_services.Files.Exists(listItem.FilePath))
            {
                StatusText = "本文を取得しています…";
                await DownloadBodyAsync(listItem, cancellationToken);
                listItem = _services.Messages.Get(item.Id);
                if (listItem?.FilePath is null)
                {
                    StatusText = "本文を取得できませんでした（サーバーで削除された可能性があります）";
                    return;
                }

                item.Update(listItem);
                StatusText = "準備完了";
            }

            var filePath = listItem.FilePath;
            var showRemote = allowRemoteContent || _services.AppSettings.AlwaysShowRemoteImages;
            var detail = await Task.Run(() => BuildDetail(listItem.Id, filePath, showRemote), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Detail = detail;
            DetailChanged?.Invoke(this, detail);
            if (!string.IsNullOrEmpty(detail.Parsed.FromAddress))
            {
                _services.Contacts.RecordUsage([new MimeKit.MailboxAddress(detail.Parsed.FromName, detail.Parsed.FromAddress)], sent: false);
            }

            if (item.IsUnread)
            {
                _services.Actions.MarkRead([listItem], read: true);
                item.IsUnread = false;
                RefreshFolderCounts();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("本文の表示に失敗しました", ex);
            StatusText = $"本文を表示できませんでした: {ex.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsDetailLoading = false;
            }
        }
    }

    private async Task DownloadBodyAsync(MessageListItem item, CancellationToken cancellationToken)
    {
        var folder = _services.Folders.Get(item.FolderId);
        var account = _services.Accounts.Get(item.AccountId);
        if (folder is null || account is null || item.Uid is null)
        {
            return;
        }

        await Task.Run(async () =>
        {
            await using var session = await _services.Sessions.ConnectAsync(account, cancellationToken);
            await using var remote = await session.OpenFolderAsync(folder.FullName, writable: false, cancellationToken);
            await _services.CreateFolderSynchronizer().DownloadBodyAsync(remote, account.Id, item.Id, item.Uid.Value, cancellationToken);
        }, cancellationToken);
    }

    private MessageDetailViewModel BuildDetail(long messageId, string filePath, bool allowRemoteContent)
    {
        using var stream = _services.Files.OpenRead(filePath);
        var message = MessageParser.Load(stream);
        var parsed = MessageParser.Parse(message);
        var (html, hasRemote) = MessageHtmlBuilder.Build(message, parsed, allowRemoteContent);

        Directory.CreateDirectory(_services.ViewerDirectory);
        var htmlFile = Path.Combine(_services.ViewerDirectory, $"{Guid.NewGuid():N}.html");
        File.WriteAllText(htmlFile, html, new System.Text.UTF8Encoding(true));

        var attachments = MessageParser.FindAttachments(message)
            .Select((a, i) => (a.Info, Index: i))
            .Where(a => !a.Info.IsInline)
            .Select(a => new AttachmentViewModel(a.Index, a.Info.FileName, a.Info.MimeType))
            .ToList();

        return new MessageDetailViewModel
        {
            MessageId = messageId,
            FilePath = filePath,
            Parsed = parsed,
            HtmlFilePath = htmlFile,
            HasRemoteContent = hasRemote,
            RemoteContentAllowed = allowRemoteContent,
            Attachments = attachments,
        };
    }

    /// <summary>添付ファイルを保存する。</summary>
    public void SaveAttachment(AttachmentViewModel attachment, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (Detail is null)
        {
            return;
        }

        using var stream = _services.Files.OpenRead(Detail.FilePath);
        var message = MessageParser.Load(stream);
        var entity = MessageParser.FindAttachments(message)[attachment.Index].Entity;
        using var output = File.Create(destinationPath);
        switch (entity)
        {
            case MimeKit.MimePart part when part.Content is not null:
                part.Content.DecodeTo(output);
                break;
            case MimeKit.MessagePart rfc822 when rfc822.Message is not null:
                rfc822.Message.WriteTo(output);
                break;
        }
    }

    private void CleanViewerDirectory()
    {
        try
        {
            if (Directory.Exists(_services.ViewerDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_services.ViewerDirectory, "*.html"))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
