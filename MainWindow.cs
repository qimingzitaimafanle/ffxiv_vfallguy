using System;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace vfallguy;

public class MainWindow : Window, IDisposable
{
    private GameEvents _gameEvents = new();
    private DebugDrawer _drawer = new();
    private AutoJoinLeave _automation = new();
    private Map? _map;
    private DateTime _now;
    private Vector3 _prevPos;
    private Vector3 _movementDirection;
    private float _movementSpeed;
    private bool _autoJoin;
    private bool _autoLeaveIfNotSolo;
    private bool _showAOEs;
    private bool _showAOEText;
    private bool _showPathfind;
    private DateTime _autoJoinAt = DateTime.MaxValue;
    private DateTime _autoLeaveAt = DateTime.MaxValue;
    private int _numPlayersInDuty;
    private float _autoJoinDelay = 0.5f;
    private float _autoLeaveDelay = 3;
    private int _autoLeaveLimit = 1;
    private bool _autoFarmingMode;
    private bool _autoWinLeave;
    private string _webSocketUrl = "";
    private int _webSocketPort;
    private string _qqPrivateChatNumber = "";
    private string _qqBotNumber = "";
    private string _gameName = "";
    private HttpClient _httpClient = new();
    private int _battlePlayerCount = 1;
    private bool _enableQQBotConfig = false;
    private bool _showDebugWindow = false;
    private string _debugMessages = "";
    private bool _captureAddonText = false;
    private bool _captureAddonTextOnce = false;
    private BotConfiguration _config = new();
    // 货币（MGF）追踪
    private int _mgfTotal = -1; // 未知时为 -1
    private int _lastMgfGain = 0;
    private bool _wasBoundByDuty = false;
    private DateTime _enteredDutyAt = DateTime.MinValue;
    private int _lastLoggedMgf = -2; // 调试日志去抖：仅在变更时输出

    public MainWindow(IDalamudPluginInterface pluginInterface) : base("vfailguy 改")
    {
        // 初始化配置
        _config.Initialize(pluginInterface);
        LoadConfig();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("vfallguy/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Service.ChatGui.ChatMessage += OnChatMessage;

        // 监听 Addon 的 PostSetup：仅在入口 NPC 对话框出现时读取 MGF 总数
        // 移除 PostUpdate 频繁轮询，降低每帧 UI 节点扫描带来的开销
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OnAddonLifecycle);
    }

    public void Dispose()
    {
        Service.ChatGui.ChatMessage -= OnChatMessage;
        Service.AddonLifecycle.UnregisterListener(OnAddonLifecycle);
        _map?.Dispose();
        _gameEvents.Dispose();
        _automation.Dispose();
    }

    private void LoadConfig()
    {
        _gameName = _config.GameName;
        _qqPrivateChatNumber = _config.QqPrivateChatNumber;
        _qqBotNumber = _config.QqBotNumber;
        _webSocketUrl = _config.WebSocketUrl;
        _webSocketPort = _config.WebSocketPort;
        _battlePlayerCount = _config.BattlePlayerCount;
    }

    private unsafe void OnAddonLifecycle(AddonEvent type, AddonArgs args)
    {
        // 非副本时尝试读取（放宽区域限制，以兼容不同入口点）
        if (Service.Condition[ConditionFlag.BoundByDuty])
            return;

        // Dalamud v13: AddonArgs.Addon 为 AtkUnitBasePtr 包装类型
        var addonPtr = args.Addon;
        if (addonPtr.IsNull)
            return;
        var unit = (AtkUnitBase*)addonPtr.Address;
        if (unit == null)
            return;

        // FGSEnterDialog：在 PostSetup 阶段用专用索引读取，避免误读
        if (type == AddonEvent.PostSetup && args is AddonSetupArgs setupArgs && (args.AddonName ?? string.Empty) == "FGSEnterDialog")
        {
            TryReadMgfFromFgsEnterDialog(setupArgs);
        }
        // 取消泛解析：不在 PostUpdate 或其他面板上扫描文本节点，避免每帧性能损耗

        // 调试：抓取当前可见面板的文本内容，帮助定位节点与名称
        // 调试采集：避免卡顿，改为“只采集一次”优先
        if (_captureAddonTextOnce && unit->IsVisible && unit->UldManager.LoadedState == AtkLoadState.Loaded)
        {
            DumpAddonText(unit, args.AddonName ?? string.Empty);
            _captureAddonTextOnce = false;
        }
        else if (_captureAddonText && unit->IsVisible && unit->UldManager.LoadedState == AtkLoadState.Loaded)
        {
            // 连续采集模式：可能造成卡顿，仅用于短时间排查
            DumpAddonText(unit, args.AddonName ?? string.Empty);
        }

        // 如需调试可临时启用 DumpSetupValues，但默认关闭以减少开销
    }

    private unsafe void TryReadMgfFromAddon(AtkUnitBase* addon, string addonName)
    {
        if (addonName == "FGSEnterDialog")
            return; // 该面板改由专读逻辑在 PostSetup 中读取，避免泛解析误读
        bool indicatorSeen = false;
        int numericFound = -1;
        if (addon == null || !addon->IsVisible || addon->UldManager.LoadedState != AtkLoadState.Loaded)
            return;
        var mgr = &addon->UldManager;
        var nodes = mgr->NodeList;
        for (var i = 0; i < mgr->NodeListCount; i++)
        {
            var node = nodes[i];
            if (node == null)
                continue;
            if (node->Type == NodeType.Text)
            {
                var t = (AtkTextNode*)node;
                var s = t->NodeText.ToString();
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                if (s.Contains("MGF", StringComparison.OrdinalIgnoreCase) || s.Contains("金碟声誉"))
                    indicatorSeen = true;

                // 同节点内提取纯数字或句子中的数字
                var m = Regex.Match(s, "^\\s*(\\d{1,7})\\s*$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var v))
                    numericFound = Math.Max(numericFound, v);
                else
                {
                    var m2 = Regex.Match(s, "(\\d{1,7})");
                    if (m2.Success && int.TryParse(m2.Groups[1].Value, out var v2))
                        numericFound = Math.Max(numericFound, v2);
                }
            }
            // NodeType.Counter 的读取在不同版本有字段差异，暂不直接读取，保持文本节点解析
        }

        if (indicatorSeen && numericFound >= 0)
        {
            // 非入口面板：若识别到奖励文本，仅记录“最近获得”，不覆盖总数
            _lastMgfGain = numericFound;
            _debugMessages += $"识别到奖励文本，最近获得: {_lastMgfGain} (Addon: {addonName})\n";
        }
        else if (indicatorSeen && numericFound < 0)
        {
            _debugMessages += $"检测到 MGF 关键词，但未找到数字 (Addon: {addonName})\n";
        }
    }

    private unsafe void TryReadMgfFromFgsEnterDialog(AddonSetupArgs setupArgs)
    {
        try
        {
            var values = (AtkValue*)setupArgs.AtkValues;
            // 根据你的调试数据，索引 #1 为 736，即当前货币
            var v = values[1];
            int cand = Math.Max(v.Int, (int)Math.Min(v.UInt, int.MaxValue));
            if (cand >= 0 && cand <= 9999999)
            {
                _mgfTotal = cand;
                if (_mgfTotal != _lastLoggedMgf)
                {
                    _debugMessages += $"FGSEnterDialog 专读到 MGF: {_mgfTotal}\n";
                    _lastLoggedMgf = _mgfTotal;
                }
            }
        }
        catch (Exception e)
        {
            _debugMessages += $"TryReadMgfFromFgsEnterDialog 异常: {e.Message}\n";
        }
    }

    private unsafe void DumpAddonText(AtkUnitBase* addon, string addonName)
    {
        try
        {
            var mgr = &addon->UldManager;
            var nodes = mgr->NodeList;
            int printed = 0;
            for (var i = 0; i < mgr->NodeListCount; i++)
            {
                var node = nodes[i];
                if (node == null)
                    continue;
                if (node->Type == NodeType.Text)
                {
                    var t = (AtkTextNode*)node;
                    var s = t->NodeText.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        _debugMessages += $"[Addon={addonName}] TextNode #{i}: \"{s}\"\n";
                        printed++;
                        if (printed >= 50) // 避免日志过长
                            break;
                    }
                }
                else
                {
                    _debugMessages += $"[Addon={addonName}] Node #{i} Type={node->Type}\n";
                }
            }
            if (printed == 0)
                _debugMessages += $"[Addon={addonName}] 未捕获到任何 TextNode 文本\n";
        }
        catch (Exception e)
        {
            _debugMessages += $"DumpAddonText 异常: {e.Message}\n";
        }
    }

    private unsafe void TryReadMgfFromSetup(AddonSetupArgs setupArgs, string addonName)
    {
        try
        {
            var count = setupArgs.AtkValueCount;
            var values = (AtkValue*)setupArgs.AtkValues;
            int numericFound = -1;
            for (int i = 0; i < count; i++)
            {
                var v = values[i];
                int cand = Math.Max(v.Int, (int)Math.Min(v.UInt, int.MaxValue));
                if (cand >= 0 && cand <= 9999999)
                    numericFound = Math.Max(numericFound, cand);
            }

            if (numericFound >= 0)
            {
                _mgfTotal = numericFound;
                _debugMessages += $"通过 PostSetup 读取到 MGF: {_mgfTotal} (Addon: {addonName}, Values={count})\n";
            }
            else
            {
                _debugMessages += $"PostSetup 未找到数字 (Addon: {addonName}, Values={count})\n";
            }
        }
        catch (Exception e)
        {
            _debugMessages += $"TryReadMgfFromSetup 异常: {e.Message}\n";
        }
    }

    private unsafe void DumpSetupValues(AddonSetupArgs setupArgs, string addonName)
    {
        try
        {
            var count = setupArgs.AtkValueCount;
            var values = (AtkValue*)setupArgs.AtkValues;
            var sb = new StringBuilder();
            sb.Append($"[Addon={addonName}] PostSetup Values (count={count}): ");
            for (int i = 0; i < count; i++)
            {
                var v = values[i];
                sb.Append($"#{i} Int={v.Int} UInt={v.UInt}; ");
                if (i >= 64) break; // 限制日志长度
            }
            _debugMessages += sb.ToString() + "\n";
        }
        catch (Exception e)
        {
            _debugMessages += $"DumpSetupValues 异常: {e.Message}\n";
        }
    }

    private bool IsConfigComplete()
    {
        return !string.IsNullOrWhiteSpace(_webSocketUrl)
            && _webSocketPort != 0
            && !string.IsNullOrWhiteSpace(_qqPrivateChatNumber)
            && !string.IsNullOrWhiteSpace(_qqBotNumber)
            && !string.IsNullOrWhiteSpace(_gameName);
    }

    public unsafe override void PreOpenCheck()
    {
        _automation.Update();
        _drawer.Update();

        _now = DateTime.Now;
        var playerPos = Service.ClientState.LocalPlayer?.Position ?? new();
        _movementDirection = playerPos - _prevPos;
        _prevPos = playerPos;
        _movementSpeed = _movementDirection.Length() / Framework.Instance()->FrameDeltaTime;
        _movementDirection = _movementDirection.NormalizedXZ();

        IsOpen = Service.ClientState.TerritoryType is 1165 or 1197;

        // 进入副本时重置一次本轮的货币读取标记
        var boundByDuty = Service.Condition[ConditionFlag.BoundByDuty];
        if (boundByDuty && !_wasBoundByDuty)
        {
            _enteredDutyAt = _now;
            _lastMgfGain = 0;
            // 不清空总数，只在未知时维持 -1；若后续在入口NPC处检测到则更新
        }
        _wasBoundByDuty = boundByDuty;

        UpdateMap();
        UpdateAutoJoin();
        UpdateAutoLeave();
        DrawOverlays();

        _drawer.DrawWorldPrimitives();

        // 将货币信息显示到窗口标题，保持稳定 ID 以避免 ImGui 频繁重建
        var mgfTextTitle = _mgfTotal >= 0 ? _mgfTotal.ToString() : "未知";
        this.WindowName = $"vfailguy 改  货币(MGF): {mgfTextTitle} 最近获得: {_lastMgfGain}###vfallguy-main";
    }

    public unsafe override void Draw()
    {
        if (ImGui.Button("进本"))
            _automation.RegisterForDuty();
        ImGui.SameLine();
        if (ImGui.Button("退本"))
            _automation.LeaveDuty();
        ImGui.SameLine();
        if (ImGui.Button("查询王之进度"))
        {
            var achievement = Achievement.Instance();
            if (achievement != null)
                achievement->RequestAchievementProgress(3407);
        }
        ImGui.SameLine();
        ImGui.Text($"{Achievement.Instance()->ProgressMax}/100 ");
        ImGui.TextUnformatted($"玩家数量: {_numPlayersInDuty} (自动退本: {(_autoLeaveAt == DateTime.MaxValue ? "无" : $"倒计时: {(_autoLeaveAt - _now).TotalSeconds:f1}s")})");

        ImGui.Checkbox("第三关结束自动退(请勿勾选挂机刷币)", ref _autoWinLeave);
        ImGui.Checkbox("挂机刷币模式(请同时勾选自动排本)", ref _autoFarmingMode);

        ImGui.Checkbox("自动排本(需要在NPC旁边)", ref _autoJoin);
        if (_autoJoin)
        {
            using (ImRaii.PushIndent())
            {
                ImGui.SliderFloat("延迟###j", ref _autoJoinDelay, 0, 10);
            }
        }
        ImGui.Checkbox("如果非单人自动退本", ref _autoLeaveIfNotSolo);
        if (_autoLeaveIfNotSolo)
        {
            using (ImRaii.PushIndent())
            {
                ImGui.SliderFloat("延迟###l", ref _autoLeaveDelay, 0, 10);
                ImGui.SliderInt("人数限制", ref _autoLeaveLimit, 1, 25);
            }
        }
        ImGui.Checkbox("AOE范围", ref _showAOEs);
        ImGui.Checkbox("AOE时间", ref _showAOEText);
        ImGui.Checkbox("推荐路线", ref _showPathfind);

        ImGui.Checkbox("启用QQ机器人通知", ref _enableQQBotConfig);
        if (_enableQQBotConfig)
        {
            ImGui.Text("QQ机器人配置:");
            ImGui.InputText("本账号名称", ref _gameName, 255);
            ImGui.InputText("私聊的QQ号", ref _qqPrivateChatNumber, 255);
            ImGui.InputText("BotQQ号", ref _qqBotNumber, 255);
            ImGui.InputText("WebSocket URL", ref _webSocketUrl, 255);
            ImGui.InputInt("端口号", ref _webSocketPort);
            ImGui.SliderInt("战局人数通知(小于等于这个值都会提醒)", ref _battlePlayerCount, 1, 24, "%d人战局");

            if (ImGui.Button("应用"))
            {
                _config.GameName = _gameName;
                _config.QqPrivateChatNumber = _qqPrivateChatNumber;
                _config.QqBotNumber = _qqBotNumber;
                _config.WebSocketUrl = _webSocketUrl;
                _config.WebSocketPort = _webSocketPort;
                _config.BattlePlayerCount = _battlePlayerCount;
                _config.Save();

                if (IsConfigComplete())
                {
                    SendMessageToQQ("机器人已成功连接\n" +
                                    $"本账号名称: {_gameName}\n" +
                                    $"通知QQ私聊号码: {_qqPrivateChatNumber}\n");
                }
                else
                {
                    _debugMessages += "QQ机器人配置不完整，取消发送消息。\n";
                }
            }
        }

        if (_map != null)
        {
            var strats = _map.Strats();
            if (strats.Length > 0)
                ImGui.TextUnformatted(strats);
            ImGui.TextUnformatted($"Pos: {_map.PlayerPos}");
            ImGui.TextUnformatted($"Path: {_map.PathSkip}-{_map.Path.Count}");
            ImGui.TextUnformatted($"Speed: {_movementSpeed}");

            //foreach (var aoe in _map.AOEs.Where(aoe => aoe.NextActivation != default))
            //{
            //    var nextActivation = (aoe.NextActivation - _now).TotalSeconds;
            //    using (ImRaii.PushColor(ImGuiCol.Text, nextActivation < 0 ? 0xff0000ff : 0xffffffff))
            //        ImGui.TextUnformatted($"{aoe.Type} R{aoe.R1} @ {aoe.Origin}: activate in {nextActivation:f3}, repeat={aoe.Repeat}, seqd={aoe.SeqDelay}");
            //}
        }

        // 货币信息已移至标题，不在底部显示或提供调试采集入口
    }

    private void UpdateMap()
    {
        if (Service.Condition[ConditionFlag.BetweenAreas])
            return;

        Type? mapType = null;
        if (IsOpen)
        {
            if (Service.ClientState.TerritoryType == 1197)
            {
                //mapType = typeof(MapTest);
            }
            else
            {
                var pos = Service.ClientState.LocalPlayer!.Position;
                mapType = pos switch
                {
                    //{ X: >= -20 and <= 20, Z: >= -400 and <= -100 } => typeof(Map1A),
                    { X: >= -40 and <= 40, Z: >= 100 and <= 350 } => typeof(Map3),
                    _ => null
                };
            }
        }

        if (_map?.GetType() != mapType)
        {
            _map?.Dispose();
            _map = null;
            if (mapType != null)
                _map = (Map?)Activator.CreateInstance(mapType, _gameEvents);
        }

        _map?.Update();
    }

    private void PerformAutoFarming()
    {
        _automation.LeaveDuty();
    }

    private void UpdateAutoJoin()
    {
        bool wantAutoJoin = _autoJoin && _automation.Idle && IsOpen && Service.ClientState.TerritoryType == 1197 && !Service.Condition[ConditionFlag.WaitingForDutyFinder] && !Service.Condition[ConditionFlag.BetweenAreas];
        if (!wantAutoJoin)
        {
            _autoJoinAt = DateTime.MaxValue;
        }
        else if (_autoJoinAt == DateTime.MaxValue)
        {
            Service.Log.Debug($"Auto-joining in {_autoJoinDelay:f2}s...");
            _autoJoinAt = _now.AddSeconds(_autoJoinDelay);
        }
        else if (_now >= _autoJoinAt)
        {
            Service.Log.Debug($"Auto-joining");
            _automation.RegisterForDuty();
            _autoJoinAt = DateTime.MaxValue;
        }
    }

    private void UpdateAutoLeave()
    {
        _numPlayersInDuty = Service.ClientState.TerritoryType == 1165 && Service.Condition[ConditionFlag.BoundByDuty] && !Service.Condition[ConditionFlag.BetweenAreas]
            ? Service.ObjectTable.Count(o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player)
            : 0;
        bool wantAutoLeave = _autoLeaveIfNotSolo && _numPlayersInDuty > _autoLeaveLimit && _automation.Idle;
        if (!wantAutoLeave)
        {
            _autoLeaveAt = DateTime.MaxValue;
        }
        else if (_autoLeaveAt == DateTime.MaxValue)
        {
            Service.Log.Debug($"Auto-leaving in {_autoLeaveDelay:f2}s...");
            _autoLeaveAt = _now.AddSeconds(_autoLeaveDelay);
        }
        else if (_now >= _autoLeaveAt)
        {
            Service.Log.Debug($"Auto-leaving: {_numPlayersInDuty} players");
            _automation.LeaveDuty();
            _autoLeaveAt = DateTime.MaxValue;
        }
    }

    private void DrawOverlays()
    {
        if (_map == null || Service.Condition[ConditionFlag.BetweenAreas])
            return;

        if (_showPathfind)
        {
            var from = _map.PlayerPos;
            for (int i = _map.PathSkip; i < _map.Path.Count; ++i)
            {
                var wp = _map.Path[i];
                var delay = (wp.StartMoveAt - _now).TotalSeconds;
                _drawer.DrawWorldLine(from, wp.Dest, i > 0 ? 0xff00ffff : delay <= 0 ? 0xff00ff00 : 0xff0000ff);
                if (delay > 0)
                    _drawer.DrawWorldText(from, 0xff0000ff, $"{delay:f3}");
                from = wp.Dest;
            }
        }

        foreach (var aoe in _map.AOEs.Where(aoe => aoe.NextActivation != default))
        {
            var nextActivation = (aoe.NextActivation - _now).TotalSeconds;
            if (nextActivation < 2.5f)
            {
                var (aoeEnter, aoeExit) = _movementSpeed > 0 ? aoe.Intersect(_map.PlayerPos, _movementDirection) : aoe.Contains(_map.PlayerPos) ? (0, float.PositiveInfinity) : (float.NaN, float.NaN);
                var delay = !float.IsNaN(aoeEnter) ? aoe.ActivatesBetween(_now, aoeEnter * Map.InvSpeed - 0.1f, aoeExit * Map.InvSpeed + 0.1f) : 0;
                var color = delay > 0 ? 0xff0000ff : 0xff00ffff;
                if (_showAOEs)
                {
                    aoe.Draw(_drawer, color);
                }
                if (_showAOEText)
                {
                    var text = $"{nextActivation:f3} [{aoeEnter * Map.InvSpeed:f2}-{aoeExit * Map.InvSpeed:f2}, {delay:f2}]";
                    var dir = (aoe.Origin - _map.PlayerPos).NormalizedXZ();
                    var (enter, exit) = aoe.Intersect(_map.PlayerPos, dir);
                    var textPos = _map.PlayerPos + dir * MathF.Max(enter, 0);
                    _drawer.DrawWorldText(textPos, color, text);
                }
            }
        }
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (Regex.IsMatch(message.TextValue, @"节目马上就要开始了"))
        {
            if (IsConfigComplete() && _enableQQBotConfig && _numPlayersInDuty <= _battlePlayerCount)
            {
                SendMessageToQQ($"本账号名称: {_gameName}\n当前为{_numPlayersInDuty}人对局");
            }
            else
            {
                _debugMessages += "判断异常\n";
            }
        }

        if (Regex.IsMatch(message.TextValue, @"获得了(\d+)个金碟声誉。"))
        {
            var m = Regex.Match(message.TextValue, @"获得了(\d+)个金碟声誉。");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var val))
                _lastMgfGain = val;
            if (_autoFarmingMode && !_autoWinLeave)
                PerformAutoFarming();
            if (_autoWinLeave && _map is Map3)
                PerformAutoFarming();
        }

        // 英文端奖励识别：用于自动退本
        if (Regex.IsMatch(message.TextValue, @"You obtain (\d+) MGF\."))
        {
            var m = Regex.Match(message.TextValue, @"You obtain (\d+) MGF\.");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var val))
                _lastMgfGain = val;
            if (_autoFarmingMode && !_autoWinLeave)
                PerformAutoFarming();
            if (_autoWinLeave && _map is Map3)
                PerformAutoFarming();
        }
    }

    private async void SendMessageToQQ(string message)
    {
        try
        {
            var qqPrivate = long.Parse(_qqPrivateChatNumber);
            var qqBot = long.Parse(_qqBotNumber);
            var requestUrl = $"{_webSocketUrl}:{_webSocketPort}/v1/LuaApiCaller?funcname=MagicCgiCmd&timeout=35&qq={qqBot}";

            var payload = new
            {
                CgiCmd = "MessageSvc.PbSendMsg",
                CgiRequest = new
                {
                    ToUin = qqPrivate,
                    ToType = 1,
                    Content = message
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(requestUrl, content);
            var responseString = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                _debugMessages += "消息发送成功: " + responseString + "\n";
            else
                _debugMessages += $"消息发送失败: 服务器返回 {response.StatusCode}\n";
        }
        catch (Exception e)
        {
            _debugMessages += "消息发送出现异常: " + e.Message + "\n";
        }
    }
}
