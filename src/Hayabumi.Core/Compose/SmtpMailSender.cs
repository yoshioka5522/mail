using Hayabumi.Core.Accounts;
using Hayabumi.Core.Security;
using Hayabumi.Core.Sync.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Hayabumi.Core.Compose;

public sealed class SmtpMailSender(IAccountSecretProvider secrets) : IMailSender
{
    private static readonly FormatOptions TransmitOptions = CreateTransmitOptions();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    private static FormatOptions CreateTransmitOptions()
    {
        var options = MessageBuilder.FormatOptions.Clone();
        options.HiddenHeaders.Add(HeaderId.Bcc);
        options.HiddenHeaders.Add(HeaderId.ResentBcc);
        return options;
    }

    public async Task SendAsync(Account account, MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        await client.ConnectAsync(account.Outgoing.Host, account.Outgoing.Port, ImapSessionFactory.ToSocketOptions(account.Outgoing.Security), cancellationToken)
            .ConfigureAwait(false);

        if (client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
        {
            var secret = await secrets.GetSecretAsync(account, cancellationToken).ConfigureAwait(false);
            if (account.AuthMethod == AuthMethod.Password)
            {
                await client.AuthenticateAsync(account.UserName, secret, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.UserName, secret), cancellationToken).ConfigureAwait(false);
            }
        }

        // Bcc は相手に見えてはいけないため、送信するデータからヘッダーを取り除く（宛先としては送られる）。
        // 送信済みフォルダに保存するコピーには残す
        await client.SendAsync(TransmitOptions, message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }
}
