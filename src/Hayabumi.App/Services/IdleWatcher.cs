using Hayabumi.Core.Accounts;
using Hayabumi.Core.Sync;

namespace Hayabumi.App.Services;

/// <summary>
/// 各アカウントの受信トレイを IMAP IDLE で見張り、新着があればすぐに知らせる。
/// IDLE に対応していないサーバーでは何もしない（定期的な受信で取り込む）。
/// </summary>
public sealed class IdleWatcher(IMailStoreSessionFactory sessions) : IDisposable
{
    /// <summary>サーバーに切断されないよう、IDLE はこの間隔でかけ直す（RFC では 29 分未満）。</summary>
    private static readonly TimeSpan s_idleTimeout = TimeSpan.FromMinutes(9);

    private readonly Dictionary<long, CancellationTokenSource> _watchers = [];
    private readonly Lock _lock = new();

    /// <summary>受信トレイに変化があった（引数はアカウント ID）。UI スレッド以外から呼ばれる。</summary>
    public event EventHandler<long>? Changed;

    public void Watch(IEnumerable<Account> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var targets = accounts.Where(a => a.IncomingProtocol == IncomingProtocol.Imap).ToDictionary(a => a.Id);
        lock (_lock)
        {
            foreach (var id in _watchers.Keys.Where(id => !targets.ContainsKey(id)).ToList())
            {
                _watchers[id].Cancel();
                _watchers[id].Dispose();
                _watchers.Remove(id);
            }

            foreach (var account in targets.Values.Where(a => !_watchers.ContainsKey(a.Id)))
            {
                var cts = new CancellationTokenSource();
                _watchers[account.Id] = cts;
                _ = Task.Run(() => RunAsync(account, cts.Token));
            }
        }
    }

    private async Task RunAsync(Account account, CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(30);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var session = await sessions.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(30);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var changed = await session.WaitForChangesAsync("INBOX", s_idleTimeout, cancellationToken).ConfigureAwait(false);
                    if (changed is null)
                    {
                        Log.Info($"{account.EmailAddress} のサーバーは IDLE に対応していません");
                        return;
                    }

                    if (changed == true)
                    {
                        Changed?.Invoke(this, account.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 回線の切断やスリープからの復帰など。間をおいて接続し直す
                Log.Info($"IDLE の接続が切れました（{account.EmailAddress}）: {ex.Message}");
                try
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 600));
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var cts in _watchers.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _watchers.Clear();
        }
    }
}
