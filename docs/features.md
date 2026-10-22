# 功能与原理

> 所有改动都只作用于本地客户端。游戏是 Unity 2022.3 + Mono，插件走 BepInEx 5 + Harmony。

## 界面是怎么塞进去的

游戏设置页的标签栏是 `SettingBtns`，问号按钮叫 `ThankBtn`。插件克隆这个按钮、换成自己的
猪头图标，位置在原按钮基础上 +155px，并把标签栏整体加宽。

内容页克隆的是「鸣谢」子页（`ThankSubUI`），但原页面自带的本地化组件会在 `Start` 里把文本
覆盖回模板内容，所以克隆后先把 `LocalizationText` 全部摘掉，再重建一个 Content 容器
（VerticalLayoutGroup + ContentSizeFitter），旧 Content 直接销毁。

字体不是自己找的，是从游戏设置行的标签上"抄"下来的：`font`、`fontStyle`、`fontSharedMaterial`、
`fontSize` 全部复制。游戏用的是 `cjkFonts-allseto-v1`、字号 65、Bold。**这一步很关键**——
之前写死 40/44 就会看起来比游戏的字小一圈。

## 自动种植 / 自动开罐 / 自动合成

- 种植：扫描自己的地块，找库存最少且不到 99 的果实补种；期间临时屏蔽游戏自带的续种逻辑，避免抢种。
- 开罐：直接调 `BaseCannedItem.ExchangeCannedItem()`，和手点按钮走同一条路径。开罐消耗
  3+3 果实 + 1 代币，没有冷却；代币按 `tPoolConfig.TokenId` 读，用完即停。
- 合成：`SyntheticUI` 的「填满 + 合成」，5 件同品质合 1 件，跳过传说级和 Default/Promo 池。

## 生长加速（只乘自然生长）

游戏里作物成长只有一个入口：

```csharp
public void Grow(float val, bool isClickGrowth = false)
{
    float num = 1f;
    if (isClickGrowth && Player != null) num = Player.BuffMultiplier;   // 联机增益
    plantData.growthValue += val * num;
    ...
}
```

调用点有两处：

| 场景 | 调用 |
| --- | --- |
| 每秒自然生长 | `Grow(ConfigManager.SecondsToGrow)` |
| 点一下作物 | `Grow(ConfigManager.ClickToGrow, isClickGrowth: true)` |

插件在 `Grow` 前挂前缀补丁，**只在 `isClickGrowth == false` 时**把 `val` 乘上倍率。
两边都乘的话，×10 实际是「每秒 10 倍 + 点一下 10 倍再叠增益」，成熟快到不像 10 倍。

实测日志（原版每秒 7）：

```
生长加速生效 #1 本次 val=70.0
点作物那一路保持原速（不乘倍率）
```

## 按键倍率

成就读的是 Steam 统计 `total_key_clicks`。游戏流程是
按键 → `KeyClickMessage` → `SteamStatsManager.HandleMessage` → `totalKeyClicks++` → `SetStat`。

补丁在 `HandleMessage` 前先补 `(倍率-1)` 次，随后游戏自己再 +1 并上报，一次按键记 N 次。
倍率 1 时完全等同原版。

## 自动偷菜

偷菜的真实链路是：点对方菜地上的按钮 → `SendStealRequestMessage` → 对面客户端校验
（不能重复偷、总果数 > 1）→ 返回 `steal_result` → 本地加果实。

插件只是每 1.5 秒扫一遍场景，谁的偷菜按钮亮着就替点一下，**不绕过任何校验**。
房间设置里关了偷菜时按钮根本不亮，插件也就不会动。

## 自动兑换码

客户端只认 `TbCouponCode` 这张表（当前 20 条），`CannedUI.OnClickCoupon` 先查表拿 itemId，
查不到直接判无效。所以：

- 能自动领：**表里已有的码**
- 领不了：公告发了但没进表的码（客户端直接判无效）

插件每分钟扫一次表，从后往前（新码在后面）比对 Steam 库存，没领的调
`SteamInventoryManager.AddPromoItem` 领一次，间隔 2.5 秒；失败的记 30 分钟冷却。

## 点击切换为什么不用按钮

克隆出来的行原本挂着 `Button`，但它的持久化监听指向被销毁的 `SpriteToggle`，点击事件
还会被上层控件吃掉。所以倍率行改成**每帧自己判定**：`Input.GetMouseButtonDown(0)` +
`RectTransformUtility.RectangleContainsScreenPoint`，点整行任意位置都算。

## 驱动方式

插件不依赖任何 GameObject 生命周期（宿主对象在首个场景加载时会被销毁）。它往
`PlayerLoop` 的 Update 阶段插了一个自己的子系统，并在判空时用 `ReferenceEquals`
绕开 Unity 重载的 `==`（假 null）。
