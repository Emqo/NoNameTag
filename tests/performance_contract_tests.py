#!/usr/bin/env python3
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def extract_method(source: str, method_name: str) -> str:
    match = re.search(
        rf"^\s*(?:public|private|protected|internal)\s+(?:static\s+)?[^\n=;]+?\b{re.escape(method_name)}\s*\(",
        source,
        re.MULTILINE,
    )
    assert match is not None, f"method {method_name} not found"
    idx = match.start()
    brace = source.find("{", idx)
    assert brace != -1, f"method {method_name} has no body"
    depth = 0
    for pos in range(brace, len(source)):
        char = source[pos]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return source[brace : pos + 1]
    raise AssertionError(f"method {method_name} body not closed")


def test_chat_formatting_uses_cached_display_name():
    chat_service = read("Services/ChatMessageService.cs")
    body = extract_method(chat_service, "BuildFormattedMessage")
    assert "_formattedNameProvider.GetFormattedPlayerName" in body
    assert "NameFormatter.FormatPlayerName" not in body


def test_damage_handler_resolves_attacker_player_once():
    plugin = read("NoNameTagPlugin.cs")
    body = extract_method(plugin, "OnDamagePlayerRequested")
    assert body.count("TryResolvePlayer(attackerSteamId)") == 1
    assert "ResolveWeaponName(parameters, attacker" in body
    assert "ResolveHitDistanceMeters(parameters, attacker" in body


def test_group_chat_does_not_wrap_each_client_as_unturned_player():
    plugin = read("NoNameTagPlugin.cs")
    chat_service = read("Services/ChatMessageService.cs")
    body = extract_method(plugin, "GetChatParticipants")
    assert "UnturnedPlayer.FromSteamPlayer(client)" not in body
    assert "TryCreateChatParticipant(client, out var participant)" in body
    assert "TryGetGroupId(runtimePlayer)" in plugin
    assert "sender.GroupId" in chat_service


def test_chat_event_short_circuits_before_recipient_snapshot():
    plugin = read("NoNameTagPlugin.cs")
    body = extract_method(plugin, "OnPlayerChatted")
    assert "if (!ShouldHandleChatEvent(player, message, cancel))" in body
    assert body.index("ShouldHandleChatEvent") < body.index("GetChatParticipants")
    assert "Recipients = RequiresRecipientSnapshot(mode) ? GetChatParticipants() : null" in body


def test_chat_sanitization_uses_single_pass_helper():
    chat_service = read("Services/ChatMessageService.cs")
    body = extract_method(chat_service, "BuildFormattedMessage")
    assert "RichTextSanitizer.SanitizeUntrustedPlayerText(message)" in body
    assert ".Replace(\"<\", \"\")" not in body


def test_chat_sender_steam_player_lookup_is_isolated_to_runtime_sender():
    plugin = read("NoNameTagPlugin.cs")
    runtime_sender = read("Services/RuntimeChatMessageSender.cs")
    chatted_body = extract_method(plugin, "OnPlayerChatted")
    assert "player.SteamPlayer()" not in chatted_body
    assert "RuntimeSteamPlayer" not in plugin
    assert "ResolveSteamPlayer(dispatch.Sender)" in runtime_sender
    assert "BroadcastHelper.GetSteamPlayer(new CSteamID(participant.SteamId))" in runtime_sender


def test_runtime_chat_sender_preserves_player_avatar_through_raw_chat_packet():
    runtime_sender = read("Services/RuntimeChatMessageSender.cs")
    send_body = extract_method(runtime_sender, "Send")
    assert "TrySendRawChatEntry(dispatch, runtimeMode)" in send_body
    assert "ResolveSteamPlayer(dispatch.Sender)" in send_body
    assert "HasPlayerId(sender)" in send_body
    raw_body = extract_method(runtime_sender, "TrySendRawChatEntry")
    assert "ResolveSpeakerSteamId(dispatch)" in raw_body
    assert "GetCurrentRecipients()" in raw_body
    assert "ResolveTransportConnection(dispatch.Recipient, resolvedRecipient)" in raw_body
    raw_send_body = extract_method(runtime_sender, "TrySendRawToConnection")
    assert "speakerSteamId" in raw_send_body
    assert "connection" in raw_send_body
    assert "dispatch.Message" in raw_send_body
    assert "dispatch.AvatarUrl ?? string.Empty" in raw_send_body
    assert "RememberedPlayers" in runtime_sender
    assert "RememberedConnections" in runtime_sender
    assert "RememberPlayer" in runtime_sender
    assert "RememberRuntimePlayer" in runtime_sender
    group_self_body = extract_method(runtime_sender, "IsGroupSelfEcho")
    assert "dispatch?.ChatMode == ChatMessageMode.Group" in group_self_body
    assert "IsSelfRecipient(dispatch)" in group_self_body
    self_recipient_body = extract_method(runtime_sender, "IsSelfRecipient")
    assert "dispatch.Sender.SteamId == dispatch.Recipient.SteamId" in self_recipient_body
    runtime_mode_body = extract_method(runtime_sender, "ToRuntimeMode")
    assert "case ChatMessageMode.Group:" in runtime_mode_body
    assert "return EChatMode.GROUP;" in runtime_mode_body
    assert "Group self echo sent via raw chat packet" in runtime_sender
    chat_manager_body = extract_method(runtime_sender, "TrySendViaChatManager")
    assert "sender," in chat_manager_body
    assert "recipient," in chat_manager_body
    assert "return true;" in chat_manager_body
    assert "return false;" in chat_manager_body
    assert "catch (Exception ex)" in chat_manager_body


def test_chat_participant_snapshot_uses_null_guard_helpers():
    plugin = read("NoNameTagPlugin.cs")
    snapshot_body = extract_method(plugin, "GetChatParticipants")
    assert "TryCreateChatParticipant(client, out var participant)" in snapshot_body
    assert "client.player.channel?.owner?.playerID" not in snapshot_body
    assert "client.player.quests == null" not in snapshot_body
    assert "TryGetGroupId" in plugin
    assert "TryGetPosition" in plugin


def test_formatted_name_cache_cleanup_includes_formatted_only_entries():
    manager = read("Services/NameTagManager.cs")
    body = extract_method(manager, "CleanupCache")
    compact_body = re.sub(r"\s+", "", body)
    assert "_playerEffects.Keys.Concat(_formattedPlayerNames.Keys)" in compact_body
    assert ".Distinct()" in body


def test_formatted_name_cache_miss_does_not_read_stats():
    manager = read("Services/NameTagManager.cs")
    body = extract_method(manager, "GetFormattedPlayerName")
    assert "FormatPlayerNameWithoutStats" in body
    assert "FormatPlayerName(" not in body

# Stage 1 hardening contracts from docs/prd/nonametag-architecture-performance-hardening.md

def test_targeted_death_messages_send_to_recipient_not_sender_slot():
    death_service = read("Services/DeathMessageService.cs")
    body = extract_method(death_service, "SendToPlayer")
    assert "ChatManager.serverSendMessage(message, Color.white, null, steamPlayer" in body
    assert "ChatManager.serverSendMessage(message, Color.white, steamPlayer, null" not in body


def test_player_disconnect_releases_player_stats_cache():
    plugin = read("NoNameTagPlugin.cs")
    body = extract_method(plugin, "CleanupPlayerData")
    assert "PlayerStatsService?.ReleasePlayer(player.CSteamID.m_SteamID)" in body
    assert "RuntimeChatMessageSender.ForgetPlayer(player.CSteamID.m_SteamID)" in body


def test_release_player_keeps_dirty_cache_when_flush_fails():
    stats_service = read("Services/PlayerStatsService.cs")
    body = extract_method(stats_service, "ReleasePlayer")
    assert "var flushed = FlushDirtyRecords()" in body
    assert "if (!flushed || _dirtySteamIds.ContainsKey(steamId))" in body
    assert body.index("return;") < body.index("_cachedStats.TryRemove(steamId, out _)")
    flush_body = extract_method(stats_service, "FlushDirtyRecords")
    assert "private bool FlushDirtyRecords()" in stats_service
    assert "return false;" in flush_body


def test_broadcast_delay_seconds_is_fully_removed():
    searched_paths = [
        "Models/BroadcastConfig.cs",
        "Utilities/ConfigValidator.cs",
        "NoNameTagConfiguration.cs",
        "NoNameTagConfiguration.example.xml",
        "NoNameTagConfiguration.xml",
        "README.md",
    ]
    for path in searched_paths:
        assert "DelaySeconds" not in read(path), f"DelaySeconds remains in {path}"


def test_legacy_configuration_file_is_removed():
    assert not (ROOT / "NoNameTag.configuration.xml").exists()


def test_checked_in_configuration_matches_authoritative_example():
    assert read("NoNameTagConfiguration.xml") == read("NoNameTagConfiguration.example.xml")


def test_unsupported_overhead_and_avatar_settings_are_removed_from_user_docs():
    searched_paths = ["NoNameTagConfiguration.example.xml", "NoNameTagConfiguration.xml", "README.md"]
    forbidden_terms = ["ApplyToNameTags", "AvatarSettings", "NameTagDisplayService", "头顶"]
    for path in searched_paths:
        text = read(path)
        for term in forbidden_terms:
            assert term not in text, f"{term} remains in {path}"


def test_untrusted_player_text_uses_shared_rich_text_sanitizer():
    chat_service = read("Services/ChatMessageService.cs")
    welcome = read("Services/WelcomeMessageService.cs")
    death = read("Services/DeathMessageService.cs")
    assert (ROOT / "Utilities/RichTextSanitizer.cs").exists()
    assert "RichTextSanitizer.SanitizeUntrustedPlayerText(message)" in chat_service
    assert "RichTextSanitizer.SanitizeUntrustedPlayerText(player.DisplayName" in welcome
    assert "RichTextSanitizer.SanitizeUntrustedPlayerText(playerName" in death


def test_trusted_config_text_keeps_rich_text_surface():
    welcome = read("Services/WelcomeMessageService.cs")
    welcome_body = extract_method(welcome, "SendWelcomeMessage")
    assert "RichTextSanitizer.SanitizeUntrustedPlayerText(player.DisplayName)" in welcome_body
    assert "RichTextSanitizer.SanitizeUntrustedPlayerText(welcomeConfig.Text)" not in welcome_body
    assert 'messageText.Replace("{", "<").Replace("}", ">")' in welcome_body


def test_color_values_match_documented_unity_color_names():
    validator = read("Utilities/ConfigValidator.cs")
    formatter = read("Utilities/NameFormatter.cs")
    example = read("NoNameTagConfiguration.example.xml")
    readme = read("README.md")
    for unsupported_name in ["orange", "purple"]:
        assert unsupported_name not in validator
        assert unsupported_name not in formatter
        assert unsupported_name not in example
        assert unsupported_name not in readme


def test_ci_and_release_workflows_run_stage1_tests_before_build_or_publish():
    ci_path = ROOT / ".github/workflows/ci.yml"
    assert ci_path.exists()
    ci = ci_path.read_text(encoding="utf-8")
    release = read(".github/workflows/manual-release.yml")
    for workflow_name, workflow in [("ci", ci), ("release", release)]:
        assert "python3 tests/performance_contract_tests.py" in workflow or "python tests/performance_contract_tests.py" in workflow, workflow_name
        assert "dotnet test" in workflow, workflow_name
        assert "dotnet build --configuration Release" in workflow, workflow_name
    assert "ILRepack.Lib.MSBuild.Task" in read("NoNameTag.csproj")


def test_stage2_version_is_1_1_18():
    assert "<Version>1.1.18</Version>" in read("NoNameTag.csproj")


def test_stage2_chat_service_and_sender_seam_are_wired():
    plugin = read("NoNameTagPlugin.cs")
    models = read("Services/ChatMessageModels.cs")
    assert (ROOT / "Services/ChatMessageService.cs").exists()
    assert (ROOT / "Services/IChatMessageSender.cs").exists()
    assert (ROOT / "Services/IFormattedNameProvider.cs").exists()
    assert (ROOT / "Services/RuntimeChatMessageSender.cs").exists()
    assert "SteamPlayer" not in models
    assert "UnityEngine" not in models
    assert "SDG.Unturned" not in models
    assert "ChatMessageService = new ChatMessageService" in plugin
    assert "new RuntimeChatMessageSender()" in plugin
    assert "using Rocket.Unturned.Player" not in read("tests/NoNameTag.Tests/ChatMessageServiceTests.cs")
    chatted_body = extract_method(plugin, "OnPlayerChatted")
    assert "ChatMessageService?.HandleChat" in chatted_body
    assert "ChatManager.serverSendMessage" not in chatted_body


def test_stage2_death_attribution_resolved_once_and_shared():
    plugin = read("NoNameTagPlugin.cs")
    body = extract_method(plugin, "OnPlayerDied")
    resolver_body = extract_method(plugin, "ResolveDeathAttribution")
    stats_body = extract_method(plugin, "RecordPlayerDeath")
    broadcast_body = extract_method(plugin, "BroadcastPlayerDeath")
    clear_body = extract_method(plugin, "ClearDeathAttribution")
    assert resolver_body.count("DeathAttributionResolver?.Resolve") == 1
    assert "RecordPlayerDeath(victimSteamId, attribution)" in body
    assert "PlayerStatsService?.RecordPlayerDeath(victimSteamId, attribution?.KillerSteamId ?? 0)" in stats_body
    assert "BroadcastPlayerDeath(sender, cause, limb, instigator, attribution)" in body
    assert "BroadcastService?.HandlePlayerDeath(sender, cause, limb, instigator, attribution ?? DeathAttributionContext.Empty)" in broadcast_body
    assert "ClearDeathAttribution(victimSteamId)" in body
    assert clear_body.count("DamageAttributionService?.ClearVictim(victimSteamId)") == 1


def test_death_handler_isolates_runtime_null_reference_boundaries():
    plugin = read("NoNameTagPlugin.cs")
    body = extract_method(plugin, "OnPlayerDied")
    assert "TryGetPlayerLifeSteamId(sender)" in body
    assert "sender?.channel?.owner?.playerID.steamID" not in body
    for method in [
        "TryGetPlayerLifeSteamId",
        "ResolveDeathAttribution",
        "RecordPlayerDeath",
        "BroadcastPlayerDeath",
        "ClearDeathAttribution",
        "RefreshCachedDisplayName",
    ]:
        method_body = extract_method(plugin, method)
        assert "catch" in method_body, method


def test_stage2_death_message_consumes_attribution_context_without_requerying_damage_service():
    death_service = read("Services/DeathMessageService.cs")
    assert "DeathAttributionContext attribution" in death_service
    assert "TryGetBleedAttribution" not in death_service
    assert "TryGetRecentAttribution" not in death_service
    assert "ResolveKiller(resolvedAttribution" in death_service


def test_death_message_does_not_call_unturned_player_from_player_without_runtime_guards():
    death_service = read("Services/DeathMessageService.cs")
    handle_body = extract_method(death_service, "HandlePlayerDeath")
    create_body = extract_method(death_service, "TryCreateUnturnedPlayer")
    assert "UnturnedPlayer.FromPlayer(sender.player)" not in handle_body
    assert "var player = sender?.player" in create_body
    assert "player == null" in create_body
    assert "UnturnedPlayer.FromPlayer(player)" in create_body
    assert "catch (Exception ex)" in create_body


def test_death_handler_uses_pre_dead_rocket_legacy_event():
    plugin = read("NoNameTagPlugin.cs")
    register_body = extract_method(plugin, "RegisterEventHandlers")
    unregister_body = extract_method(plugin, "UnregisterEventHandlers")
    assert "PlayerLife.RocketLegacyOnDeath += OnPlayerDied" in register_body
    assert "PlayerLife.RocketLegacyOnDeath -= OnPlayerDied" in unregister_body
    assert "PlayerLife.onPlayerDied += OnPlayerDied" not in register_body
    assert "PlayerLife.onPlayerDied -= OnPlayerDied" not in unregister_body


def test_broadcast_helper_handles_clients_with_missing_player_id():
    helper = read("Services/BroadcastHelper.cs")
    body = extract_method(helper, "GetSteamPlayer")
    assert "PlayerTool.getSteamPlayer(steamId)" in body
    assert "var clients = Provider.clients" in body
    assert "var playerId = client?.playerID" in body
    assert "playerId != null && playerId.steamID == steamId" in body
    assert body.count("catch") >= 2


def test_litedb_is_merged_into_plugin_release_build():
    assert (ROOT / "ILRepack.targets").exists()
    assert not (ROOT / "FodyWeavers.xml").exists()
    csproj = read("NoNameTag.csproj")
    targets = read("ILRepack.targets")
    assert "ILRepack.Lib.MSBuild.Task" in csproj
    assert "Costura.Fody" not in csproj
    assert "Libraries\\LiteDB.dll" in targets
    assert "Internalize=\"true\"" in targets


if __name__ == "__main__":
    tests = [name for name in globals() if name.startswith("test_")]
    failures = []
    for test_name in tests:
        try:
            globals()[test_name]()
            print(f"PASS {test_name}")
        except Exception as exc:
            failures.append((test_name, exc))
            print(f"FAIL {test_name}: {exc}")
    if failures:
        raise SystemExit(1)
