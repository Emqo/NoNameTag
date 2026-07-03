using SDG.Unturned;
using Steamworks;
using System.Text.RegularExpressions;

namespace Emqo.NoNameTag.Services
{
    /// <summary>
    /// 广播服务共享工具方法
    /// </summary>
    internal static class BroadcastHelper
    {
        private static readonly Regex RichTextRegex = new Regex("<.*?>", RegexOptions.Compiled);

        /// <summary>
        /// 移除富文本标签
        /// </summary>
        public static string StripRichText(string text)
        {
            return RichTextRegex.Replace(text, string.Empty);
        }

        /// <summary>
        /// 根据 CSteamID 获取 SteamPlayer
        /// </summary>
        public static SteamPlayer GetSteamPlayer(CSteamID steamId)
        {
            if (steamId == CSteamID.Nil || steamId.m_SteamID == 0)
                return null;

            try
            {
                var player = PlayerTool.getSteamPlayer(steamId);
                if (player != null)
                    return player;
            }
            catch
            {
                // Fall back to the Provider.clients snapshot below. Some runtime builds can
                // throw while player state is being connected/disconnected.
            }

            var clients = Provider.clients;
            if (clients == null)
                return null;

            foreach (var client in clients)
            {
                try
                {
                    var playerId = client?.playerID;
                    if (playerId != null && playerId.steamID == steamId)
                        return client;
                }
                catch
                {
                    // Ignore transient partial client entries.
                }
            }

            return null;
        }

        /// <summary>
        /// 替换服务器变量
        /// </summary>
        public static string ReplaceVariables(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            text = text.Replace("{server_icon}", Provider.configData.Browser.Icon);
            text = text.Replace("{server_thumbnail}", Provider.configData.Browser.Thumbnail);
            text = text.Replace("{server_name}", Provider.serverName);
            text = text.Replace("{server_players}", Provider.clients.Count.ToString("N0"));
            text = text.Replace("{server_maxplayers}", Provider.maxPlayers.ToString("N0"));
            text = text.Replace("{server_map}", Level.info?.name ?? string.Empty);
            text = text.Replace("{server_mode}", Provider.mode.ToString());

            return text;
        }
    }
}
