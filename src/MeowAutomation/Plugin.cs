using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using cfg;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.LowLevel;
using UnityEngine.UI;

namespace MeowAutomation
{
    [BepInPlugin(Guid, "Meow Automation", "0.4.1")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "echo.meow.automation";

        internal static ManualLogSource L;
        internal static ConfigEntry<bool> AutoFarm;
        internal static ConfigEntry<bool> AutoCan;
        internal static ConfigEntry<bool> AutoMerge;
        internal static ConfigEntry<bool> FreeFruit;
        internal static ConfigEntry<bool> RandomRecord;
        internal static ConfigEntry<bool> AutoRedeem;
        internal static ConfigEntry<int> KeyMultiplier;
        internal static ConfigEntry<int> GrowSpeed;
        internal static ConfigEntry<bool> AutoSteal;
        internal static ConfigEntry<float> CanInterval;
        internal static ConfigEntry<float> MergeInterval;

        private static readonly FieldInfo BusyField =
            typeof(BaseCannedItem).GetField("_isExchanging", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PoolField =
            typeof(BaseCannedItem).GetField("_cachedPoolConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SynthBusyField =
            typeof(SyntheticUI).GetField("_isSynthesizing", BindingFlags.Instance | BindingFlags.NonPublic);

        private bool injected;
        private bool showPanel;
        private float nextCan;
        private float nextMerge;
        private float nextPlant;
        private float nextHarvest;
        private string lastNote;
        private bool inputBroken;
        private bool tickLogged;

        private readonly Dictionary<int, int> lastTokenCounts = new Dictionary<int, int>();
        private readonly Dictionary<int, int> tokenFailStreak = new Dictionary<int, int>();
        private SpriteToggle autoCanToggle;
        private TMP_Text tokenNormalText;
        private TMP_Text tokenEventText;
        private TMP_Text couponText;
        private TMP_Text multiplierText;
        private TMP_Text growText;
        private float nextTokenRefresh;
        private int savedPlayMode = 2;
        private readonly Dictionary<string, float> redeemFailedAt = new Dictionary<string, float>();
        private bool playModeApplied;

        private void Awake()
        {
            L = Logger;
            Instance = this;
            Diag("Awake 开始");

            AutoFarm = Config.Bind("Auto", "AutoFarm", false, "自动收获，并按库存最少的果实补种");
            AutoCan = Config.Bind("Auto", "AutoCan", false, "自动开猫罐头");
            AutoMerge = Config.Bind("Auto", "AutoMerge", false, "自动合成（5 合 1）");
            FreeFruit = Config.Bind("Auto", "FreeFruit", false, "开罐不扣果实（Steam 代币仍然会扣）");
            RandomRecord = Config.Bind("Auto", "RandomRecord", false, "唱片随机播放（切到随机模式，没在放歌就随机起一首）");
            AutoRedeem = Config.Bind("Auto", "AutoRedeem", true, "自动兑换码（扫描兑换码表，未领取的自动兑一遍）");
            KeyMultiplier = Config.Bind("Cheat", "KeyMultiplier", 1, "按键倍率：一次按键记几次（1=原版，10/100/1000 加速成就）");
            GrowSpeed = Config.Bind("Cheat", "GrowSpeed", 1, "生长加速：作物每秒生长量翻几倍（1=原版）");
            AutoSteal = Config.Bind("Auto", "AutoSteal", false, "联机自动偷菜（对成熟且没偷过的邻居下手）");
            CanInterval = Config.Bind("Auto", "CanIntervalSeconds", 1.0f, "两次开罐之间的间隔（秒）");
            MergeInterval = Config.Bind("Auto", "MergeIntervalSeconds", 3.0f, "两次合成之间的间隔（秒）");

            ApplyPatches();

            // 主驱动：插进 Unity 主循环，和场景/组件生命周期完全无关
            try { InstallPlayerLoop(); }
            catch (Exception e) { Diag("PlayerLoop 注入异常: " + e.Message); }

            DumpEnv();

            L.LogInfo("Meow Automation 0.4.1 已加载");
        }

        private float nextTick;

        private void Tick()
        {
            float now = Time.unscaledTime;
            if (now < nextTick) return;
            nextTick = now + 0.5f;

            CheckCommands();

            if (!tickLogged)
            {
                tickLogged = true;
                L.LogInfo("[插件] 巡检开始（0.5 秒一轮）");
                Diag("Tick 首次运行");
            }

            try
            {
                if (!playModeApplied)
                {
                    playModeApplied = true;
                    if (RandomRecord.Value) ApplyShuffle(true);
                }

                if (!injected) TryInjectPigTab();

                if (AutoFarm.Value) DoFarm();
                if (AutoCan.Value) DoCan();
                if (AutoMerge.Value) DoMerge();
                if (AutoSteal.Value) DoSteal();

                if (now >= nextTokenRefresh)
                {
                    nextTokenRefresh = now + 1f;
                    RefreshTokens();
                    RefreshCoupons();
                }

                TickRedeem();
            }
            catch (Exception e)
            {
                L.LogError("Tick 异常: " + e);
            }
        }

        private float nextCmdAt;

        /// <summary>调试通道：读游戏目录下的 meow_cmd.txt，执行完就把文件删掉。</summary>
        private void CheckCommands()
        {
            if (Time.unscaledTime < nextCmdAt) return;
            nextCmdAt = Time.unscaledTime + 0.5f;

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "meow_cmd.txt");
            if (!File.Exists(path)) return;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
                File.Delete(path);
            }
            catch { return; }

            foreach (var raw in lines)
            {
                string cmd = raw.Trim();
                if (cmd.Length == 0) continue;
                try { RunCommand(cmd); }
                catch (Exception e) { Diag("命令失败 " + cmd + ": " + e.Message); }
            }
        }

        private void RunCommand(string cmd)
        {
            Diag("执行命令: " + cmd);

            if (cmd.StartsWith("shot"))
            {
                string name = cmd.Length > 4 ? cmd.Substring(4).Trim() : "shot";
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shots");
                Directory.CreateDirectory(dir);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
                return;
            }

            if (cmd == "open_pig")
            {
                var st = FindSettingUi(out _);
                if (st == null || pigPage == null) { Diag("还没注入，打不开猪头页"); return; }
                st.SwitchSubUI(pigPage);
                Diag("已切到猪头页");
                return;
            }

            if (cmd == "rowinfo")
            {
                var rt = growText != null ? growText.rectTransform : null;
                if (rt == null) { Diag("没有倍率行"); return; }
                var row = rt.parent as RectTransform;
                var canvas = rt.GetComponentInParent<Canvas>();
                var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                var sp = RectTransformUtility.WorldToScreenPoint(cam, row != null ? row.position : rt.position);
                var mp = Input.mousePosition;
                Diag($"鼠标(游戏坐标) {mp.x:F0},{mp.y:F0}");
                Diag($"行中心(游戏坐标) {sp.x:F0},{sp.y:F0} | Screen={Screen.width}x{Screen.height} " +
                     $"| 画布scaleFactor={canvas?.scaleFactor:F2} 模式={canvas?.renderMode}");
                if (row != null)
                {
                    Diag($"行尺寸 {row.rect.width:F0}x{row.rect.height:F0}");
                    Diag("行上组件: " + string.Join(",", row.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()));
                    Diag("子物体: " + string.Join(",", row.GetComponentsInChildren<Component>(true)
                        .Select(c => c.gameObject.name + ":" + c.GetType().Name).Distinct().ToArray()));
                }
                return;
            }

            if (cmd == "state")
            {
                Diag($"状态：按键倍率×{KeyMultiplier.Value} 生长加速×{GrowSpeed.Value} " +
                     $"自动偷菜={AutoSteal.Value} 已注入={injected}");
                return;
            }

            if (cmd == "open_setting")
            {
                MenuUI menu = null;
                try
                {
                    var ui = SingletonMono<UIManager>.Instance;
                    if (ui != null) menu = ui.menuUI;
                }
                catch { }
                if (menu == null)
                    menu = Resources.FindObjectsOfTypeAll<MenuUI>()
                                    .FirstOrDefault(m => m != null && m.settingUI != null);
                if (menu == null) { Diag("没找到 MenuUI"); return; }
                if (!menu.gameObject.activeInHierarchy) menu.Show();
                menu.SwitchUI(menu.settingUI);
                return;
            }
        }

        // ---------------- 自动种植 / 收获 ----------------

        private void DoFarm()
        {
            var pm = PlayerManager.Instance;
            var player = pm != null ? pm.LocalPlayer : null;
            var pot = player != null ? player.flowerPot : null;
            if (pot == null || pot.slots == null || pot.slots.Count == 0) return;

            bool anyReady = pot.slots.Any(s => s != null && s.State == FlowerPotState.ReadyToHarvest);
            if (anyReady && Time.unscaledTime >= nextHarvest)
            {
                nextHarvest = Time.unscaledTime + 2f;
                pot.HarvestFlower();
                nextPlant = Time.unscaledTime + 1.5f;
                L.LogInfo("[农田] 自动收获");
                return;
            }

            if (Time.unscaledTime < nextPlant) return;
            if (!pot.slots.All(s => s == null || s.State == FlowerPotState.Empty)) return;

            var best = PickLeastFruit();
            if (best == null)
            {
                Note("所有果实都已到 99 上限，暂停补种");
                nextPlant = Time.unscaledTime + 5f;
                return;
            }
            MessageManager.Instance().Dispatch(PlantMessage.Obtain(best));
            L.LogInfo($"[农田] 补种 {best.Id}（库存 {DataManager.Instance.GetFruitInventoryCount(best.Id)}）");
            nextPlant = Time.unscaledTime + 2f;
        }

        private tPlantConfig PickLeastFruit()
        {
            var table = ConfigManager.Instance != null ? ConfigManager.Instance.tables : null;
            var list = table != null ? table.TbPlantConfig.DataList : null;
            if (list == null) return null;

            tPlantConfig best = null;
            int bestCount = int.MaxValue;
            foreach (var c in list)
            {
                if (c == null || c.IsEventCrop != 0) continue;
                int n = DataManager.Instance.GetFruitInventoryCount(c.Id);
                if (n >= 99) continue;
                if (n < bestCount || (n == bestCount && best != null && c.Id < best.Id))
                {
                    bestCount = n;
                    best = c;
                }
            }
            return best;
        }

        // ---------------- 联机自动偷菜 ----------------

        private float nextSteal;

        /// <summary>
        /// 偷菜走的是“点击对方菜地上的偷菜按钮 → 发 steal_request 给对面 → 对方校验后返回结果”。
        /// 这里只做一件事：谁的偷菜按钮亮着就替玩家点一下。
        /// </summary>
        private void DoSteal()
        {
            if (Time.unscaledTime < nextSteal) return;
            nextSteal = Time.unscaledTime + 1.5f;

            var lobby = SingletonMono<SteamLobbySystem>.Instance;
            if (lobby == null || !lobby.IsInLobby())
            {
                Note("自动偷菜要先进联机房间才有邻居");
                return;
            }

            int hit = 0;
            foreach (var p in Resources.FindObjectsOfTypeAll<Player>())
            {
                if (p == null || p.isLocal) continue;
                if (p.stealButton == null || !p.stealButton.activeInHierarchy) continue;
                try
                {
                    p.ClickStealBtn();
                    hit++;
                    L.LogInfo($"[偷菜] 对 {p.steamId} 的菜地偷了一次");
                }
                catch (Exception e)
                {
                    L.LogWarning("[偷菜] 失败：" + e.Message);
                }
            }

            if (hit == 0) Note("自动偷菜待机中：要邻居的菜成熟、且这轮没被偷过");
        }

        // ---------------- 自动开罐 ----------------

        private void DoCan()
        {
            if (Time.unscaledTime < nextCan) return;
            nextCan = Time.unscaledTime + Mathf.Max(0.4f, CanInterval.Value);

            foreach (var b in Resources.FindObjectsOfTypeAll<BaseCannedItem>())
            {
                if (b == null || !b.gameObject.scene.IsValid()) continue;
                var btn = GetExchangeButton(b);
                if (btn == null || !btn.interactable) continue;
                if (BusyField != null && (bool)BusyField.GetValue(b)) continue;

                var pool = PoolField != null ? PoolField.GetValue(b) as tPoolConfig : null;
                if (pool == null)
                {
                    Note("猫罐头页还没打开过，先在游戏里点开一次再自动开");
                    continue;
                }

                int tokens = GetTokenCount(pool.TokenId);
                if (tokens == 0)
                {
                    StopAutoCan($"代币 {pool.TokenId} 已用完（剩余 0）");
                    return;
                }
                if (tokens > 0)
                {
                    int streak = 0;
                    if (lastTokenCounts.TryGetValue(pool.TokenId, out int prev))
                    {
                        tokenFailStreak.TryGetValue(pool.TokenId, out streak);
                        streak = tokens >= prev ? streak + 1 : 0;
                    }
                    lastTokenCounts[pool.TokenId] = tokens;
                    tokenFailStreak[pool.TokenId] = streak;
                    if (streak >= 3)
                    {
                        StopAutoCan("连续 3 次兑换代币没有减少，可能被 Steam 限流或页面未就绪");
                        return;
                    }
                }

                b.ExchangeCannedItem();
                L.LogInfo($"[开罐] 触发，代币 {pool.TokenId} 剩余 {tokens}");
                return;
            }
        }

        private static int GetTokenCount(int tokenId)
        {
            var mgr = SteamInventoryManager.Instance;
            if (mgr == null) return -1;
            var all = mgr.GetAllItems();
            if (all == null) return -1;
            return all.TryGetValue(tokenId, out int n) ? n : 0;
        }

        private void StopAutoCan(string reason)
        {
            AutoCan.Value = false;
            if (autoCanToggle != null) autoCanToggle.SetIsOnWithoutNotify(false);
            L.LogWarning("[开罐] 自动开罐已停止：" + reason);
        }

        private static Button GetExchangeButton(BaseCannedItem b)
        {
            if (b is CannedItem c) return c.exchangeButton;
            if (b is SpecialCannedItem s) return s.exchangeButton;
            return null;
        }

        // ---------------- 自动合成 ----------------

        private void DoMerge()
        {
            if (Time.unscaledTime < nextMerge) return;
            nextMerge = Time.unscaledTime + Mathf.Max(1f, MergeInterval.Value);

            var ui = Resources.FindObjectsOfTypeAll<SyntheticUI>().FirstOrDefault(x => x != null && x.gameObject.scene.IsValid());
            if (ui == null || ui.syntheticList == null) return;
            if (SynthBusyField != null && (bool)SynthBusyField.GetValue(ui)) return;

            ui.AutoFill();
            int filled = ui.syntheticList.GetFilledSlotCount();
            if (filled < 5 || ui.syntheticList.currentRarity == -1)
            {
                Note("没有凑够 5 件同品质可合成物品");
                return;
            }

            ui.Synthesize();
            lastNote = null;
            L.LogInfo("[合成] 触发一次 5 合 1");
        }

        // ---------------- “山猪歪瓜” 标签页 ----------------

        private bool diagLogged;

        // 设置页一 Start 就把实例记下来，比场景里捞更靠得住
        internal static SettingUI HookedSetting;

        internal static Plugin Instance;

        private static BaseUI pigPage;

        private static int dataTicks;

        internal static void Diag(string msg)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                File.AppendAllText(Path.Combine(dir, "meow_diag.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch
            {
                // 诊断都写不了就算了
            }
        }

        /// <summary>
        /// 逐个补丁类显式打，每个的成败都落盘。
        /// PatchAll 在这里既不抛异常也不干活，不能再信它。
        /// </summary>
        private static void ApplyPatches()
        {
            var h = new Harmony(Guid);
            var classes = typeof(Patches)
                .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
                .ToArray();

            int ok = 0;
            foreach (var c in classes)
            {
                try
                {
                    var touched = h.CreateClassProcessor(c).Patch();
                    int n = touched != null ? touched.Count : 0;
                    if (n > 0) ok++;
                    Diag("补丁 " + c.Name + " → " + n + " 个方法");
                }
                catch (Exception e)
                {
                    Diag("补丁 " + c.Name + " 失败: " + e.GetType().Name + ": " + e.Message);
                }
            }

            try { Diag("打补丁统计: " + ok + "/" + classes.Length + " 成功，全局面板方法数=" + h.GetPatchedMethods().Count()); }
            catch (Exception e) { Diag("统计失败: " + e.Message); }
        }

        private struct MeowLoopMarker { }

        private static int loopTicks;

        /// <summary>
        /// 直接往 Unity 主循环的 Update 阶段插一个自己的子系统。
        /// 不依赖任何 GameObject/场景/MonoBehaviour 生命周期，主循环在跑就一定会被调。
        /// </summary>
        private static void InstallPlayerLoop()
        {
            PlayerLoopSystem[] subs = PlayerLoop.GetCurrentPlayerLoop().subSystemList;
            if (subs == null) { Diag("PlayerLoop 没有子系统，注入失败"); return; }

            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(UnityEngine.PlayerLoop.Update)) continue;

                var inner = subs[i].subSystemList ?? new PlayerLoopSystem[0];
                foreach (var s in inner)
                {
                    if (s.type == typeof(MeowLoopMarker))
                    {
                        Diag("PlayerLoop 驱动本来就在（重复注入已跳过）");
                        return;
                    }
                }

                var list = new List<PlayerLoopSystem>(inner);
                list.Add(new PlayerLoopSystem
                {
                    type = typeof(MeowLoopMarker),
                    updateDelegate = OnLoopTick
                });
                subs[i].subSystemList = list.ToArray();

                var root = PlayerLoop.GetCurrentPlayerLoop();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                Diag("PlayerLoop 驱动已注入（Update 阶段）");
                return;
            }

            Diag("没找到 Update 阶段，PlayerLoop 注入失败");
        }

        private static void OnLoopTick()
        {
            try
            {
                loopTicks++;
                if (loopTicks <= 2 || loopTicks % 6000 == 0)
                    Diag("PlayerLoop 心跳 #" + loopTicks + " frame=" + Time.frameCount);

                Pump();

                // 点击判定要逐帧做，不能等 0.5 秒一轮的巡检
                if (!ReferenceEquals(Instance, null)) Instance.CheckRowClicks();
            }
            catch (Exception e)
            {
                Diag("PlayerLoop 异常: " + e.Message);
            }
        }

        private static int pumpCount;

        /// <summary>
        /// Unity 重载了 ==，被销毁的组件会“假等于 null”。
        /// 插件宿主对象在首个场景加载时会被销毁，于是所有驱动都卡在 Instance != null 上。
        /// 这里用纯托管判空，被销毁也照样能驱动（字段都还在）。
        /// </summary>
        private static void Pump()
        {
            var p = Instance;
            if (ReferenceEquals(p, null)) return;

            if (pumpCount++ == 0)
                Diag("Tick 分派开始（Unity 判空=" + (Instance == null) + "，托管控件判空=False）");

            try { p.Tick(); }
            catch (Exception e) { Diag("Tick 严重异常: " + e); }
        }

        internal static int steamTicks;

        internal static void DriveFromSteamManager()
        {
            try
            {
                steamTicks++;
                if (steamTicks == 1) Diag("SteamManager.Update 补丁命中");
                Pump();
            }
            catch (Exception e)
            {
                Diag("DriveFromSteamManager 异常: " + e.Message);
            }
        }

        /// <summary>把“我在哪、补丁在不在”一次性写进日志，省得下次又要猜。</summary>
        private void DumpEnv()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("--- 环境快照 ---");
                sb.AppendLine("PID=" + System.Diagnostics.Process.GetCurrentProcess().Id);
                sb.AppendLine("插件宿主 Unity 判空(this==null)=" + (this == null) + "  托管控件判空=" + ReferenceEquals(this, null));
                sb.AppendLine("frame=" + Time.frameCount + " realtime=" + Time.realtimeSinceStartup.ToString("F2") +
                              " isPlaying=" + Application.isPlaying);
                sb.AppendLine("thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                              " name=" + (System.Threading.Thread.CurrentThread.Name ?? "(无名)"));
                sb.AppendLine("syncCtx=" + (System.Threading.SynchronizationContext.Current != null
                    ? System.Threading.SynchronizationContext.Current.GetType().FullName : "null"));
                sb.AppendLine("activeScene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);

                int copies = 0;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "Assembly-CSharp") continue;
                    copies++;
                    sb.AppendLine("已加载 Assembly-CSharp: " + a.Location);
                }
                sb.AppendLine("Assembly-CSharp 份数: " + copies);
                sb.AppendLine("插件眼里的 DataManager 来自: " + typeof(DataManager).Assembly.Location);

                sb.AppendLine("DataManager.Update 补丁: " + PatchDesc(typeof(DataManager), "Update"));
                sb.AppendLine("SettingUI.Start 补丁: " + PatchDesc(typeof(SettingUI), "Start"));
                sb.AppendLine("BaseUI.Show 补丁: " + PatchDesc(typeof(BaseUI), "Show"));
                sb.AppendLine("SteamManager.Update 补丁: " + PatchDesc(typeof(SteamManager), "Update"));
                sb.AppendLine("--- 快照结束 ---");
                Diag(sb.ToString());
            }
            catch (Exception e)
            {
                Diag("DumpEnv 失败: " + e.Message);
            }
        }

        private static string PatchDesc(Type t, string method)
        {
            try
            {
                var mi = t.GetMethod(method,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (mi == null) return "方法不存在";
                var info = Harmony.GetPatchInfo(mi);
                if (info == null) return "未打补丁";
                return $"prefix={info.Prefixes.Count} postfix={info.Postfixes.Count}";
            }
            catch (Exception e)
            {
                return "查询失败: " + e.Message;
            }
        }

        internal static void DriveFromGame()
        {
            try
            {
                dataTicks++;
                if (dataTicks == 1 || dataTicks % 600 == 0) Diag("DataManager.Update #" + dataTicks);
                Pump();
            }
            catch (Exception e)
            {
                Diag("DriveFromGame 异常: " + e.Message);
            }
        }

        internal static void OnSettingUiReady(SettingUI ui)
        {
            if (ui == null) return;
            HookedSetting = ui;
            Diag("设置页出现（钩子命中）");
            if (L != null) L.LogInfo("[UI] 抓到设置页实例");

            // 不依赖 Update：设置页一出来就直接插标签
            try
            {
                var p = Instance;
                if (!ReferenceEquals(p, null) && !p.injected) p.TryInjectPigTab();
            }
            catch (Exception e)
            {
                Diag("钩子注入异常: " + e);
                if (L != null) L.LogError("[UI] 钩子里注入失败: " + e);
            }
        }

        private SettingUI FindSettingUi(out string how)
        {
            how = "";
            if (HookedSetting != null)
            {
                how = "Start 钩子";
                return HookedSetting;
            }

            var all = Resources.FindObjectsOfTypeAll<SettingUI>();

            if (!diagLogged)
            {
                diagLogged = true;
                L.LogInfo($"[UI] SettingUI 候选 {all.Length} 个");
                foreach (var s in all)
                {
                    if (s == null) { L.LogInfo("[UI]   空引用"); continue; }
                    L.LogInfo($"[UI]   {s.name} scene={s.gameObject.scene.name} " +
                              $"valid={s.gameObject.scene.IsValid()} active={s.gameObject.activeInHierarchy} " +
                              $"thank={s.thankSubUI != null} setup={s.setupSubUI != null}");
                }
                L.LogInfo($"[UI] ThankSubUI 候选 {Resources.FindObjectsOfTypeAll<ThankSubUI>().Length} 个");
                L.LogInfo($"[UI] SetupSubUI 候选 {Resources.FindObjectsOfTypeAll<SetupSubUI>().Length} 个");
                L.LogInfo($"[UI] RectTransform 候选 {Resources.FindObjectsOfTypeAll<RectTransform>().Length} 个");
            }

            var direct = all.Where(x => x != null && x.thankSubUI != null)
                            .OrderByDescending(x => x.gameObject.activeInHierarchy)
                            .FirstOrDefault();
            if (direct != null) { how = "SettingUI"; return direct; }

            var loose = all.FirstOrDefault(x => x != null);
            if (loose != null) { how = "SettingUI(无 thankSubUI)"; return loose; }

            // 兜底：从子页面往上找
            var sub = Resources.FindObjectsOfTypeAll<ThankSubUI>().FirstOrDefault(x => x != null)
                      ?? (BaseUI)Resources.FindObjectsOfTypeAll<SetupSubUI>().FirstOrDefault(x => x != null);
            if (sub != null)
            {
                var owner = sub.transform.parent != null
                    ? sub.transform.parent.GetComponent<SettingUI>()
                    : null;
                if (owner != null) { how = "子页面上溯"; return owner; }
            }
            return null;
        }

        private void TryInjectPigTab()
        {
            var setting = FindSettingUi(out string how);

            Transform btnBar = null;
            Transform thankBtn = null;
            if (setting != null)
            {
                btnBar = setting.transform.Find("SettingBtns");
                thankBtn = btnBar != null ? btnBar.Find("ThankBtn") : null;
            }

            // 兜底：直接按名字在场景里找标签栏
            if (btnBar == null || thankBtn == null)
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null || t.name != "SettingBtns") continue;
                    btnBar = t;
                    thankBtn = t.Find("ThankBtn");
                    if (setting == null) setting = t.GetComponentInParent<SettingUI>();
                    break;
                }
                if (btnBar != null) how = (how.Length > 0 ? how + "+" : "") + "名字扫描";
            }

            if (setting == null || btnBar == null || thankBtn == null)
            {
                Note($"还没找到设置页（SettingUI={(setting != null)} 标签栏={(btnBar != null)} 问号={(thankBtn != null)}）");
                return;
            }
            L.LogInfo("[UI] 定位到设置页：" + how);

            var pageGO = BuildPage(setting, thankBtn);
            if (pageGO == null) return;
            BuildTab(setting, btnBar, thankBtn, pageGO);
            pigPage = pageGO.GetComponent<BaseUI>();

            injected = true;
            L.LogInfo("[UI] 已注入“山猪歪瓜专用”标签页");
        }

        private GameObject BuildPage(SettingUI setting, Transform thankBtn)
        {
            var template = (BaseUI)setting.thankSubUI ?? setting.setupSubUI;
            if (template == null)
            {
                L.LogWarning("[UI] 没有可克隆的子页面模板");
                return null;
            }

            var pageGO = Instantiate(template.gameObject, template.transform.parent);
            pageGO.name = "PigSubUI";
            pageGO.SetActive(false);
            StripLocalization(pageGO);

            // 文字样式借游戏自己的行标签，不然我们加的字比游戏的字细一圈
            CaptureLabelStyle(setting);
            cycleRows.Clear();   // 重建页面时别把旧的点击行留着，否则一次点击会切两下

            // 克隆的是设置页时，得先关掉它自己的 Start，否则它会去点已经不存在的开关
            var setupClone = pageGO.GetComponent<SetupSubUI>();
            if (setupClone != null) setupClone.enabled = false;

            var scrollT = pageGO.transform.Find("Scroll View");
            var viewport = scrollT != null ? scrollT.Find("Viewport") : null;
            var oldContent = viewport != null ? viewport.Find("Content") : null;
            if (viewport == null)
            {
                L.LogWarning("[UI] 页面里找不到 Viewport");
                return pageGO;
            }

            // 原页面的 Content 挂着游戏自己的布局组件，直接往里塞东西位置不可控；
            // 这里另建一个 Content，用自己配置的纵向布局，再把 ScrollRect 指过来。
            var titleTemplate = oldContent != null ? oldContent.Find("ThankName") : null;

            var contentGO = new GameObject("PigContent", typeof(RectTransform));
            var content = contentGO.transform;
            content.SetParent(viewport, false);
            var crt = contentGO.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = Vector2.zero;

            var vlg = contentGO.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 22f;
            vlg.padding = new RectOffset(0, 0, 20, 40);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;

            var fitter = contentGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = scrollT.GetComponent<ScrollRect>();
            if (scroll != null)
            {
                scroll.content = crt;
                scroll.verticalNormalizedPosition = 1f;
            }

            MakeAuthorLine(content, titleTemplate);

            // 克隆来的“鸣谢”页原本有一大块名单，不藏起来会和我们的内容叠在一起
            if (oldContent != null) oldContent.gameObject.SetActive(false);

            var setup = setting.setupSubUI;
            var rowTemplate = setup != null && setup.autoharvestBtn != null
                ? setup.autoharvestBtn.transform.parent
                : null;
            if (rowTemplate != null)
            {
                MakeRow(rowTemplate, content, "自动种植", AutoFarm, "MEOW_AUTO_FARM", null,
                        "按库存最少的果实补种，收完自动再种");
                autoCanToggle = MakeRow(rowTemplate, content, "自动开罐", AutoCan, "MEOW_AUTO_CAN", null,
                        "按间隔自动开罐头，要花 Steam 代币");
                MakeRow(rowTemplate, content, "自动合成", AutoMerge, "MEOW_AUTO_MERGE", null,
                        "5 合 1，把多余的东西自动合掉");
                MakeRow(rowTemplate, content, "开罐免果实", FreeFruit, "MEOW_AUTO_FREEFRUIT", null,
                        "开罐不扣本地果实，Steam 代币照样扣");
                growText = MakeCycleRow(rowTemplate, content, "MEOW_GROW_SPEED", GrowLabel, CycleGrow,
                        "点这一行切换：×1 / ×10 / ×100（只加速每秒自然生长，点作物还是原速）");
                multiplierText = MakeCycleRow(rowTemplate, content, "MEOW_KEY_MULT", MultiplierLabel, CycleMultiplier,
                        "点这一行切换：×1 / ×10 / ×100，用来冲按键成就");
                MakeRow(rowTemplate, content, "自动偷菜", AutoSteal, "MEOW_AUTO_STEAL", null,
                        "联机时自动偷邻居成熟的菜");
                MakeRow(rowTemplate, content, "随机播放唱片", RandomRecord, "MEOW_RANDOM_RECORD", ApplyShuffle,
                        "切到随机模式，没在放歌就随一首");
                MakeRow(rowTemplate, content, "自动兑换码", AutoRedeem, "MEOW_AUTO_REDEEM", v =>
                {
                    RefreshCoupons();
                }, "自动把没领过的兑换码兑一遍");
            }
            else
            {
                L.LogWarning("[UI] 找不到设置行的模板");
            }

            tokenNormalText = MakeTextLine(content, titleTemplate, "普通代币：--", 1f);
            tokenEventText = MakeTextLine(content, titleTemplate, "活动代币：--", 1f);
            MakeTextLine(content, titleTemplate, "挂机每分钟 +1", 1f);

            MakeTextLine(content, titleTemplate, "兑换码", 1f);
            couponText = MakeTextLine(content, titleTemplate, "读取中…", 1f);

            // 等所有克隆都做完再拆旧 Content，避免克隆源被提前销毁
            if (oldContent != null)
            {
                oldContent.gameObject.SetActive(false);
                Destroy(oldContent.gameObject);
            }

            return pageGO;
        }

        private static TMP_FontAsset FindFontWith(char c)
        {
            // 提示栏那套字体最全（能正常显示「の」），优先用它
            try
            {
                var ui = SingletonMono<UIManager>.Instance;
                var tips = ui != null && ui.tooltipsUI != null ? ui.tooltipsUI.tooltipsText : null;
                if (tips != null && tips.font != null && tips.font.HasCharacter(c)) return tips.font;
            }
            catch { }

            foreach (var t in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (t == null || t.font == null) continue;
                if (t.font.HasCharacter(c)) return t.font;
            }
            return null;
        }

        private static TMP_FontAsset labelFont;
        private static FontStyles labelStyle = FontStyles.Normal;
        private static Material labelMat;
        private static float labelSize;
        private static bool labelAutoSize;

        /// <summary>游戏自己行标签的字号。我们的字一直显得小，就是因为没跟着它走。</summary>
        private static float BaseSize()
        {
            if (labelSize > 1f && !labelAutoSize) return labelSize;
            if (labelSize > 1f) return labelSize;       // 自动缩放的字号也先当基准
            return 62f;                                  // 没抄到时的兜底
        }

        /// <summary>把游戏行标签的字体/字重/材质抄下来，我们加的文本才有同样的分量。</summary>
        private void CaptureLabelStyle(SettingUI setting)
        {
            try
            {
                var setup = setting.setupSubUI;
                var row = setup != null && setup.autoharvestBtn != null
                    ? setup.autoharvestBtn.transform.parent
                    : null;
                var t = row != null
                    ? row.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault()
                    : null;
                if (t == null) { L.LogWarning("[UI] 没抄到行标签字体"); return; }

                labelFont = t.font;
                labelStyle = t.fontStyle;
                labelMat = t.fontSharedMaterial;
                labelSize = t.fontSize > 1f ? t.fontSize : 0f;
                labelAutoSize = t.enableAutoSizing;
                L.LogInfo($"[UI] 抄到游戏字体 {labelFont?.name} 字重={labelStyle} 字号={labelSize} " +
                          $"自动缩放={labelAutoSize} 材质={(labelMat != null)}");
            }
            catch (Exception e)
            {
                L.LogWarning("[UI] 抄字体失败：" + e.Message);
            }
        }

        private TMP_Text MakeTextLine(Transform content, Transform template, string text, float scale = 1f)
        {
            if (template == null) return null;
            var go = Instantiate(template.gameObject, content);
            go.name = "PigLine";
            go.SetActive(true);
            SetTooltip(go, null);
            var rt = go.GetComponent<RectTransform>();
            float size = BaseSize() * scale;
            if (rt != null) rt.sizeDelta = new Vector2(540f, size + 26f);
            var t = go.GetComponent<TMP_Text>();
            if (t != null)
            {
                if (labelFont != null) t.font = labelFont;
                if (labelMat != null) t.fontSharedMaterial = labelMat;
                t.fontStyle = labelStyle != FontStyles.Normal ? labelStyle : FontStyles.Bold;

                // 字号不写死：上限跟游戏自己的行标签一样大，只有真放不下时才自动缩一点。
                // 之前固定 44/50 会比游戏的字整整小一圈，就是因为没跟它的字号走。
                t.enableAutoSizing = true;
                t.fontSizeMax = Mathf.Max(18f, BaseSize() * scale);
                t.fontSizeMin = Mathf.Max(12f, BaseSize() * scale * 0.55f);
                t.enableWordWrapping = false;
                t.overflowMode = TextOverflowModes.Overflow;

                t.text = text;
                t.fontSize = t.fontSizeMax;
                t.alignment = TextAlignmentOptions.Center;
                t.color = new Color32(74, 47, 34, 255);
                t.ForceMeshUpdate();
            }
            return t;
        }

        private void MakeAuthorLine(Transform content, Transform template)
        {
            const char no = 'の';
            string author = "by 神の麒麟";

            var t = MakeTextLine(content, template, author, 1f);
            if (t == null) return;
            t.color = new Color32(120, 70, 88, 255);

            // 统一字体多半没有「の」，去场景里找一个有的（连字体回退表一起找），别让署名缺字
            if (t.font == null || !t.font.HasCharacter(no, true, false))
            {
                var better = FindFontWith(no);
                if (better != null)
                {
                    t.font = better;
                    L.LogInfo("[UI] 署名改用字体 " + better.name);
                }
                else
                {
                    L.LogWarning("[UI] 全场景都找不到带「の」的字体，署名会缺字");
                }
            }
            t.ForceMeshUpdate();
        }

        private void BuildTab(SettingUI setting, Transform btnBar, Transform thankBtn, GameObject pageGO)
        {
            var pigBtn = Instantiate(thankBtn.gameObject, btnBar);
            pigBtn.name = "PigBtn";
            pigBtn.SetActive(true);
            StripLocalization(pigBtn);
            SetTooltip(pigBtn, "by 神の麒麟");

            var thankRT = thankBtn.GetComponent<RectTransform>();
            var rt = pigBtn.GetComponent<RectTransform>();
            if (thankRT != null && rt != null)
                rt.anchoredPosition = thankRT.anchoredPosition + new Vector2(155f, 0f);

            var iconT = pigBtn.transform.Find("Image");
            var icon = iconT != null ? iconT.GetComponent<Image>() : null;
            var sprite = LoadPigSprite();
            if (icon != null && sprite != null)
            {
                icon.sprite = sprite;
                icon.preserveAspect = true;
            }

            var toggle = pigBtn.GetComponent<Toggle>();
            if (toggle == null)
            {
                L.LogWarning("[UI] 新标签按钮上没有 Toggle");
                return;
            }
            int n = toggle.onValueChanged.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
                toggle.onValueChanged.SetPersistentListenerState(i, UnityEventCallState.Off);

            toggle.SetIsOnWithoutNotify(false);
            toggle.onValueChanged.AddListener(new UnityAction<bool>(on =>
            {
                if (on)
                {
                    setting.SwitchSubUI(pageGO.GetComponent<BaseUI>());
                    RefreshTokens();
                }
            }));

            var barRT = btnBar.GetComponent<RectTransform>();
            if (barRT != null) barRT.sizeDelta = barRT.sizeDelta + new Vector2(155f, 0f);
        }

        private Sprite LoadPigSprite()
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var path = Path.Combine(dir, "pig_icon.png");
                if (!File.Exists(path))
                {
                    L.LogWarning("[UI] 缺少 pig_icon.png，标签沿用问号图标");
                    return null;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.LoadImage(File.ReadAllBytes(path));
                tex.filterMode = FilterMode.Bilinear;
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception e)
            {
                L.LogWarning("[UI] 加载猪头图标失败: " + e.Message);
                return null;
            }
        }

        private void RefreshTokens()
        {
            if (tokenNormalText == null && tokenEventText == null) return;
            int normal = GetTokenCount(100002);
            int eventCan = GetTokenCount(100003);
            if (tokenNormalText != null)
                tokenNormalText.text = $"普通代币：{(normal < 0 ? "--" : normal.ToString("N0"))}";
            if (tokenEventText != null)
                tokenEventText.text = $"活动代币：{(eventCan < 0 ? "--" : eventCan.ToString("N0"))}";
        }

        private void RefreshCoupons()
        {
            if (couponText == null) return;
            try
            {
                var tables = SingletonMono<ConfigManager>.Instance.tables;
                var list = tables.TbCouponCode != null ? tables.TbCouponCode.DataList : null;
                if (list == null || list.Count == 0) return;

                var mgr = SteamInventoryManager.Instance;
                int ownedCount = 0;
                foreach (var c in list)
                {
                    if (mgr != null && mgr.GetItemCount(c.Id) > 0) ownedCount++;
                }

                // 不再逐个列码，只报进度；剩下的多半是活动结束的失效码
                string text = $"已领 {ownedCount}/{list.Count}";
                int missing = list.Count - ownedCount;
                if (missing > 0)
                {
                    float t0 = Time.unscaledTime;
                    int cooling = 0;
                    foreach (var c in list)
                    {
                        if (mgr != null && mgr.GetItemCount(c.Id) > 0) continue;
                        if (redeemFailedAt.TryGetValue(c.Key, out float tf) && t0 - tf < 1800f) cooling++;
                    }
                    // 拆成两行，字才能保持一样大，不会被挤小
                    if (cooling >= missing) text += "\n其余已失效";
                    else text += AutoRedeem.Value ? "\n正在兑换…" : "\n还有没领的";
                }
                if (couponText.text != text) couponText.text = text;            }
            catch (Exception e)
            {
                L.LogWarning("[兑换码] 读取失败：" + e.Message);
            }
        }

        /// <summary>
        /// 克隆来的界面带着游戏的本地化组件，它会在 Start 里把文字覆盖回模板内容，
        /// 于是每一行都变回“自动收获”。这里先把它们摘掉。
        /// </summary>
        private static void StripLocalization(GameObject go)
        {
            foreach (var lt in go.GetComponentsInChildren<LocalizationText>(true))
            {
                if (lt == null) continue;
                lt.enabled = false;
                Destroy(lt);
            }
        }

        /// <summary>
        /// 克隆来的控件带着原件的悬停提示（猪头显示“感谢名单”、倍率行显示“自动收获”的说明）。
        /// ButtonTooltips 会在 Start 里按本地化 key 再覆盖一次，所以先摘掉它，再写我们自己的提示。
        /// </summary>
        private static void SetTooltip(GameObject go, string text)
        {
            foreach (var bt in go.GetComponentsInChildren<ButtonTooltips>(true))
            {
                if (bt == null) continue;
                bt.enabled = false;
                Destroy(bt);
            }

            var tips = go.GetComponentsInChildren<Tooltips>(true);
            if (tips.Length == 0) return;

            if (string.IsNullOrEmpty(text))
            {
                foreach (var t in tips) Destroy(t);
                return;
            }

            tips[0].tooltipsText = text;
            for (int i = 1; i < tips.Length; i++) Destroy(tips[i]);
        }

        private SpriteToggle MakeRow(Transform template, Transform parent, string label, ConfigEntry<bool> cfg,
                                     string name, Action<bool> onChanged = null, string tooltip = null)
        {
            var go = Instantiate(template.gameObject, parent);
            go.name = name;
            go.SetActive(true);
            StripLocalization(go);
            SetTooltip(go, string.IsNullOrEmpty(tooltip) ? label : tooltip);

            // 关掉的行会被游戏压暗，这里统一拉回来
            var cg = go.GetComponent<CanvasGroup>();
            if (cg != null) cg.alpha = 1f;

            var tmp = go.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
            if (tmp != null)
            {
                if (labelFont != null) tmp.font = labelFont;
                if (labelMat != null) tmp.fontSharedMaterial = labelMat;
                tmp.color = new Color32(74, 47, 34, 255);
                tmp.text = label;
                tmp.ForceMeshUpdate();
            }

            var st = go.GetComponentInChildren<SpriteToggle>(true);
            if (st == null)
            {
                L.LogWarning("[UI] 克隆行里没有 SpriteToggle: " + label);
                return null;
            }

            int n = st.onValueChanged.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
                st.onValueChanged.SetPersistentListenerState(i, UnityEventCallState.Off);

            st.SetIsOnWithoutNotify(cfg.Value);
            st.onValueChanged.AddListener(new UnityAction<bool>(v =>
            {
                cfg.Value = v;
                L.LogInfo($"[UI] {label} = {v}");
                if (onChanged != null)
                {
                    try { onChanged(v); }
                    catch (Exception e) { L.LogWarning("[UI] " + label + " 回调失败：" + e.Message); }
                }
            }));
            return st;
        }

        private static readonly int[] Multipliers = { 1, 10, 100 };

        private string MultiplierLabel()
        {
            return "按键倍率 ×" + KeyMultiplier.Value;
        }

        private static readonly int[] GrowSpeeds = { 1, 10, 100 };
        private string GrowLabel()
        {
            return "生长加速 ×" + GrowSpeed.Value;
        }

        private void CycleMultiplier()
        {
            int idx = Array.IndexOf(Multipliers, KeyMultiplier.Value);
            int next = Multipliers[(idx + 1) % Multipliers.Length];
            KeyMultiplier.Value = next;
            Diag("点击命中：按键倍率 → ×" + next);
            L.LogInfo("[倍率] 按键倍率 = " + next);
            if (multiplierText != null) multiplierText.text = MultiplierLabel();
        }

        private void CycleGrow()
        {
            int idx = Array.IndexOf(GrowSpeeds, GrowSpeed.Value);
            int next = GrowSpeeds[(idx + 1) % GrowSpeeds.Length];
            GrowSpeed.Value = next;
            Diag("点击命中：生长加速 → ×" + next);
            L.LogInfo("[加速] 生长加速 = ×" + next);
            if (growText != null) growText.text = GrowLabel();
        }

        private TMP_Text MakeCycleRow(Transform template, Transform parent, string name,
                                      Func<string> label, UnityAction onClick, string tooltip = null)
        {
            var go = Instantiate(template.gameObject, parent);
            go.name = name;
            go.SetActive(true);
            StripLocalization(go);
            SetTooltip(go, string.IsNullOrEmpty(tooltip) ? label() : tooltip);

            var st = go.GetComponentInChildren<SpriteToggle>(true);
            if (st != null) Destroy(st.gameObject);

            var tmp = go.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
            if (tmp != null)
            {
                if (labelFont != null) tmp.font = labelFont;
                if (labelMat != null) tmp.fontSharedMaterial = labelMat;
                tmp.fontStyle = labelStyle != FontStyles.Normal ? labelStyle : FontStyles.Bold;
                tmp.color = new Color32(74, 47, 34, 255);
                tmp.raycastTarget = false;
                tmp.text = label();

                // 同样：上限跟游戏行标签同字号，只有放不下才缩
                tmp.enableAutoSizing = true;
                tmp.fontSizeMax = Mathf.Max(18f, BaseSize());
                tmp.fontSizeMin = Mathf.Max(12f, BaseSize() * 0.6f);
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.fontSize = tmp.fontSizeMax;

                // 标签原来只占左半边（右边留给开关），现在没开关了，让它整行居中
                var lrt = tmp.rectTransform;
                if (lrt != null)
                {
                    lrt.anchorMin = Vector2.zero;
                    lrt.anchorMax = Vector2.one;
                    lrt.offsetMin = new Vector2(8f, 0f);
                    lrt.offsetMax = new Vector2(-8f, 0f);
                }
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.ForceMeshUpdate();
            }

            // 点击不靠 Unity 的射线了（会被上层控件吃掉），改成每帧自己算鼠标在不在这一行里
            var rrt = go.GetComponent<RectTransform>();
            if (rrt != null) cycleRows.Add(new CycleRow { rt = rrt, act = onClick });
            return tmp;
        }

        private class CycleRow
        {
            public RectTransform rt;
            public UnityAction act;
        }

        private readonly List<CycleRow> cycleRows = new List<CycleRow>();
        private float lastRowClick = -10f;

        private static bool HitRow(RectTransform rt, Vector2 screen)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) return false;
            var canvas = rt.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(rt, screen, cam);
        }

        /// <summary>每帧调用：鼠标按下时自己判断落点，避开 EventSystem 那一堆遮挡问题。</summary>
        private void CheckRowClicks()
        {
            if (!injected || cycleRows.Count == 0) return;
            if (!Input.GetMouseButtonDown(0)) return;
            if (Time.unscaledTime - lastRowClick < 0.2f) return;

            Vector2 mp = Input.mousePosition;
            foreach (var row in cycleRows)
            {
                if (row.rt == null || !HitRow(row.rt, mp)) continue;
                lastRowClick = Time.unscaledTime;
                try { row.act(); }
                catch (Exception e) { Diag("切换异常: " + e.Message); }
                return;
            }
        }

        private void Note(string msg)
        {
            if (lastNote == msg) return;
            lastNote = msg;
            L.LogInfo("[提示] " + msg);
        }

        // ---------------- 兑换码 ----------------

        private List<tCouponCode> redeemQueue;
        private int redeemIndex;
        private tCouponCode redeemPending;
        private int redeemOk;
        private float nextRedeemAt;
        private float nextRedeemPassAt;

        private void TickRedeem()
        {
            if (!AutoRedeem.Value)
            {
                redeemQueue = null;
                redeemPending = null;
                return;
            }

            float now = Time.unscaledTime;
            var mgr = SteamInventoryManager.Instance;
            if (mgr == null) return;

            // 先看上一次请求有没有到账
            if (redeemPending != null)
            {
                if (mgr.GetItemCount(redeemPending.Id) > 0)
                {
                    redeemFailedAt.Remove(redeemPending.Key);
                    L.LogInfo($"[兑换码] ✓ {redeemPending.Key} 兑换成功");
                    redeemOk++;
                }
                else
                {
                    redeemFailedAt[redeemPending.Key] = now;
                    L.LogInfo($"[兑换码] {redeemPending.Key} 没换成（大概活动已结束），30 分钟内不再试");
                }
                redeemPending = null;
                RefreshCoupons();
            }

            if (now < nextRedeemAt) return;

            if (redeemQueue == null)
            {
                if (now < nextRedeemPassAt) return;
                nextRedeemPassAt = now + 60f;

                var list = CouponList();
                if (list == null || list.Count == 0 || mgr.GetAllItems() == null) return;

                redeemQueue = new List<tCouponCode>();
                // 表按下发顺序排，新的在后面，从后往前试
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var c = list[i];
                    if (mgr.GetItemCount(c.Id) > 0) continue;
                    if (redeemFailedAt.TryGetValue(c.Key, out float t) && now - t < 1800f) continue;
                    redeemQueue.Add(c);
                }
                redeemIndex = 0;
                if (redeemQueue.Count == 0) { redeemQueue = null; return; }
                L.LogInfo($"[兑换码] 这轮要试 {redeemQueue.Count} 个");
            }

            if (redeemIndex >= redeemQueue.Count)
            {
                redeemQueue = null;
                // 整批只提示一次，免得一串弹窗糊在屏幕上挡着玩
                if (redeemOk > 0)
                {
                    RewardToast($"这轮自动兑换到 {redeemOk} 个码，奖励已经进背包啦");
                    redeemOk = 0;
                }
                return;
            }

            var cur = redeemQueue[redeemIndex++];
            nextRedeemAt = now + 2.5f;
            try
            {
                mgr.AddPromoItem(cur.Id);
                redeemPending = cur;
            }
            catch (Exception e)
            {
                redeemFailedAt[cur.Key] = now;
                L.LogInfo($"[兑换码] {cur.Key} 请求失败：{e.Message}");
            }
        }

        private static List<tCouponCode> CouponList()
        {
            var tables = SingletonMono<ConfigManager>.Instance?.tables;
            return tables != null && tables.TbCouponCode != null ? tables.TbCouponCode.DataList : null;
        }

        private static void RewardToast(string msg)
        {
            try
            {
                SingletonMono<DialogManager>.Instance.ShowDialog<BaseDialog>(d => d.ShowDialog("山猪歪瓜", msg));
            }
            catch
            {
                // 弹窗失败不影响兑换
            }
        }


        // ---------------- 唱片随机播放 ----------------

        private void ApplyShuffle(bool on)
        {
            try
            {
                var dm = SingletonMono<DataManager>.Instance;
                if (dm == null || dm.settingsData == null) return;

                if (on)
                {
                    if (dm.settingsData.playMode != (int)PlayMode.Shuffle)
                    {
                        savedPlayMode = dm.settingsData.playMode;
                        dm.settingsData.playMode = (int)PlayMode.Shuffle;
                        dm.MarkSettingsDirty();
                    }
                }
                else if (dm.settingsData.playMode == (int)PlayMode.Shuffle)
                {
                    dm.settingsData.playMode = savedPlayMode;
                    dm.MarkSettingsDirty();
                }

                MessageManager.Instance().Dispatch(
                    PlayModeChangedMessage.Create((PlayMode)dm.settingsData.playMode));

                if (!on) return;

                var player = FindObjectOfType<MusicPlayer>();
                if (player != null && player.currentMusicId != -1) return;

                var owned = dm.currentData != null ? dm.currentData.ownedMusicIds : null;
                if (owned == null || owned.Count == 0) return;
                var pool = new List<int>();
                foreach (var m in SingletonMono<ConfigManager>.Instance.tables.TbMusicConfig.DataList)
                    if (m.MusicType == 1 && owned.Contains(m.Id)) pool.Add(m.Id);
                if (pool.Count == 0) return;

                int pick = pool[UnityEngine.Random.Range(0, pool.Count)];
                MessageManager.Instance().Dispatch(PlayMusicMessage.Create(pick));
                L.LogInfo($"[唱片] 随机播放 {pick}（共 {pool.Count} 张）");
            }
            catch (Exception e)
            {
                L.LogWarning("[唱片] 切换随机播放失败：" + e.Message);
            }
        }

        // ---------------- 应急面板 ----------------

        private void Update()
        {
            Tick();

            if (inputBroken) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.F8)) showPanel = !showPanel;
            }
            catch
            {
                inputBroken = true;
            }
        }

        private void OnGUI()
        {
            if (!showPanel) return;
            var rect = new Rect(20, 20, 280, 262);
            GUI.Box(rect, "山猪歪瓜 (F8)");
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 28, rect.width - 24, rect.height - 36));
            AutoFarm.Value = GUILayout.Toggle(AutoFarm.Value, " 自动种植(最少果实)");
            AutoCan.Value = GUILayout.Toggle(AutoCan.Value, " 自动开罐");
            AutoMerge.Value = GUILayout.Toggle(AutoMerge.Value, " 自动合成");
            FreeFruit.Value = GUILayout.Toggle(FreeFruit.Value, " 开罐免果实");
            bool ar = GUILayout.Toggle(AutoRedeem.Value, " 自动兑换码");
            if (ar != AutoRedeem.Value)
            {
                AutoRedeem.Value = ar;
            }
            bool rr = GUILayout.Toggle(RandomRecord.Value, " 随机播放唱片");
            if (rr != RandomRecord.Value)
            {
                RandomRecord.Value = rr;
                ApplyShuffle(rr);
            }
            GUILayout.Label($"普通代币：{GetTokenCount(100002)}   活动代币：{GetTokenCount(100003)}");
            GUILayout.EndArea();
        }
    }

    internal static class Patches
    {
        [HarmonyPatch(typeof(FlowerPot), "AutoReplantAfterHarvest")]
        private static class NoVanillaReplant
        {
            private static bool Prefix() => !Plugin.AutoFarm.Value;
        }

        [HarmonyPatch(typeof(BaseCannedItem), "ConsumeFruits")]
        private static class NoFruitCost
        {
            private static bool Prefix() => !Plugin.FreeFruit.Value;
        }

        [HarmonyPatch(typeof(BaseCannedItem), "CheckInventorySufficient")]
        private static class AlwaysEnoughFruit
        {
            private static bool Prefix(ref bool __result)
            {
                if (!Plugin.FreeFruit.Value) return true;
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(DataManager), "Update")]
        private static class DriveFromDataManager
        {
            private static void Postfix()
            {
                Plugin.DriveFromGame();
            }
        }

        [HarmonyPatch(typeof(SettingUI), "Start")]
        private static class HookSettingUi
        {
            private static void Postfix(SettingUI __instance)
            {
                try { Plugin.OnSettingUiReady(__instance); }
                catch { }
            }
        }

        // 打开设置页走的是 MenuUI.SwitchUI → SettingUI.Show()，每次都过这里，
        // 比只跑一次的 Start 可靠得多
        [HarmonyPatch(typeof(BaseUI), "Show")]
        private static class HookAnyUiShow
        {
            private static void Postfix(BaseUI __instance)
            {
                if (!(__instance is SettingUI)) return;
                try { Plugin.OnSettingUiReady((SettingUI)__instance); }
                catch { }
            }
        }

        // 兜底驱动：SteamManager.Update 每帧都在跑（Steam 回调就是它推的）
        [HarmonyPatch(typeof(SteamManager), "Update")]
        private static class DriveFromSteam
        {
            private static void Postfix()
            {
                Plugin.DriveFromSteamManager();
            }
        }

        [HarmonyPatch(typeof(Plant), "Grow")]
        private static class GrowFaster
        {
            private static int hits;

            /// <summary>
            /// 只乘「每秒自然生长」那一路；isClickGrowth=true 是玩家点作物，
            /// 那一路游戏自己还会乘 Player.BuffMultiplier，再乘一次会快得离谱。
            /// </summary>
            private static int clickSkipped;

            private static void Prefix(ref float val, bool isClickGrowth)
            {
                int g = Plugin.GrowSpeed.Value;
                if (isClickGrowth)
                {
                    if (clickSkipped++ == 0) Plugin.Diag("点作物那一路保持原速（不乘倍率）");
                    return;
                }
                if (g <= 1) return;
                val *= g;
                hits++;
                if (hits == 1 || hits % 300 == 0)
                    Plugin.Diag("生长加速生效 #" + hits + " 本次 val=" + val.ToString("F1"));
            }
        }

        // 成就「按键 6,666,666 次」读的是 Steam 统计 total_key_clicks。
        // 每次按键原本 +1，这里先补上 (倍率-1)，原方法随后再 +1 并上报 Steam。
        [HarmonyPatch(typeof(SteamStatsManager), "HandleMessage")]
        private static class KeyClickMultiplier
        {
            private static void Prefix(IMessage __0, ref int ___totalKeyClicks)
            {
                int mul = Plugin.KeyMultiplier.Value;
                if (mul <= 1) return;
                if (!(__0 is KeyClickMessage)) return;
                ___totalKeyClicks += mul - 1;
            }
        }
    }
}
