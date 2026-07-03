using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Emqo.NoNameTag.Utilities;
using SDG.NetTransport;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace Emqo.NoNameTag.Services
{
    public sealed class RuntimeChatMessageSender : IChatMessageSender
    {
        private static readonly object SendChatEntryLock = new object();
        private static readonly ConcurrentDictionary<ulong, SteamPlayer> RememberedPlayers =
            new ConcurrentDictionary<ulong, SteamPlayer>();
        private static readonly ConcurrentDictionary<ulong, ITransportConnection> RememberedConnections =
            new ConcurrentDictionary<ulong, ITransportConnection>();
        private static object sendChatEntry;
        private static MethodInfo sendChatEntryInvoke;
        private static bool sendChatEntryInitialized;
        private static bool rawFallbackUnavailableLogged;

        public static void RememberPlayer(ulong steamId, SteamPlayer player)
        {
            if (steamId == 0)
                return;

            if (HasPlayerId(player) && TryGetSteamId(player) == steamId)
                RememberedPlayers[steamId] = player;
            else
                RememberedPlayers.TryRemove(steamId, out _);

            RememberConnection(steamId, player);
        }

        public static void RememberRuntimePlayer(ulong steamId, Player player)
        {
            if (steamId == 0)
                return;

            try
            {
                var owner = player?.channel?.owner;
                if (owner != null)
                    RememberPlayer(steamId, owner);
            }
            catch
            {
                // Continue with the transport-only fallback below.
            }

            try
            {
                var connection = player?.channel?.GetOwnerTransportConnection();
                if (connection != null)
                    RememberedConnections[steamId] = connection;
            }
            catch
            {
                // Ignore transient runtime player state.
            }
        }

        public static void ForgetPlayer(ulong steamId)
        {
            if (steamId == 0)
                return;

            RememberedPlayers.TryRemove(steamId, out _);
            RememberedConnections.TryRemove(steamId, out _);
        }

        public static void ClearRememberedPlayers()
        {
            RememberedPlayers.Clear();
            RememberedConnections.Clear();
        }

        public bool Send(ChatMessageDispatch dispatch)
        {
            if (dispatch == null || string.IsNullOrEmpty(dispatch.Message))
                return false;

            var runtimeMode = ToRuntimeMode(dispatch.ChatMode);
            if (TrySendRawChatEntry(dispatch, runtimeMode))
                return true;

            var sender = ResolveSteamPlayer(dispatch.Sender);
            if (!HasPlayerId(sender))
            {
                PluginLogger.Warning($"Chat replay skipped: sender not resolved for {dispatch.Sender?.SteamId ?? 0} mode={dispatch.ChatMode}", LogCategory.Plugin);
                return false;
            }

            var recipient = dispatch.Recipient == null
                ? null
                : ResolveSteamPlayer(dispatch.Recipient);

            if (dispatch.Recipient != null && !HasTransport(recipient))
            {
                LogGroupSelfEchoFailure(dispatch, "recipient not resolved or missing transport");
                return false;
            }

            var sent = TrySendViaChatManager(dispatch, sender, recipient, runtimeMode);
            if (IsGroupSelfEcho(dispatch))
            {
                if (sent)
                    PluginLogger.Info($"Group self echo sent via ChatManager to {dispatch.Recipient?.SteamId ?? 0}", LogCategory.Plugin);
                else
                    LogGroupSelfEchoFailure(dispatch, "ChatManager send failed");
            }

            return sent;
        }

        private static IEnumerable<SteamPlayer> GetCurrentRecipients()
        {
            var clients = Provider.clients;
            if (clients == null)
                yield break;

            foreach (var client in clients)
            {
                if (HasTransport(client))
                    yield return client;
            }
        }

        private static bool TrySendRawChatEntry(ChatMessageDispatch dispatch, EChatMode runtimeMode)
        {
            try
            {
                if (!EnsureRawChatEntrySender())
                    return false;

                var speakerSteamId = ResolveSpeakerSteamId(dispatch);
                if (speakerSteamId == CSteamID.Nil)
                    return false;

                if (dispatch.Recipient == null)
                {
                    var sentAny = false;
                    foreach (var recipient in GetCurrentRecipients())
                    {
                        if (TrySendRawToConnection(recipient.transportConnection, speakerSteamId, dispatch, runtimeMode))
                            sentAny = true;
                    }
                    return sentAny;
                }

                var resolvedRecipient = ResolveSteamPlayer(dispatch.Recipient);
                var connection = ResolveTransportConnection(dispatch.Recipient, resolvedRecipient);
                if (connection == null)
                {
                    LogGroupSelfEchoFailure(dispatch, "recipient transport not resolved for raw chat");
                    return false;
                }

                var sent = TrySendRawToConnection(connection, speakerSteamId, dispatch, runtimeMode);
                if (IsGroupSelfEcho(dispatch))
                {
                    if (sent)
                        PluginLogger.Info($"Group self echo sent via raw chat packet to {dispatch.Recipient?.SteamId ?? 0}", LogCategory.Plugin);
                    else
                        LogGroupSelfEchoFailure(dispatch, "raw chat packet send failed");
                }

                return sent;
            }
            catch (Exception ex)
            {
                PluginLogger.Warning($"Raw chat replay failed: {ex}", LogCategory.Plugin);
                return false;
            }
        }

        private static bool TrySendRawToConnection(
            ITransportConnection connection,
            CSteamID speakerSteamId,
            ChatMessageDispatch dispatch,
            EChatMode runtimeMode)
        {
            if (connection == null)
                return false;

            try
            {
                sendChatEntryInvoke.Invoke(sendChatEntry, new object[]
                {
                    (ENetReliability)0,
                    connection,
                    speakerSteamId,
                    dispatch.AvatarUrl ?? string.Empty,
                    runtimeMode,
                    Color.white,
                    true,
                    dispatch.Message
                });
                return true;
            }
            catch (Exception ex)
            {
                PluginLogger.Warning($"Raw chat packet send failed: {ex.Message}", LogCategory.Plugin);
                return false;
            }
        }

        private static CSteamID ResolveSpeakerSteamId(ChatMessageDispatch dispatch)
        {
            return dispatch?.Sender?.SteamId > 0
                ? new CSteamID(dispatch.Sender.SteamId)
                : CSteamID.Nil;
        }

        private static ITransportConnection ResolveTransportConnection(ChatMessageParticipant participant, SteamPlayer player)
        {
            if (HasTransport(player))
                return player.transportConnection;

            if (participant?.SteamId > 0 && RememberedConnections.TryGetValue(participant.SteamId, out var remembered))
                return remembered;

            return null;
        }

        private static void RememberConnection(ulong steamId, SteamPlayer player)
        {
            if (steamId == 0)
                return;

            try
            {
                if (player?.transportConnection != null)
                    RememberedConnections[steamId] = player.transportConnection;
            }
            catch
            {
                // Ignore transient partial SteamPlayer state.
            }
        }

        private static bool EnsureRawChatEntrySender()
        {
            if (sendChatEntryInitialized)
                return sendChatEntry != null && sendChatEntryInvoke != null;

            lock (SendChatEntryLock)
            {
                if (sendChatEntryInitialized)
                    return sendChatEntry != null && sendChatEntryInvoke != null;

                var field = typeof(ChatManager).GetField("SendChatEntry", BindingFlags.NonPublic | BindingFlags.Static);
                sendChatEntry = field?.GetValue(null);
                sendChatEntryInvoke = sendChatEntry?.GetType().GetMethod(
                    "Invoke",
                    new[]
                    {
                        typeof(ENetReliability),
                        typeof(ITransportConnection),
                        typeof(CSteamID),
                        typeof(string),
                        typeof(EChatMode),
                        typeof(Color),
                        typeof(bool),
                        typeof(string)
                    });

                sendChatEntryInitialized = true;

                if ((sendChatEntry == null || sendChatEntryInvoke == null) && !rawFallbackUnavailableLogged)
                {
                    rawFallbackUnavailableLogged = true;
                    PluginLogger.Warning("Raw chat packet sender is unavailable; chat avatar preservation will depend on ChatManager.serverSendMessage fallback.", LogCategory.Plugin);
                }

                return sendChatEntry != null && sendChatEntryInvoke != null;
            }
        }

        private static SteamPlayer ResolveSteamPlayer(ChatMessageParticipant participant)
        {
            if (participant?.SteamId == null || participant.SteamId == 0)
                return null;

            try
            {
                if (RememberedPlayers.TryGetValue(participant.SteamId, out var remembered))
                {
                    if (HasPlayerId(remembered) && TryGetSteamId(remembered) == participant.SteamId)
                        return remembered;

                    RememberedPlayers.TryRemove(participant.SteamId, out _);
                }

                var player = BroadcastHelper.GetSteamPlayer(new CSteamID(participant.SteamId));
                RememberPlayer(participant.SteamId, player);
                return player;
            }
            catch (Exception ex)
            {
                PluginLogger.Warning($"Chat player lookup failed for {participant.SteamId}: {ex.Message}", LogCategory.Plugin);
                return null;
            }
        }

        private static bool HasPlayerId(SteamPlayer player)
        {
            try
            {
                return player?.playerID != null
                    && player.playerID.steamID != CSteamID.Nil
                    && player.playerID.steamID.m_SteamID != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasTransport(SteamPlayer player)
        {
            try
            {
                return player != null && player.transportConnection != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySendViaChatManager(
            ChatMessageDispatch dispatch,
            SteamPlayer sender,
            SteamPlayer recipient,
            EChatMode runtimeMode)
        {
            try
            {
                ChatManager.serverSendMessage(
                    dispatch.Message,
                    Color.white,
                    sender,
                    recipient,
                    runtimeMode,
                    dispatch.AvatarUrl,
                    true);
                return true;
            }
            catch (Exception ex)
            {
                PluginLogger.Warning($"Chat replay through ChatManager failed: {ex}", LogCategory.Plugin);
                return false;
            }
        }

        private static ulong TryGetSteamId(SteamPlayer player)
        {
            try
            {
                return player?.playerID?.steamID.m_SteamID ?? 0UL;
            }
            catch
            {
                return 0UL;
            }
        }

        private static bool IsGroupSelfEcho(ChatMessageDispatch dispatch)
        {
            return dispatch?.ChatMode == ChatMessageMode.Group && IsSelfRecipient(dispatch);
        }

        private static bool IsSelfRecipient(ChatMessageDispatch dispatch)
        {
            return dispatch?.Sender?.SteamId > 0
                && dispatch.Recipient?.SteamId > 0
                && dispatch.Sender.SteamId == dispatch.Recipient.SteamId;
        }

        private static void LogGroupSelfEchoFailure(ChatMessageDispatch dispatch, string reason)
        {
            if (IsGroupSelfEcho(dispatch))
                PluginLogger.Warning($"Group self echo failed for {dispatch.Recipient?.SteamId ?? 0}: {reason}", LogCategory.Plugin);
        }

        private static EChatMode ToRuntimeMode(ChatMessageMode mode)
        {
            switch (mode)
            {
                case ChatMessageMode.Local:
                    return EChatMode.LOCAL;
                case ChatMessageMode.Group:
                    return EChatMode.GROUP;
                case ChatMessageMode.Say:
                    return EChatMode.SAY;
                default:
                    return EChatMode.GLOBAL;
            }
        }
    }
}
