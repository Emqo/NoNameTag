using System.Collections.Generic;
using System.Linq;
using Emqo.NoNameTag.Services;
using Xunit;

namespace Emqo.NoNameTag.Tests
{
    public sealed class ChatMessageServiceTests
    {
        [Fact]
        public void LocalChat_SendsOnlyToNearbyRecipients()
        {
            var sender = Participant(1, "Alice", 0UL, Position(0, 0, 0));
            var near = Participant(2, "Near", 0UL, Position(10, 0, 0));
            var far = Participant(3, "Far", 0UL, Position(100, 0, 0));
            var senderSpy = new SpyChatMessageSender();
            var service = CreateService(senderSpy);

            var handled = service.HandleChat(new ChatMessageRequest
            {
                Sender = sender,
                Recipients = new[] { sender, near, far },
                Message = "<color=red>hi</color>",
                ChatMode = ChatMessageMode.Local
            });

            Assert.True(handled);
            Assert.Equal(new ulong[] { 1, 2 }, senderSpy.Dispatches.Select(d => d.Recipient.SteamId).ToArray());
            Assert.All(senderSpy.Dispatches, d => Assert.Equal("[A] [VIP] Alice: color=redhi/color", d.Message));
        }

        [Fact]
        public void GroupChat_SendsOnlyToSenderGroup()
        {
            var group = 42UL;
            var sender = Participant(1, "Alice", group, Position(0, 0, 0));
            var sameGroup = Participant(2, "Bob", group, Position(0, 0, 0));
            var otherGroup = Participant(3, "Eve", 43UL, Position(0, 0, 0));
            var senderSpy = new SpyChatMessageSender();
            var service = CreateService(senderSpy);

            service.HandleChat(new ChatMessageRequest
            {
                Sender = sender,
                Recipients = new[] { sender, sameGroup, otherGroup },
                Message = "hello",
                ChatMode = ChatMessageMode.Group
            });

            Assert.Equal(new ulong[] { 1, 2 }, senderSpy.Dispatches.Select(d => d.Recipient.SteamId).ToArray());
            Assert.All(senderSpy.Dispatches, d => Assert.Equal("[G] [VIP] Alice: hello", d.Message));
        }

        [Fact]
        public void GroupChatWithoutGroup_SendsOnlyToSender()
        {
            var sender = Participant(1, "Alice", 0UL, Position(0, 0, 0));
            var other = Participant(2, "Bob", 0UL, Position(0, 0, 0));
            var senderSpy = new SpyChatMessageSender();
            var service = CreateService(senderSpy);

            service.HandleChat(new ChatMessageRequest
            {
                Sender = sender,
                Recipients = new[] { sender, other },
                Message = "hello",
                ChatMode = ChatMessageMode.Group
            });

            var dispatch = Assert.Single(senderSpy.Dispatches);
            Assert.Equal(1UL, dispatch.Recipient.SteamId);
            Assert.Equal("[G] [VIP] Alice: hello", dispatch.Message);
        }

        [Fact]
        public void GroupChat_WhenSnapshotOmitsSender_StillSendsBackToSender()
        {
            var group = 42UL;
            var sender = Participant(1, "Alice", group, Position(0, 0, 0));
            var sameGroup = Participant(2, "Bob", group, Position(0, 0, 0));
            var senderSpy = new SpyChatMessageSender();
            var service = CreateService(senderSpy);

            service.HandleChat(new ChatMessageRequest
            {
                Sender = sender,
                Recipients = new[] { sameGroup },
                Message = "hello",
                ChatMode = ChatMessageMode.Group
            });

            Assert.Equal(new ulong[] { 1, 2 }, senderSpy.Dispatches.Select(d => d.Recipient.SteamId).ToArray());
        }

        [Fact]
        public void LocalChat_WhenSnapshotOmitsSender_StillSendsBackToSender()
        {
            var sender = Participant(1, "Alice", 0UL, Position(0, 0, 0));
            var near = Participant(2, "Near", 0UL, Position(10, 0, 0));
            var senderSpy = new SpyChatMessageSender();
            var service = CreateService(senderSpy);

            service.HandleChat(new ChatMessageRequest
            {
                Sender = sender,
                Recipients = new[] { near },
                Message = "hello",
                ChatMode = ChatMessageMode.Local
            });

            Assert.Equal(new ulong[] { 1, 2 }, senderSpy.Dispatches.Select(d => d.Recipient.SteamId).ToArray());
        }

        [Fact]
        public void GlobalChat_UsesBroadcastDelivery()
        {
            var sender = Participant(1, "Alice", 0UL, Position(0, 0, 0));
            var senderSpy = new SpyChatMessageSender();
            var service = CreateService(senderSpy);

            service.HandleChat(new ChatMessageRequest
            {
                Sender = sender,
                Recipients = new[] { sender },
                Message = "hello",
                ChatMode = ChatMessageMode.Global
            });

            var dispatch = Assert.Single(senderSpy.Dispatches);
            Assert.Null(dispatch.Recipient);
            Assert.Equal(ChatMessageMode.Global, dispatch.ChatMode);
            Assert.Equal("[VIP] Alice: hello", dispatch.Message);
        }

        [Fact]
        public void HandleChat_ReturnsFalseWhenRuntimeSenderDoesNotSend()
        {
            var sender = Participant(1, "Alice", 0UL, Position(0, 0, 0));
            var senderSpy = new SpyChatMessageSender { SendResult = false };
            var service = CreateService(senderSpy);

            var handled = service.HandleChat(new ChatMessageRequest
            {
                Sender = sender,
                Message = "hello",
                ChatMode = ChatMessageMode.Global
            });

            Assert.False(handled);
            Assert.Single(senderSpy.Dispatches);
        }

        [Fact]
        public void ChatFormatting_UsesCachedFormattedNameLookup()
        {
            var nameTags = new SpyNameTagManager();
            var service = new ChatMessageService(DefaultConfig(), nameTags, new SpyChatMessageSender());

            var formatted = service.BuildFormattedMessage(Participant(7, "Alice", 0UL, Position(0, 0, 0)), "hello", ChatMessageMode.Global);

            Assert.Equal("[VIP] Alice: hello", formatted.Message);
            Assert.Equal(1, nameTags.FormattedNameLookupCount);
            Assert.Equal(7UL, nameTags.LastSteamId);
        }

        private static ChatMessageService CreateService(SpyChatMessageSender sender)
        {
            return new ChatMessageService(DefaultConfig(), new SpyNameTagManager(), sender);
        }

        private static ChatMessagePosition Position(float x, float y, float z)
        {
            return new ChatMessagePosition(x, y, z);
        }

        private static IChatMessageSettings DefaultConfig()
        {
            return new TestChatMessageSettings
            {
                Enabled = true,
                ApplyToChatMessages = true
            };
        }

        private static ChatMessageParticipant Participant(ulong steamId, string displayName, ulong groupId, ChatMessagePosition position)
        {
            return new ChatMessageParticipant
            {
                SteamId = steamId,
                DisplayName = displayName,
                GroupId = groupId,
                Position = position
            };
        }

        private sealed class TestChatMessageSettings : IChatMessageSettings
        {
            public bool Enabled { get; set; }
            public bool ApplyToChatMessages { get; set; }
        }

        private sealed class SpyChatMessageSender : IChatMessageSender
        {
            public List<ChatMessageDispatch> Dispatches { get; } = new List<ChatMessageDispatch>();
            public bool SendResult { get; set; } = true;

            public bool Send(ChatMessageDispatch dispatch)
            {
                Dispatches.Add(dispatch);
                return SendResult;
            }
        }

        private sealed class SpyNameTagManager : IFormattedNameProvider
        {
            public int FormattedNameLookupCount { get; private set; }
            public ulong LastSteamId { get; private set; }

            public string GetFormattedPlayerName(ulong steamId, string fallbackPlayerName)
            {
                FormattedNameLookupCount++;
                LastSteamId = steamId;
                return $"[VIP] {fallbackPlayerName}";
            }
        }
    }
}
