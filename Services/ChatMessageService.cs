using System;
using System.Collections.Generic;
using Emqo.NoNameTag.Utilities;

namespace Emqo.NoNameTag.Services
{
    public sealed class ChatMessageService
    {
        private const float LocalChatRangeSqr = 4096f;

        private readonly IChatMessageSettings _config;
        private readonly IFormattedNameProvider _formattedNameProvider;
        private readonly IChatMessageSender _sender;

        public ChatMessageService(IChatMessageSettings config, IFormattedNameProvider formattedNameProvider, IChatMessageSender sender)
        {
            _config = config;
            _formattedNameProvider = formattedNameProvider;
            _sender = sender;
        }

        public bool HandleChat(ChatMessageRequest request)
        {
            if (!ShouldHandle(request))
                return false;

            var formatted = BuildFormattedMessage(request.Sender, request.Message, request.ChatMode);
            if (string.IsNullOrEmpty(formatted.Message))
                return false;

            var sent = false;
            foreach (var recipient in ResolveRecipients(request))
            {
                sent |= _sender.Send(new ChatMessageDispatch
                {
                    Sender = request.Sender,
                    Recipient = recipient,
                    Message = formatted.Message,
                    AvatarUrl = formatted.AvatarUrl,
                    ChatMode = request.ChatMode
                });
            }

            return sent;
        }

        public ChatFormattedMessage BuildFormattedMessage(ChatMessageParticipant sender, string message, ChatMessageMode chatMode)
        {
            if (sender == null)
                return ChatFormattedMessage.Empty;

            var formattedName = _formattedNameProvider.GetFormattedPlayerName(sender.SteamId, sender.DisplayName);
            var safeMessage = RichTextSanitizer.SanitizeUntrustedPlayerText(message);
            var modePrefix = GetModePrefix(chatMode);

            return new ChatFormattedMessage
            {
                Message = $"{modePrefix}{formattedName}: {safeMessage}",
                AvatarUrl = null
            };
        }

        private bool ShouldHandle(ChatMessageRequest request)
        {
            return _config != null
                && _config.Enabled
                && _config.ApplyToChatMessages
                && request != null
                && !request.IsCanceled
                && request.Sender != null
                && !string.IsNullOrEmpty(request.Message)
                && !request.Message.StartsWith("/", StringComparison.Ordinal);
        }

        private IEnumerable<ChatMessageParticipant> ResolveRecipients(ChatMessageRequest request)
        {
            var recipients = request.Recipients ?? Array.Empty<ChatMessageParticipant>();
            var sender = request.Sender;

            switch (request.ChatMode)
            {
                case ChatMessageMode.Local:
                    if (sender != null)
                        yield return sender;

                    foreach (var recipient in recipients)
                    {
                        if (recipient == null)
                            continue;
                        if (HasSameSteamId(recipient, sender))
                            continue;

                        var distanceSqr = recipient.Position.DistanceSquaredTo(sender.Position);
                        if (distanceSqr <= LocalChatRangeSqr)
                            yield return recipient;
                    }
                    yield break;

                case ChatMessageMode.Group:
                    if (sender == null)
                        yield break;

                    yield return sender;

                    if (sender.GroupId == 0)
                    {
                        yield break;
                    }

                    foreach (var recipient in recipients)
                    {
                        if (recipient != null
                            && !HasSameSteamId(recipient, sender)
                            && recipient.GroupId == sender.GroupId)
                        {
                            yield return recipient;
                        }
                    }
                    yield break;

                default:
                    yield return null;
                    yield break;
            }
        }

        private static bool HasSameSteamId(ChatMessageParticipant left, ChatMessageParticipant right)
        {
            return left?.SteamId > 0
                && right?.SteamId > 0
                && left.SteamId == right.SteamId;
        }

        private static string GetModePrefix(ChatMessageMode chatMode)
        {
            if (chatMode == ChatMessageMode.Local)
                return "[A] ";

            if (chatMode == ChatMessageMode.Group)
                return "[G] ";

            return string.Empty;
        }
    }

    public sealed class ChatFormattedMessage
    {
        public static readonly ChatFormattedMessage Empty = new ChatFormattedMessage { Message = string.Empty };

        public string Message { get; set; }
        public string AvatarUrl { get; set; }
    }
}
