using Emqo.NoNameTag.Services;
using Emqo.NoNameTag.Utilities;
using Rocket.Core.Plugins;
using Rocket.Unturned;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using System;
using UnityEngine;
using Logger = Emqo.NoNameTag.Utilities.PluginLogger;

namespace Emqo.NoNameTag
{
    public class NoNameTagPlugin : RocketPlugin<NoNameTagConfiguration>
    {
        public static NoNameTagPlugin Instance { get; private set; }

        public IPermissionService PermissionService { get; private set; }
        public INameTagManager NameTagManager { get; private set; }
        public IBroadcastService BroadcastService { get; private set; }
        public IPlayerStatsService PlayerStatsService { get; private set; }
        public IDamageAttributionService DamageAttributionService { get; private set; }
        public IDeathAttributionResolver DeathAttributionResolver { get; private set; }
        private ChatMessageService ChatMessageService { get; set; }

        protected override void Load()
        {
            Instance = this;
            Logger.DebugEnabled = Configuration.Instance.DebugMode;

            try
            {
                if (!ConfigValidator.ValidateConfiguration(Configuration.Instance, out var configError))
                {
                    Logger.Warning($"Configuration validation failed: {configError}");
                }

                InitializeServices();
                RegisterEventHandlers();
                RefreshAllDisplays();
                BroadcastService?.StartAllBroadcasts();

                Logger.Info($"{Name} {Assembly.GetName().Version.ToString(3)} has been loaded!");
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "Failed to load plugin");
            }
        }

        protected override void Unload()
        {
            try
            {
                UnregisterEventHandlers();
                BroadcastService?.Dispose();
                DamageAttributionService?.ClearAll();
                PlayerStatsService?.Dispose();
                NameTagManager?.ClearAll();
                PermissionService?.ClearAllCache();
                RuntimeChatMessageSender.ClearRememberedPlayers();
                UnityMainThreadDispatcher.DestroyInstance();
                Instance = null;
                Logger.Info($"{Name} has been unloaded!");
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "Error during plugin unload");
            }
        }

        private void RegisterEventHandlers()
        {
            U.Events.OnPlayerConnected += OnPlayerConnected;
            U.Events.OnPlayerDisconnected += OnPlayerDisconnected;
            ChatManager.onChatted += OnRuntimeChatted;
            ChatManager.onServerFormattingMessage += OnServerFormattingChatMessage;
            DamageTool.damagePlayerRequested += OnDamagePlayerRequested;
            PlayerLife.OnTellBleeding_Global += OnPlayerBleedingUpdated;
            PlayerLife.OnRevived_Global += OnPlayerRevived;
            PlayerLife.RocketLegacyOnDeath += OnPlayerDied;
        }

        private void UnregisterEventHandlers()
        {
            U.Events.OnPlayerConnected -= OnPlayerConnected;
            U.Events.OnPlayerDisconnected -= OnPlayerDisconnected;
            ChatManager.onChatted -= OnRuntimeChatted;
            ChatManager.onServerFormattingMessage -= OnServerFormattingChatMessage;
            DamageTool.damagePlayerRequested -= OnDamagePlayerRequested;
            PlayerLife.OnTellBleeding_Global -= OnPlayerBleedingUpdated;
            PlayerLife.OnRevived_Global -= OnPlayerRevived;
            PlayerLife.RocketLegacyOnDeath -= OnPlayerDied;
        }

        private void RefreshAllDisplays()
        {
            NameTagManager.RefreshAllPlayers();
        }

        private void InitializeServices()
        {
            PermissionService = new PermissionService(Configuration.Instance);
            PlayerStatsService = new PlayerStatsService(Configuration.Instance.StatsSettings);
            var damageAttributionService = new DamageAttributionService(Configuration.Instance.StatsSettings);
            DamageAttributionService = damageAttributionService;
            DeathAttributionResolver = new DeathAttributionResolver(damageAttributionService);
            NameTagManager = new NameTagManager(Configuration.Instance, PermissionService);
            ChatMessageService = new ChatMessageService(Configuration.Instance, NameTagManager, new RuntimeChatMessageSender());
            BroadcastService = new BroadcastService(Configuration.Instance, NameTagManager);

            Logger.Debug("All services initialized");
        }

        public void ReloadServices()
        {
            try
            {
                Logger.DebugEnabled = Configuration.Instance.DebugMode;
                Configuration.Instance.ClearCache();
                BroadcastService?.Dispose();
                DamageAttributionService?.ClearAll();
                PlayerStatsService?.Dispose();
                NameTagManager?.ClearAll();
                PermissionService?.ClearAllCache();
                InitializeServices();
                RefreshAllDisplays();
                BroadcastService?.StartAllBroadcasts();
                Logger.Info("Services reloaded successfully");
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "Failed to reload services");
            }
        }

        private void OnPlayerConnected(UnturnedPlayer player)
        {
            if (!IsValidPlayerConnection(player)) return;

            try
            {
                ApplyPlayerEffects(player);
                LogPlayerConnection(player);
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"Error handling player connect: {player?.DisplayName}");
            }
        }

        private bool IsValidPlayerConnection(UnturnedPlayer player)
        {
            return Configuration.Instance.Enabled
                && player != null
                && player.Player != null
                && player.CSteamID != CSteamID.Nil;
        }

        private void ApplyPlayerEffects(UnturnedPlayer player)
        {
            PlayerStatsService?.EnsurePlayer(player.CSteamID.m_SteamID);
            NameTagManager.ApplyDisplayEffect(player);
            BroadcastService?.SendWelcomeMessage(player);
        }

        private void LogPlayerConnection(UnturnedPlayer player)
        {
            Logger.Debug($"Player connected: {player.DisplayName}");
        }

        private void OnPlayerDisconnected(UnturnedPlayer player)
        {
            if (player == null) return;

            try
            {
                CleanupPlayerData(player);
                LogPlayerDisconnection(player);
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"Error handling player disconnect: {player?.DisplayName}");
            }
        }

        private void CleanupPlayerData(UnturnedPlayer player)
        {
            BroadcastService?.SendLeaveMessage(player);
            DamageAttributionService?.ClearVictim(player.CSteamID.m_SteamID);
            PlayerStatsService?.ReleasePlayer(player.CSteamID.m_SteamID);
            RuntimeChatMessageSender.ForgetPlayer(player.CSteamID.m_SteamID);
            NameTagManager.RemoveDisplayEffect(player);
            PermissionService?.ClearPlayerCache(player.CSteamID.m_SteamID);
        }

        private void LogPlayerDisconnection(UnturnedPlayer player)
        {
            Logger.Debug($"Player disconnected: {player.DisplayName}");
        }

        private void OnRuntimeChatted(SteamPlayer player, EChatMode chatMode, ref Color color, ref bool isRich, string message, ref bool isVisible)
        {
            if (!ShouldHandleRuntimeChatEvent(player, message, isVisible))
                return;

            // Formatting is applied later by ChatManager.onServerFormattingMessage.
            // Keep Unturned's native routing for global/local/group chat, but allow
            // our formatted name rich-text tags to render.
            color = Color.white;
            isRich = true;
        }

        private void OnServerFormattingChatMessage(SteamPlayer speaker, EChatMode mode, ref string text)
        {
            if (!ShouldFormatServerChatMessage(speaker, text))
            {
                ApplyVanillaChatFormatting(mode, ref text);
                return;
            }

            try
            {
                if (!TryCreateChatParticipant(speaker, out var sender))
                {
                    ApplyVanillaChatFormatting(mode, ref text);
                    return;
                }

                var formatted = ChatMessageService?.BuildFormattedMessage(sender, text, ToChatMessageMode(mode));
                if (formatted == null || string.IsNullOrEmpty(formatted.Message))
                {
                    ApplyVanillaChatFormatting(mode, ref text);
                    return;
                }

                text = formatted.Message;
                Logger.Debug($"Chat message formatted through native route - Player: {sender.DisplayName}, SteamID: {sender.SteamId}, ChatMode: {mode}", LogCategory.Plugin);
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"Error formatting chat message from {TryGetSteamId(speaker)}");
                ApplyVanillaChatFormatting(mode, ref text);
            }
        }

        private bool ShouldHandleRuntimeChatEvent(SteamPlayer player, string message, bool isVisible)
        {
            return Configuration.Instance.Enabled
                && Configuration.Instance.ApplyToChatMessages
                && player != null
                && isVisible
                && !string.IsNullOrEmpty(message)
                && !message.StartsWith("/", StringComparison.Ordinal);
        }

        private bool ShouldFormatServerChatMessage(SteamPlayer player, string message)
        {
            return Configuration.Instance.Enabled
                && Configuration.Instance.ApplyToChatMessages
                && player != null
                && !string.IsNullOrEmpty(message)
                && !message.StartsWith("/", StringComparison.Ordinal);
        }

        private static void ApplyVanillaChatFormatting(EChatMode mode, ref string text)
        {
            text = "%SPEAKER%: " + text;
            switch (mode)
            {
                case EChatMode.LOCAL:
                    text = "[A] " + text;
                    break;
                case EChatMode.GROUP:
                    text = "[G] " + text;
                    break;
            }
        }

        private static ChatMessageMode ToChatMessageMode(EChatMode chatMode)
        {
            switch (chatMode)
            {
                case EChatMode.LOCAL:
                    return ChatMessageMode.Local;
                case EChatMode.GROUP:
                    return ChatMessageMode.Group;
                case EChatMode.SAY:
                    return ChatMessageMode.Say;
                default:
                    return ChatMessageMode.Global;
            }
        }

        private static bool TryCreateChatParticipant(SteamPlayer client, out ChatMessageParticipant participant)
        {
            participant = null;

            try
            {
                var runtimePlayer = client?.player;
                if (runtimePlayer == null)
                    return false;

                var playerId = client.playerID ?? runtimePlayer.channel?.owner?.playerID;
                if (playerId == null || playerId.steamID == CSteamID.Nil || playerId.steamID.m_SteamID == 0)
                    return false;

                RuntimeChatMessageSender.RememberPlayer(playerId.steamID.m_SteamID, client);
                participant = new ChatMessageParticipant
                {
                    SteamId = playerId.steamID.m_SteamID,
                    DisplayName = playerId.characterName,
                    GroupId = TryGetGroupId(runtimePlayer),
                    Position = TryGetPosition(runtimePlayer)
                };
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Skipped invalid chat recipient snapshot: {ex.Message}", LogCategory.Plugin);
                return false;
            }
        }

        private static ulong TryGetSteamId(UnturnedPlayer player)
        {
            try
            {
                return player?.CSteamID.m_SteamID ?? 0UL;
            }
            catch
            {
                return 0UL;
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

        private static string TryGetDisplayName(UnturnedPlayer player)
        {
            try
            {
                return player?.DisplayName ?? player?.CharacterName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static ulong TryGetGroupId(Player player)
        {
            try
            {
                var quests = player?.quests;
                return quests == null ? 0UL : quests.groupID.m_SteamID;
            }
            catch
            {
                return 0UL;
            }
        }

        private static ChatMessagePosition TryGetPosition(Player player)
        {
            try
            {
                var transform = player?.transform;
                return ToChatPosition(transform == null ? Vector3.zero : transform.position);
            }
            catch
            {
                return ToChatPosition(Vector3.zero);
            }
        }

        private static ChatMessagePosition ToChatPosition(Vector3 position)
        {
            return new ChatMessagePosition(position.x, position.y, position.z);
        }

        private static DeathAttributionCause ToDeathAttributionCause(EDeathCause cause)
        {
            switch (cause)
            {
                case EDeathCause.BLEEDING:
                    return DeathAttributionCause.Bleeding;
                case EDeathCause.BURNING:
                    return DeathAttributionCause.Burning;
                case EDeathCause.BURNER:
                    return DeathAttributionCause.Burner;
                case EDeathCause.CHARGE:
                    return DeathAttributionCause.Charge;
                case EDeathCause.GRENADE:
                    return DeathAttributionCause.Grenade;
                case EDeathCause.LANDMINE:
                    return DeathAttributionCause.Landmine;
                case EDeathCause.MISSILE:
                    return DeathAttributionCause.Missile;
                case EDeathCause.ROADKILL:
                    return DeathAttributionCause.Roadkill;
                case EDeathCause.SPLASH:
                    return DeathAttributionCause.Splash;
                case EDeathCause.VEHICLE:
                    return DeathAttributionCause.Vehicle;
                case EDeathCause.GUN:
                    return DeathAttributionCause.Gun;
                case EDeathCause.MELEE:
                    return DeathAttributionCause.Melee;
                case EDeathCause.PUNCH:
                    return DeathAttributionCause.Punch;
                case EDeathCause.ZOMBIE:
                    return DeathAttributionCause.Zombie;
                default:
                    return DeathAttributionCause.Unknown;
            }
        }

        private void OnPlayerDied(PlayerLife sender, EDeathCause cause, ELimb limb, CSteamID instigator)
        {
            if (!IsPluginEnabled()) return;

            try
            {
                var victimSteamId = TryGetPlayerLifeSteamId(sender);
                var attribution = ResolveDeathAttribution(victimSteamId, cause, instigator);

                if (victimSteamId != 0)
                {
                    RecordPlayerDeath(victimSteamId, attribution);
                    RefreshCachedDisplayNames(victimSteamId, attribution.KillerSteamId);
                }

                BroadcastPlayerDeath(sender, cause, limb, instigator, attribution);

                if (victimSteamId != 0)
                {
                    ClearDeathAttribution(victimSteamId);
                }
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"Error handling player death (cause={cause}, limb={limb}, instigator={instigator.m_SteamID})");
            }
        }

        private bool IsPluginEnabled()
        {
            try
            {
                return Configuration?.Instance?.Enabled == true;
            }
            catch
            {
                return false;
            }
        }

        private static ulong TryGetPlayerLifeSteamId(PlayerLife sender)
        {
            if (sender == null)
                return 0;

            try
            {
                var owner = sender.channel?.owner;
                if (owner == null || owner.playerID == null)
                    return 0;

                return owner.playerID.steamID.m_SteamID;
            }
            catch
            {
                return 0;
            }
        }

        private DeathAttributionContext ResolveDeathAttribution(ulong victimSteamId, EDeathCause cause, CSteamID instigator)
        {
            try
            {
                return DeathAttributionResolver?.Resolve(new DeathAttributionRequest
                {
                    VictimSteamId = victimSteamId,
                    InstigatorSteamId = instigator.m_SteamID,
                    Cause = ToDeathAttributionCause(cause)
                }) ?? DeathAttributionContext.Empty;
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"Skipped death attribution resolution (victim={victimSteamId}, cause={cause}, instigator={instigator.m_SteamID})");
                return new DeathAttributionContext
                {
                    VictimSteamId = victimSteamId,
                    Source = DeathAttributionSource.None
                };
            }
        }

        private void RecordPlayerDeath(ulong victimSteamId, DeathAttributionContext attribution)
        {
            try
            {
                PlayerStatsService?.RecordPlayerDeath(victimSteamId, attribution?.KillerSteamId ?? 0);
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"Skipped player stats death update (victim={victimSteamId}, killer={attribution?.KillerSteamId ?? 0})");
            }
        }

        private void BroadcastPlayerDeath(PlayerLife sender, EDeathCause cause, ELimb limb, CSteamID instigator, DeathAttributionContext attribution)
        {
            try
            {
                BroadcastService?.HandlePlayerDeath(sender, cause, limb, instigator, attribution ?? DeathAttributionContext.Empty);
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"Skipped death broadcast (cause={cause}, limb={limb}, instigator={instigator.m_SteamID})");
            }
        }

        private void ClearDeathAttribution(ulong victimSteamId)
        {
            try
            {
                DamageAttributionService?.ClearVictim(victimSteamId);
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"Skipped clearing death attribution (victim={victimSteamId})");
            }
        }

        private void OnDamagePlayerRequested(ref DamagePlayerParameters parameters, ref bool shouldAllow)
        {
            if (!Configuration.Instance.Enabled)
                return;

            if (!shouldAllow)
                return;

            try
            {
                var victim = parameters.player;
                if (victim == null)
                    return;

                var attackerSteamId = parameters.killer.m_SteamID;
                var victimSteamId = victim.channel?.owner?.playerID.steamID.m_SteamID ?? 0UL;
                var bleedingModifier = parameters.bleedingModifier;
                if (attackerSteamId == 0 || victimSteamId == 0 || attackerSteamId == victimSteamId)
                {
                    return;
                }

                if (IsAttributionTrackableCause(parameters.cause))
                {
                    var attacker = TryResolvePlayer(attackerSteamId);
                    var weaponName = ResolveWeaponName(parameters, attacker);
                    var distanceMeters = ResolveHitDistanceMeters(parameters, attacker);
                    DamageAttributionService?.RecordAttributedHit(attackerSteamId, victimSteamId, parameters.cause, weaponName, distanceMeters);

                    var isAlreadyBleeding = victim.life?.isBleeding == true;
                    if (bleedingModifier != DamagePlayerParameters.Bleeding.Never
                        && bleedingModifier != DamagePlayerParameters.Bleeding.Heal
                        && IsBleedTrackableCause(parameters.cause)
                        && (bleedingModifier == DamagePlayerParameters.Bleeding.Always || isAlreadyBleeding))
                    {
                        DamageAttributionService?.HandleBleedingStateChanged(victimSteamId, true);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Skipped damage attribution update: {ex.Message}", LogCategory.Plugin);
            }
        }

        private void OnPlayerBleedingUpdated(PlayerLife playerLife)
        {
            if (!Configuration.Instance.Enabled || playerLife == null)
                return;

            try
            {
                var steamId = playerLife.channel?.owner?.playerID.steamID.m_SteamID ?? 0UL;
                if (steamId == 0)
                    return;

                DamageAttributionService?.HandleBleedingStateChanged(steamId, playerLife.isBleeding);
                DamageAttributionService?.ClearExpired();
            }
            catch (Exception ex)
            {
                Logger.Debug($"Skipped bleeding attribution update: {ex.Message}", LogCategory.Plugin);
            }
        }

        private void OnPlayerRevived(PlayerLife playerLife)
        {
            var steamId = playerLife?.channel?.owner?.playerID.steamID.m_SteamID ?? 0UL;
            if (steamId == 0)
                return;

            DamageAttributionService?.ClearVictim(steamId);
        }

        private static bool IsAttributionTrackableCause(EDeathCause cause)
        {
            switch (cause)
            {
                case EDeathCause.GUN:
                case EDeathCause.MELEE:
                case EDeathCause.PUNCH:
                case EDeathCause.VEHICLE:
                case EDeathCause.ROADKILL:
                case EDeathCause.GRENADE:
                case EDeathCause.MISSILE:
                case EDeathCause.CHARGE:
                case EDeathCause.SPLASH:
                case EDeathCause.SHRED:
                case EDeathCause.LANDMINE:
                case EDeathCause.SENTRY:
                case EDeathCause.KILL:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsBleedTrackableCause(EDeathCause cause)
        {
            return IsAttributionTrackableCause(cause);
        }

        private void RefreshCachedDisplayNames(ulong victimSteamId, ulong? killerSteamId)
        {
            RefreshCachedDisplayName(victimSteamId);

            if (killerSteamId.HasValue && killerSteamId.Value != 0 && killerSteamId.Value != victimSteamId)
                RefreshCachedDisplayName(killerSteamId.Value);
        }

        private void RefreshCachedDisplayName(ulong steamId)
        {
            try
            {
                var player = TryResolvePlayer(steamId);
                if (player != null)
                    NameTagManager?.RefreshPlayer(player);
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"Skipped cached display name refresh (steamId={steamId})");
            }
        }

        private static string ResolveWeaponName(DamagePlayerParameters parameters, UnturnedPlayer attacker)
        {
            if (attacker?.Player?.equipment?.asset is ItemAsset itemAsset && !string.IsNullOrWhiteSpace(itemAsset.itemName))
            {
                return itemAsset.itemName;
            }

            return GetFallbackWeaponName(parameters.cause);
        }

        private static int? ResolveHitDistanceMeters(DamagePlayerParameters parameters, UnturnedPlayer attacker)
        {
            var victim = parameters.player;
            if (attacker?.Player?.transform == null || victim?.transform == null)
            {
                return null;
            }

            try
            {
                var distance = Vector3.Distance(attacker.Player.transform.position, victim.transform.position);
                if (float.IsNaN(distance) || float.IsInfinity(distance))
                {
                    return null;
                }

                return Mathf.RoundToInt(distance);
            }
            catch
            {
                return null;
            }
        }

        private static UnturnedPlayer TryResolvePlayer(ulong steamId)
        {
            if (steamId == 0)
            {
                return null;
            }

            try
            {
                return UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
            }
            catch
            {
                return null;
            }
        }

        private static string GetFallbackWeaponName(EDeathCause cause)
        {
            switch (cause)
            {
                case EDeathCause.GUN: return "枪械";
                case EDeathCause.MELEE: return "近战";
                case EDeathCause.PUNCH: return "拳击";
                case EDeathCause.GRENADE: return "手雷";
                case EDeathCause.MISSILE: return "导弹";
                case EDeathCause.CHARGE: return "炸药";
                case EDeathCause.LANDMINE: return "地雷";
                case EDeathCause.SPLASH: return "爆炸";
                case EDeathCause.SHRED: return "陷阱";
                case EDeathCause.SENTRY: return "哨戒炮";
                case EDeathCause.VEHICLE:
                case EDeathCause.ROADKILL: return "载具";
                case EDeathCause.KILL: return "管理员处决";
                default: return null;
            }
        }
    }
}
