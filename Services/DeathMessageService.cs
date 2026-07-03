using Emqo.NoNameTag.Models;
using Emqo.NoNameTag.Utilities;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using System;
using UnityEngine;
using Logger = Emqo.NoNameTag.Utilities.PluginLogger;

namespace Emqo.NoNameTag.Services
{
    /// <summary>
    /// 死亡消息服务
    /// </summary>
    internal class DeathMessageService
    {
        private const string DefaultHeadshotTag = " [爆头]";
        private const string UnknownWeaponText = "未知武器";
        private const string UnknownDistanceText = "-";
        private readonly NoNameTagConfiguration _config;
        private readonly INameTagManager _nameTagManager;

        public DeathMessageService(
            NoNameTagConfiguration config,
            INameTagManager nameTagManager)
        {
            _config = config;
            _nameTagManager = nameTagManager;
        }

        public void HandlePlayerDeath(PlayerLife sender, EDeathCause cause, ELimb limb, CSteamID instigator, DeathAttributionContext attribution)
        {
            if (!_config.Enabled || _config.Broadcast?.DeathMessage == null || !_config.Broadcast.DeathMessage.Enabled)
                return;

            if (_config.Broadcast.DeathMessage.DisplayMode == DisplayMode.None)
                return;

            try
            {
                var victim = TryCreateUnturnedPlayer(sender);
                if (victim == null)
                    return;

                var victimSteamId = TryGetSteamId(victim);
                var resolvedAttribution = attribution ?? CreateDirectAttribution(victimSteamId, instigator);
                var killer = ResolveKiller(resolvedAttribution, victimSteamId);
                var message = FormatDeathMessage(victim, killer, cause, limb, resolvedAttribution.WeaponName, resolvedAttribution.DistanceMeters);
                if (!string.IsNullOrEmpty(message))
                {
                    BroadcastDeathMessage(message, victim, killer);
                }
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "Error handling player death", LogCategory.DeathMessage);
            }
        }

        private static UnturnedPlayer TryCreateUnturnedPlayer(PlayerLife sender)
        {
            try
            {
                var owner = sender?.channel?.owner;
                if (owner?.player != null)
                    return UnturnedPlayer.FromSteamPlayer(owner);

                var player = sender?.player;
                if (player == null)
                    return null;

                return UnturnedPlayer.FromPlayer(player);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not resolve death victim: {ex.Message}", LogCategory.DeathMessage);
                return null;
            }
        }

        private string FormatDeathMessage(UnturnedPlayer victim, UnturnedPlayer killer, EDeathCause cause, ELimb limb, string attributedWeaponName, int? attributedDistanceMeters)
        {
            if (victim == null)
                return null;

            try
            {
                var deathConfig = _config.Broadcast.DeathMessage;
                if (deathConfig == null)
                    return null;

                bool isSelfKill = false;
                bool isPlayerKill = false;
                var victimSteamId = TryGetSteamId(victim);
                var killerSteamId = TryGetSteamId(killer);

                if (killerSteamId != 0 && victimSteamId != 0)
                {
                    isSelfKill = killerSteamId == victimSteamId;
                    isPlayerKill = !isSelfKill;
                }

                string format;
                if (isSelfKill)
                    format = deathConfig.SelfKillFormat;
                else if (isPlayerKill)
                    format = GetPlayerKillFormatByCause(cause, deathConfig);
                else
                    format = GetFormatByCause(cause, deathConfig);

                if (string.IsNullOrWhiteSpace(format))
                    format = deathConfig.DefaultFormat;

                var victimName = FormatPlayerName(victim);
                var killerName = killer != null ? FormatPlayerName(killer) : "";
                var isLikelyPlayerKill = isPlayerKill || IsPlayerKillCause(cause);
                var isHeadshot = isLikelyPlayerKill && IsHeadshotLimb(limb);
                var weaponName = GetWeaponName(killer, cause, isLikelyPlayerKill, attributedWeaponName);
                var killDistance = GetKillDistance(victim, killer, isLikelyPlayerKill, attributedDistanceMeters);
                var hasWeaponPlaceholder = format.IndexOf("{weapon}", StringComparison.OrdinalIgnoreCase) >= 0;
                var hasDistancePlaceholder = format.IndexOf("{distance}", StringComparison.OrdinalIgnoreCase) >= 0;

                var result = format
                    .Replace("{victim}", victimName)
                    .Replace("{killer}", killerName)
                    .Replace("{weapon}", weaponName)
                    .Replace("{distance}", killDistance);

                if (isLikelyPlayerKill)
                    result = AppendKillDetailsIfNeeded(result, weaponName, killDistance, hasWeaponPlaceholder, hasDistancePlaceholder);

                if (isHeadshot)
                {
                    var headshotTag = string.IsNullOrWhiteSpace(deathConfig.HeadshotTag)
                        ? DefaultHeadshotTag
                        : deathConfig.HeadshotTag;
                    result = $"{result}{headshotTag}";
                }

                // 应用死亡消息字体颜色和大小
                if (!string.IsNullOrEmpty(deathConfig.FontColor))
                    result = NameFormatter.WrapWithStyle(result, deathConfig.FontColor, deathConfig.FontSize);
                else if (deathConfig.FontSize > 0)
                    result = $"<size={deathConfig.FontSize}>{result}</size>";

                return result;
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "Error in FormatDeathMessage", LogCategory.DeathMessage);
                return null;
            }
        }

        private string GetPlayerKillFormatByCause(EDeathCause cause, DeathMessageConfig config)
        {
            switch (cause)
            {
                case EDeathCause.GUN: return config.GunFormat;
                case EDeathCause.MELEE: return config.MeleeFormat;
                case EDeathCause.PUNCH: return config.PunchFormat;
                case EDeathCause.GRENADE: return config.GrenadeFormat;
                case EDeathCause.SHRED: return config.ShredFormat;
                case EDeathCause.LANDMINE: return config.LandmineFormat;
                case EDeathCause.ARENA: return config.ArenaFormat;
                case EDeathCause.MISSILE: return config.MissileFormat;
                case EDeathCause.CHARGE: return config.ChargeFormat;
                case EDeathCause.SPLASH: return config.SplashFormat;
                case EDeathCause.SENTRY: return config.SentryFormat;
                case EDeathCause.VEHICLE:
                case EDeathCause.ROADKILL: return config.VehicleFormat;
                case EDeathCause.KILL: return config.KillFormat;
                default: return config.Format;
            }
        }

        private string GetWeaponName(UnturnedPlayer killer, EDeathCause cause, bool isLikelyPlayerKill, string attributedWeaponName)
        {
            if (!string.IsNullOrWhiteSpace(attributedWeaponName))
                return attributedWeaponName;

            if (!isLikelyPlayerKill || killer == null || killer.Player == null)
                return UnknownWeaponText;

            try
            {
                var equipment = killer.Player.equipment;
                var itemAsset = equipment?.asset as ItemAsset;
                if (itemAsset != null && !string.IsNullOrWhiteSpace(itemAsset.itemName))
                    return itemAsset.itemName;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not resolve weapon name: {ex.Message}", LogCategory.DeathMessage);
            }

            return GetWeaponNameByCause(cause);
        }

        private string GetWeaponNameByCause(EDeathCause cause)
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
                case EDeathCause.SENTRY: return "哨兵";
                case EDeathCause.VEHICLE:
                case EDeathCause.ROADKILL: return "载具";
                default: return UnknownWeaponText;
            }
        }

        private string GetKillDistance(UnturnedPlayer victim, UnturnedPlayer killer, bool isLikelyPlayerKill, int? attributedDistanceMeters)
        {
            if (attributedDistanceMeters.HasValue && attributedDistanceMeters.Value >= 0)
                return $"{attributedDistanceMeters.Value}m";

            if (!isLikelyPlayerKill
                || victim?.Player?.transform == null
                || killer?.Player?.transform == null)
                return UnknownDistanceText;

            try
            {
                var distance = Vector3.Distance(victim.Player.transform.position, killer.Player.transform.position);
                if (float.IsNaN(distance) || float.IsInfinity(distance))
                    return UnknownDistanceText;

                return $"{Mathf.RoundToInt(distance)}m";
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not resolve kill distance: {ex.Message}", LogCategory.DeathMessage);
                return UnknownDistanceText;
            }
        }

        private string AppendKillDetailsIfNeeded(string baseMessage, string weaponName, string killDistance, bool hasWeaponPlaceholder, bool hasDistancePlaceholder)
        {
            if (hasWeaponPlaceholder && hasDistancePlaceholder)
                return baseMessage;

            if (!hasWeaponPlaceholder && !hasDistancePlaceholder)
                return $"{baseMessage} [{weaponName} | {killDistance}]";

            if (!hasWeaponPlaceholder)
                return $"{baseMessage} [武器:{weaponName}]";

            return $"{baseMessage} [距离:{killDistance}]";
        }

        private bool IsHeadshotLimb(ELimb limb)
        {
            var limbName = limb.ToString();
            return limbName.Equals("SKULL", StringComparison.OrdinalIgnoreCase)
                || limbName.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPlayerKillCause(EDeathCause cause)
        {
            switch (cause)
            {
                case EDeathCause.GUN:
                case EDeathCause.MELEE:
                case EDeathCause.PUNCH:
                case EDeathCause.GRENADE:
                case EDeathCause.SHRED:
                case EDeathCause.LANDMINE:
                case EDeathCause.ARENA:
                case EDeathCause.MISSILE:
                case EDeathCause.CHARGE:
                case EDeathCause.SPLASH:
                case EDeathCause.SENTRY:
                case EDeathCause.VEHICLE:
                case EDeathCause.ROADKILL:
                case EDeathCause.KILL:
                    return true;
                default:
                    return false;
            }
        }

        private string GetFormatByCause(EDeathCause cause, DeathMessageConfig config)
        {
            switch (cause)
            {
                case EDeathCause.BLEEDING: return config.BleedingFormat;
                case EDeathCause.ZOMBIE: return config.ZombieFormat;
                case EDeathCause.ANIMAL: return config.AnimalFormat;
                case EDeathCause.VEHICLE:
                case EDeathCause.ROADKILL: return config.VehicleFormat;
                case EDeathCause.BONES: return config.FallFormat;
                case EDeathCause.BREATH: return config.DrownFormat;
                case EDeathCause.FREEZING: return config.FreezeFormat;
                case EDeathCause.BURNING:
                case EDeathCause.BURNER: return config.BurnFormat;
                case EDeathCause.FOOD: return config.FoodFormat;
                case EDeathCause.WATER: return config.WaterFormat;
                case EDeathCause.INFECTION: return config.InfectionFormat;
                case EDeathCause.SUICIDE: return config.SelfKillFormat;
                case EDeathCause.GUN: return config.GunFormat;
                case EDeathCause.MELEE: return config.MeleeFormat;
                case EDeathCause.PUNCH: return config.PunchFormat;
                case EDeathCause.GRENADE: return config.GrenadeFormat;
                case EDeathCause.SHRED: return config.ShredFormat;
                case EDeathCause.LANDMINE: return config.LandmineFormat;
                case EDeathCause.ARENA: return config.ArenaFormat;
                case EDeathCause.MISSILE: return config.MissileFormat;
                case EDeathCause.CHARGE: return config.ChargeFormat;
                case EDeathCause.SPLASH: return config.SplashFormat;
                case EDeathCause.SENTRY: return config.SentryFormat;
                case EDeathCause.ACID: return config.AcidFormat;
                case EDeathCause.BOULDER: return config.BoulderFormat;
                case EDeathCause.SPARK: return config.SparkFormat;
                case EDeathCause.SPIT: return config.SpitFormat;
                case EDeathCause.KILL: return config.KillFormat;
                default: return config.DefaultFormat;
            }
        }

        private string FormatPlayerName(UnturnedPlayer player)
        {
            if (player == null)
                return "Unknown";

            string playerName;
            try
            {
                playerName = player.DisplayName ?? player.CharacterName ?? "Unknown";
            }
            catch
            {
                playerName = "Unknown";
            }

            playerName = RichTextSanitizer.SanitizeUntrustedPlayerText(playerName);
            var steamId = TryGetSteamId(player);
            var group = steamId != 0 ? _nameTagManager?.GetPlayerEffect(steamId) : null;
            return NameFormatter.FormatPlayerName(playerName, steamId, group?.DisplayEffect);
        }

        private UnturnedPlayer ResolveKiller(DeathAttributionContext attribution, ulong victimSteamId)
        {
            if (attribution?.KillerSteamId == null)
                return null;

            return TryResolvePlayer(new CSteamID(attribution.KillerSteamId.Value), victimSteamId);
        }

        private DeathAttributionContext CreateDirectAttribution(ulong victimSteamId, CSteamID instigator)
        {
            if (instigator == CSteamID.Nil || instigator.m_SteamID == 0 || instigator.m_SteamID == victimSteamId)
                return DeathAttributionContext.Empty;

            return new DeathAttributionContext
            {
                VictimSteamId = victimSteamId,
                KillerSteamId = instigator.m_SteamID,
                Source = DeathAttributionSource.DirectInstigator
            };
        }

        private UnturnedPlayer TryResolvePlayer(CSteamID steamId, ulong victimSteamId)
        {
            if (steamId == CSteamID.Nil || steamId.m_SteamID == 0 || steamId.m_SteamID == victimSteamId)
                return null;

            try
            {
                return UnturnedPlayer.FromCSteamID(steamId);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not get killer: {ex.Message}", LogCategory.DeathMessage);
                return null;
            }
        }

        private ulong TryGetSteamId(UnturnedPlayer player)
        {
            if (player == null)
                return 0;

            try
            {
                return player.CSteamID.m_SteamID;
            }
            catch
            {
                return 0;
            }
        }

        private void BroadcastDeathMessage(string message, UnturnedPlayer victim, UnturnedPlayer killer)
        {
            var displayMode = _config.Broadcast.DeathMessage.DisplayMode;
            var visibility = _config.Broadcast.DeathMessage.Visibility;

            if (displayMode == DisplayMode.Console || displayMode == DisplayMode.Both)
                Logger.Info($"[死亡] {BroadcastHelper.StripRichText(message)}", LogCategory.DeathMessage);

            if (displayMode == DisplayMode.Chat || displayMode == DisplayMode.Both)
                SendDeathMessageToChat(message, visibility, victim, killer);

            Logger.Info($"Death message broadcasted: {BroadcastHelper.StripRichText(message)} (DisplayMode: {displayMode}, Visibility: {visibility})", LogCategory.DeathMessage);
        }

        private void SendDeathMessageToChat(string message, DeathMessageVisibility visibility, UnturnedPlayer victim, UnturnedPlayer killer)
        {
            try
            {
                switch (visibility)
                {
                    case DeathMessageVisibility.All:
                        ChatManager.serverSendMessage(message, Color.white, null, null, EChatMode.GLOBAL, null, true);
                        break;

                    case DeathMessageVisibility.KillerOnly:
                        SendToPlayer(killer, message);
                        break;

                    case DeathMessageVisibility.VictimOnly:
                        SendToPlayer(victim, message);
                        break;

                    case DeathMessageVisibility.KillerAndVictimOnly:
                        SendToPlayer(killer, message);
                        SendToPlayer(victim, message);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to send death message to chat: {ex}", LogCategory.DeathMessage);
            }
        }

        private void SendToPlayer(UnturnedPlayer player, string message)
        {
            if (player == null) return;
            var steamPlayer = BroadcastHelper.GetSteamPlayer(player.CSteamID);
            if (steamPlayer != null)
                ChatManager.serverSendMessage(message, Color.white, null, steamPlayer, EChatMode.SAY, null, true);
        }
    }
}
