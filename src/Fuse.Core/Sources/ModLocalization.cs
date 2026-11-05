using System;
using System.Collections.Generic;
using System.Linq;

namespace Fuse.Core.Sources;

/// <summary>
/// 模组中文名对照表。
///
/// 存在的原因:Modrinth 的项目元数据(标题/描述)基本只有英文,直接搜「钠」只会返回汉化包和整合包,
/// 搜「Sodium」才能找到本体。所以中文名在前端靠自己维护一份热门模组的对照表:
/// ① 列表显示成「中文名 English Name」;② 中文关键词先映射成英文名再去搜索。
/// 表没收录的模组会退回显示/搜索原始英文名。
/// </summary>
public static class ModLocalization
{
    /// <summary>(slug, 中文名, 英文名)。slug 用于精确识别项目,英文名用于搜索。</summary>
    private static readonly (string Slug, string Zh, string En)[] Table =
    {
        // ---- 性能优化 ----
        ("sodium", "钠", "Sodium"),
        ("lithium", "锂", "Lithium"),
        ("phosphor", "磷", "Phosphor"),
        ("starlight", "星光", "Starlight"),
        ("indium", "铟", "Indium"),
        ("sodium-extra", "钠：附加选项", "Sodium Extra"),
        ("embeddium", "嵌入式渲染优化", "Embeddium"),
        ("oculus", "视觉之眼（光影）", "Oculus"),
        ("iris", "虹膜光影", "Iris Shaders"),
        ("entityculling", "实体剔除", "Entity Culling"),
        ("entity-culling", "实体剔除", "Entity Culling"),
        ("krypton", "氪（网络优化）", "Krypton"),
        ("ferritecore", "铁氧体核心（省内存）", "FerriteCore"),
        ("modernfix", "现代修复", "ModernFix"),
        ("memoryleakfix", "内存泄漏修复", "Memory Leak Fix"),
        ("bobby", "远景区块缓存", "Bobby"),
        ("distanthorizons", "遥远的地平线", "Distant Horizons"),
        ("c2me-fabric", "并发区块引擎", "C2ME"),
        ("vmp-fabric", "多线程优化", "Very Many Players"),
        ("noisium", "地形生成优化", "Noisium"),

        // ---- 地图 / HUD ----
        ("xaeros-minimap", "Xaero 小地图", "Xaero's Minimap"),
        ("xaeros-world-map", "Xaero 世界地图", "Xaero's World Map"),
        ("journeymap", "旅行地图", "JourneyMap"),
        ("minihud", "迷你 HUD", "MiniHUD"),
        ("jade", "Jade 方块信息", "Jade"),
        ("wthit", "WTHIT 方块信息", "WTHIT"),
        ("appleskin", "苹果皮（饱食度显示）", "AppleSkin"),
        ("modmenu", "模组菜单", "Mod Menu"),
        ("betterf3", "更好的调试屏幕", "BetterF3"),

        // ---- 物品 / 界面 ----
        ("jei", "JEI 物品管理器", "Just Enough Items"),
        ("just-enough-items", "JEI 物品管理器", "Just Enough Items"),
        ("rei", "REI 物品管理器", "Roughly Enough Items"),
        ("roughly-enough-items", "REI 物品管理器", "Roughly Enough Items"),
        ("emi", "EMI 物品管理器", "EMI"),
        ("controlling", "按键冲突检索", "Controlling"),
        ("mouse-tweaks", "鼠标手势增强", "Mouse Tweaks"),
        ("inventory-profiles-next", "背包配置", "Inventory Profiles Next"),

        // ---- 前置库 ----
        ("fabric-api", "Fabric API", "Fabric API"),
        ("cloth-config", "布料配置库", "Cloth Config API"),
        ("architectury-api", "架构 API", "Architectury API"),
        ("geckolib", "GeckoLib 动画库", "GeckoLib"),
        ("patchouli", "帕秋莉手册", "Patchouli"),
        ("balm", "Balm 前置库", "Balm"),
        ("bookshelf", "书架前置库", "Bookshelf"),
        ("placebo", "安慰剂前置库", "Placebo"),
        ("citadel", "堡垒前置库", "Citadel"),
        ("curios", "饰品栏", "Curios API"),
        ("trinkets", "饰品栏", "Trinkets"),
        ("kubejs", "KubeJS 脚本", "KubeJS"),
        ("crafttweaker", "合成修改器", "CraftTweaker"),

        // ---- 科技 / 工业 ----
        ("create", "机械动力", "Create"),
        ("createaddition", "机械动力：附加", "Create Crafts & Additions"),
        ("create-steam-n-rails", "机械动力：蒸汽与铁轨", "Create: Steam 'n' Rails"),
        ("create-fabric", "机械动力（Fabric 版）", "Create Fabric"),
        ("immersiveengineering", "沉浸工程", "Immersive Engineering"),
        ("mekanism", "通用机械", "Mekanism"),
        ("mekanism-generators", "通用机械：发电机", "Mekanism Generators"),
        ("thermal-foundation", "热力基础", "Thermal Foundation"),
        ("thermal-expansion", "热力膨胀", "Thermal Expansion"),
        ("thermal-dynamics", "热力动力学", "Thermal Dynamics"),
        ("appliedenergistics2", "应用能源 2", "Applied Energistics 2"),
        ("ae2", "应用能源 2", "Applied Energistics 2"),
        ("refinedstorage", "精致存储", "Refined Storage"),
        ("storagedrawers", "储物抽屉", "Storage Drawers"),
        ("iron-chests", "铁箱子", "Iron Chests"),
        ("sophisticatedbackpacks", "精致背包", "Sophisticated Backpacks"),
        ("sophisticated-storage", "精致存储", "Sophisticated Storage"),
        ("enderio", "末影接口", "Ender IO"),
        ("rftools", "RF 工具", "RFTools"),
        ("computercraft", "电脑", "ComputerCraft"),
        ("cc-tweaked", "电脑：改装版", "CC: Tweaked"),
        ("powah", "电力", "Powah"),
        ("draconicevolution", "龙之研究", "Draconic Evolution"),
        ("gregtechceu-modern", "格雷科技", "GregTech CEu Modern"),
        ("forestry", "林业", "Forestry"),
        ("railcraft", "铁路", "Railcraft"),
        ("buildcraft", "建筑", "BuildCraft"),

        // ---- 魔法 ----
        ("botania", "植物魔法", "Botania"),
        ("bloodmagic", "血魔法", "Blood Magic"),
        ("arsnouveau", "新生魔艺", "Ars Nouveau"),
        ("mysticalagriculture", "神秘农业", "Mystical Agriculture"),
        ("apotheosis", "神化", "Apotheosis"),
        ("ensorcellation", "附魔", "Ensorcellation"),

        // ---- 冒险 / 内容 ----
        ("tconstruct", "匠魂", "Tinkers' Construct"),
        ("tinkers-construct", "匠魂", "Tinkers' Construct"),
        ("quark", "夸克", "Quark"),
        ("supplementaries", "补充物", "Supplementaries"),
        ("alexsmobs", "亚历克斯的生物", "Alex's Mobs"),
        ("alexs-mobs", "亚历克斯的生物", "Alex's Mobs"),
        ("ice-and-fire", "冰与火", "Ice and Fire"),
        ("twilightforest", "暮色森林", "The Twilight Forest"),
        ("biomesoplenty", "超多生物群系", "Biomes O' Plenty"),
        ("terralith", "地形重构", "Terralith"),
        ("oh-the-biomes-youll-go", "你的群系", "Oh The Biomes You'll Go"),
        ("when-dungeons-arise", "地牢崛起", "When Dungeons Arise"),
        ("waystones", "传送石碑", "Waystones"),
        ("comforts", "舒适睡眠", "Comforts"),
        ("farmers-delight", "农夫乐事", "Farmer's Delight"),
        ("croptopia", "作物托邦", "Croptopia"),
        ("pams-harvestcraft", "潘马斯的农业", "Pam's HarvestCraft"),
        ("cooking-for-blockheads", "方块头的烹饪", "Cooking for Blockheads"),
        ("aquaculture", "水产养殖", "Aquaculture"),
        ("veinmining", "连锁挖矿", "Vein Mining"),
        ("right-click-harvest", "右键收获", "Right Click Harvest"),
        ("mob-grinding-utils", "刷怪塔工具", "Mob Grinding Utils"),
        ("epicfight", "史诗战斗", "Epic Fight"),
        ("bettercombat", "更好的战斗", "Better Combat"),
        ("pehkui", "尺寸变化", "Pehkui"),
        ("first-person-model", "第一人称模型", "First Person Model"),
        ("not-enough-animations", "更多动画", "Not Enough Animations"),
        ("falling-leaves", "落叶", "Falling Leaves"),
        ("presence-footsteps", "脚步回响", "Presence Footsteps"),
        ("ambientsounds", "环境音效", "Ambient Sounds"),
        ("simple-voice-chat", "简单语音", "Simple Voice Chat"),
        ("open-parties-and-claims", "队伍与领地", "Open Parties and Claims"),
        ("ftb-chunks", "FTB 区块认领", "FTB Chunks"),
        ("ftb-teams", "FTB 队伍", "FTB Teams"),
    };

    private static readonly Dictionary<string, string> ZhBySlug = new(StringComparer.OrdinalIgnoreCase);
    private static readonly (string Slug, string Zh, string En)[] SortedByZhLength;

    static ModLocalization()
    {
        foreach (var row in Table)
            ZhBySlug[row.Slug] = row.Zh;
        // 中文名短的更精确("钠" 优先于 "钠：附加选项")
        SortedByZhLength = Table.OrderBy(r => r.Zh.Length).ToArray();
    }

    /// <summary>按 slug 取中文名;未收录返回 null。</summary>
    public static string? ChineseName(string? slug)
    {
        if (string.IsNullOrEmpty(slug)) return null;
        return ZhBySlug.TryGetValue(slug!, out var zh) ? zh : null;
    }

    /// <summary>查询串里是否含中日韩字符(含中文时才需要做映射)。</summary>
    public static bool HasChinese(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text!)
            if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
        return false;
    }

    /// <summary>
    /// 中文关键词 → 英文搜索词。命中中文名(或其中一部分)时返回该模组的英文名,
    /// 例如「钠」→「Sodium」、「机械动力」→「Create」。未收录返回 null(调用方回退原文搜索)。
    /// </summary>
    public static string? ToEnglishQuery(string? chineseQuery)
    {
        if (!HasChinese(chineseQuery)) return null;
        var q = chineseQuery!.Trim();
        if (q.Length == 0) return null;

        // 1. 精确等于中文名
        foreach (var row in SortedByZhLength)
            if (string.Equals(row.Zh, q, StringComparison.Ordinal))
                return row.En;

        // 2. 中文名包含查询词(用户只输入了一部分)
        foreach (var row in SortedByZhLength)
            if (row.Zh.IndexOf(q, StringComparison.Ordinal) >= 0)
                return row.En;

        // 3. 查询词包含中文名(用户输入了更长的描述)
        foreach (var row in SortedByZhLength)
            if (q.IndexOf(row.Zh, StringComparison.Ordinal) >= 0)
                return row.En;

        return null;
    }
}
