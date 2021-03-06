using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Core.Configuration;
using Microsoft.Extensions.Logging;
using NodaTime;
using Persistence.Models;
using Persistence.Repos;
using TwitchLib.Client;
using TwitchLib.Client.Enums;
using TwitchLib.Client.Events;
using TwitchLib.Client.Extensions;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;

namespace Core.Chat
{
    public sealed class TwitchChat : IChat, IChatModeChanger
    {
        public event EventHandler<MessageEventArgs> IncomingMessage = null!;
        public event EventHandler<string> IncomingUnhandledIrcLine = null!;

        /// Twitch Messaging Interface (TMI, the somewhat IRC-compatible protocol twitch uses) maximum message length.
        /// This limit is in characters, not bytes. See https://discuss.dev.twitch.tv/t/message-character-limit/7793/6
        private const int MaxMessageLength = 500;

        private static readonly MessageSplitter MessageSplitterRegular = new(
            maxMessageLength: MaxMessageLength - "/me ".Length);

        private static readonly MessageSplitter MessageSplitterWhisper = new(
            // visual representation of the longest possible username (25 characters)
            maxMessageLength: MaxMessageLength - "/w ,,,,,''''',,,,,''''',,,,, ".Length);

        private readonly ILogger<TwitchChat> _logger;
        private readonly IClock _clock;
        private readonly string _ircChannel;
        private readonly ImmutableHashSet<ChatConfig.SuppressionType> _suppressions;
        private readonly ImmutableHashSet<string> _suppressionOverrides;
        private readonly IUserRepo _userRepo;
        private readonly TwitchClient _twitchClient;

        private bool _connected = false;
        private Action? _connectivityWorkerCleanup;

        public TwitchChat(
            ILoggerFactory loggerFactory,
            IClock clock,
            ChatConfig chatConfig,
            IUserRepo userRepo)
        {
            _logger = loggerFactory.CreateLogger<TwitchChat>();
            _clock = clock;
            _ircChannel = chatConfig.Channel;
            _suppressions = chatConfig.Suppressions;
            _suppressionOverrides = chatConfig.SuppressionOverrides
                .Select(s => s.ToLowerInvariant()).ToImmutableHashSet();
            _userRepo = userRepo;

            _twitchClient = new TwitchClient(
                client: new WebSocketClient(new ClientOptions()),
                logger: loggerFactory.CreateLogger<TwitchClient>());
            var credentials = new ConnectionCredentials(
                twitchUsername: chatConfig.Username,
                twitchOAuth: chatConfig.Password,
                disableUsernameCheck: true);
            _twitchClient.Initialize(
                credentials: credentials,
                channel: chatConfig.Channel,
                // disable TwitchLib's command features, we do that ourselves
                chatCommandIdentifier: '\0',
                whisperCommandIdentifier: '\0');
        }

        public async Task SendMessage(string message)
        {
            if (_suppressions.Contains(ChatConfig.SuppressionType.Message) &&
                !_suppressionOverrides.Contains(_ircChannel))
            {
                _logger.LogDebug("(suppressed) >#{Channel}: {Message}", _ircChannel, message);
                return;
            }
            _logger.LogDebug(">#{Channel}: {Message}", _ircChannel, message);
            await Task.Run(() =>
            {
                foreach (string part in MessageSplitterRegular.FitToMaxLength(message))
                {
                    _twitchClient.SendMessage(_ircChannel, "/me " + part);
                }
            });
        }

        public async Task SendWhisper(User target, string message)
        {
            if (_suppressions.Contains(ChatConfig.SuppressionType.Whisper) &&
                !_suppressionOverrides.Contains(target.SimpleName))
            {
                _logger.LogDebug("(suppressed) >@{Username}: {Message}", target.SimpleName, message);
                return;
            }
            _logger.LogDebug(">@{Username}: {Message}", target.SimpleName, message);
            await Task.Run(() =>
            {
                foreach (string part in MessageSplitterWhisper.FitToMaxLength(message))
                {
                    _twitchClient.SendWhisper(target.SimpleName, part);
                }
            });
        }

        public void Connect()
        {
            if (_connected)
            {
                throw new InvalidOperationException("Can only ever connect once per chat instance.");
            }
            _connected = true;
            _twitchClient.OnMessageReceived += MessageReceived;
            _twitchClient.OnWhisperReceived += WhisperReceived;
            _twitchClient.OnSendReceiveData += AnythingElseReceived;
            _twitchClient.Connect();
            var tokenSource = new CancellationTokenSource();
            Task checkConnectivityWorker = CheckConnectivityWorker(tokenSource.Token);
            _connectivityWorkerCleanup = () =>
            {
                tokenSource.Cancel();
                if (!checkConnectivityWorker.IsCanceled) checkConnectivityWorker.Wait();
            };
        }

        /// TwitchClient's disconnect event appears to fire unreliably,
        /// so it is safer to manually check the connection every few seconds.
        private async Task CheckConnectivityWorker(CancellationToken cancellationToken)
        {
            TimeSpan minDelay = TimeSpan.FromSeconds(3);
            TimeSpan maxDelay = TimeSpan.FromSeconds(30);
            TimeSpan delay = minDelay;
            while (!cancellationToken.IsCancellationRequested)
            {
                delay *= _twitchClient.IsConnected ? 0.5 : 2;
                if (delay > maxDelay) delay = maxDelay;
                if (delay < minDelay) delay = minDelay;

                if (!_twitchClient.IsConnected)
                {
                    _logger.LogError("Not connected to twitch, trying to reconnect...");
                    try
                    {
                        _twitchClient.Reconnect();
                    }
                    catch (Exception)
                    {
                        _logger.LogError("Failed to reconnect, trying again in {Delay} seconds", delay.TotalSeconds);
                    }
                }

                await Task.Delay(delay, cancellationToken);
            }
        }

        private async void MessageReceived(object? sender, OnMessageReceivedArgs e)
        {
            _logger.LogDebug("<#{Channel} {Username}: {Message}",
                _ircChannel, e.ChatMessage.Username, e.ChatMessage.Message);
            await AnyMessageReceived(e.ChatMessage, e.ChatMessage.Message, MessageSource.Chat);
        }

        private async void WhisperReceived(object? sender, OnWhisperReceivedArgs e)
        {
            _logger.LogDebug("<@{Username}: {Message}", e.WhisperMessage.Username, e.WhisperMessage.Message);
            await AnyMessageReceived(e.WhisperMessage, e.WhisperMessage.Message, MessageSource.Whisper);
        }

        private void AnythingElseReceived(object? sender, OnSendReceiveDataArgs e)
        {
            // This gives us _everything_, but we already explicitly handle messages and whispers.
            // Therefore do a quick&dirty parse over the message to filter those out.
            // Simplified example: "@tags :user@twitch.tv PRIVMSG #twitchplayspokemon :test"
            if (e.Direction != SendReceiveDirection.Received) return;
            string ircLine = e.Data;
            if (ircLine.StartsWith("PING"))
            {
                IncomingUnhandledIrcLine?.Invoke(this, ircLine);
                return;
            }
            if (ircLine.StartsWith("PONG")) return;
            string[] splitTagsMetaMessage = Regex.Split(ircLine, @"(?:^| ):");
            if (splitTagsMetaMessage.Length < 2)
            {
                _logger.LogWarning("received unparsable irc line (colon delimiter): {IrcLine}", ircLine);
                return;
            }
            string[] splitHostCommandChannel = splitTagsMetaMessage[1].Split(' ', count: 3);
            if (splitHostCommandChannel.Length < 3)
            {
                _logger.LogWarning("received unparsable irc line (space delimiter): {IrcLine}", ircLine);
                return;
            }
            string command = splitHostCommandChannel[1];
            if (command == "PRIVMSG" || command == "WHISPER")
            {
                return;
            }
            IncomingUnhandledIrcLine?.Invoke(this, ircLine);
        }

        private async Task AnyMessageReceived(
            TwitchLibMessage twitchLibMessage,
            string messageText,
            MessageSource source)
        {
            string? colorHex = twitchLibMessage.ColorHex;
            User user = await _userRepo.RecordUser(new UserInfo(
                id: twitchLibMessage.UserId,
                twitchDisplayName: twitchLibMessage.DisplayName,
                simpleName: twitchLibMessage.Username,
                color: string.IsNullOrEmpty(colorHex) ? null : colorHex.TrimStart('#'),
                fromMessage: true,
                updatedAt: _clock.GetCurrentInstant()
            ));
            Message message = new(user, messageText, source, twitchLibMessage.RawIrcMessage);
            IncomingMessage?.Invoke(this, new MessageEventArgs(message));
        }

        public void Dispose()
        {
            if (_connected)
            {
                _connectivityWorkerCleanup?.Invoke();
                _twitchClient.Disconnect();
            }
            _twitchClient.OnMessageReceived -= MessageReceived;
            _twitchClient.OnWhisperReceived -= WhisperReceived;
            _logger.LogDebug("twitch chat is now fully shut down");
        }

        public async Task EnableEmoteOnly()
        {
            if (_suppressions.Contains(ChatConfig.SuppressionType.Command) &&
                !_suppressionOverrides.Contains(_ircChannel))
            {
                _logger.LogDebug($"(suppressed) enabling emote only mode in #{_ircChannel}");
                return;
            }

            _logger.LogDebug($"enabling emote only mode in #{_ircChannel}");
            await Task.Run(() => _twitchClient.EmoteOnlyOn(_ircChannel));
        }

        public async Task DisableEmoteOnly()
        {
            if (_suppressions.Contains(ChatConfig.SuppressionType.Command) &&
                !_suppressionOverrides.Contains(_ircChannel))
            {
                _logger.LogDebug($"(suppressed) disabling emote only mode in #{_ircChannel}");
                return;
            }

            _logger.LogDebug($"disabling emote only mode in #{_ircChannel}");
            await Task.Run(() => _twitchClient.EmoteOnlyOff(_ircChannel));
        }
    }
}
