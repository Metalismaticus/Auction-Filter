using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

// ============================================================
//  Item Rarity Mod  для Vintage Story 1.22
// ============================================================

namespace ClothingRarity
{
    // ── Модели для stackrandomizer.json ──────────────────────────────
    internal class SRDef
    {
        public Dictionary<string, SRPool>? attributesByType;
    }
    internal class SRPool  { public SRStack[]? stacks; }
    internal class SRStack { public string? code; public float chance; }

    // ── Модели для trader-*.json ──────────────────────────────────────
    internal class TraderList    { public TraderSection? selling; }
    internal class TraderSection { public TraderItem[]? list; }
    internal class TraderItem    { public string? code; public TraderPrice? price; }
    internal class TraderPrice   { public float avg; }

    // ── Данные на предмет ────────────────────────────────────────────
    internal class PoolEntry
    {
        public float  Chance;
        public float  PoolTotal;
        public string ContainerLabel;
        public PoolEntry(float c, float t, string l) { Chance = c; PoolTotal = t; ContainerLabel = l; }
    }
    internal class ItemRarityData
    {
        public List<PoolEntry> Pools = new List<PoolEntry>();
        public PoolEntry? BestPool => Pools.Count == 0 ? null : Pools[0];
        public void AddPool(float c, float t, string l)
        {
            // If same chest and same pool, aggregate (e.g. multiple lantern materials share one code)
            var existing = Pools.Find(p => p.ContainerLabel == l && Math.Abs(p.PoolTotal - t) < 0.01f);
            if (existing != null)
                existing.Chance += c;
            else
                Pools.Add(new PoolEntry(c, t, l));
            Pools.Sort((a, b) => b.Chance.CompareTo(a.Chance));
        }
    }

    // ── Основной ModSystem ───────────────────────────────────────────
    public class ItemRarityMod : ModSystem
    {
        private static readonly Dictionary<string, string> TargetPools = new Dictionary<string, string>
        {
            { "cloth-lowstatus",        "Low-status ruins chest"     },
            { "cloth-mediumstatus",     "Medium-status ruins chest"  },
            { "cloth-highstatus",       "High-status ruins chest"    },
            { "accessory-lowstatus",    "Low-status ruins chest"     },
            { "accessory-mediumstatus", "Medium-status ruins chest"  },
            { "accessory-highstatus",   "High-status ruins chest"    },
            { "theater",                "Theater prop chest"         },
            { "butterfly",              "Butterfly collector's box"  },
            { "lazaret",                "Lazaret chest"              },
            { "painting",               "Starving artist's chest"   },
            { "lantern",                "Workshop / Guardroom chest" },
            { "ruinedweapon",           "Guardroom chest"            },
            { "armor",                  "Tomb / Cathedral chest"     },
        };

        private static readonly string[] TrackedPrefixes =
        {
            "clothes-", "hoovedwearables-",
            "painting-",
            "lantern-", "chandelier-",
            "armor-", "blade-", "axe-", "club-", "knife-", "spear-",
        };

        // Префиксы, которым ВСЕГДА показываем тир (Common, если нет в loot)
        private static readonly string[] DefaultCommonPrefixes =
        {
            "clothes-", "hoovedwearables-", "painting-",
        };

        // Особые имена для красивого отображения
        private static readonly Dictionary<string, string> TraderNameOverrides = new Dictionary<string, string>
        {
            { "trader-treasurehunter", "Treasure hunter"          },
            { "trader-survivalgoods",  "Survival goods trader"    },
            { "trader-buildmaterials", "Building materials trader" },
            { "trader-agriculture",    "Farmer"                    },
        };

        internal static Dictionary<string, ItemRarityData>               LootDB  = new Dictionary<string, ItemRarityData>();
        internal static Dictionary<string, (float price, string trader)> TradeDB = new Dictionary<string, (float, string)>();

        // Категории, где КАЖДЫЙ предмет принудительно Legendary (по желанию игрока)
        internal static readonly HashSet<string> ForceLegendaryLabels = new HashSet<string>
        {
            "Lazaret chest",
        };

        internal enum Rarity { Common, Uncommon, Rare, Epic, Legendary }

        // Порог по процентному дропу — не зависит от масштаба пула
        internal static Rarity GetRarity(float pct)
        {
            if (pct >= 15f)   return Rarity.Common;
            if (pct >= 5f)    return Rarity.Uncommon;
            if (pct >= 1.5f)  return Rarity.Rare;
            if (pct >= 0.5f)  return Rarity.Epic;
            return Rarity.Legendary;
        }

        internal static (string color, string label) GetRarityDisplay(Rarity r)
        {
            switch (r)
            {
                case Rarity.Common:    return ("#aaaaaa", "Common");
                case Rarity.Uncommon:  return ("#40a060", "Uncommon");
                case Rarity.Rare:      return ("#4b85c0", "Rare");
                case Rarity.Epic:      return ("#9966cc", "Epic");
                case Rarity.Legendary: return ("#e8a028", "Legendary");
                default:               return ("#ffffff", "Unknown");
            }
        }

        internal static string FormatPct(float chance, float total)
        {
            float p = chance / total * 100f;
            return p < 1f ? $"~{p:0.##}%" : $"~{p:0.#}%";
        }

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        private static readonly JsonSerializerSettings _js = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        // Regex для преобразования VS JSON: unquoted keys → "quoted keys"
        // \b([a-zA-Z_]\w*)\s*:  матчит только слова-идентификаторы перед двоеточием.
        // Уже закавыченные ключи не затрагивает: после "word" идёт " не :
        private static readonly Regex _keyRe = new Regex(@"\b([a-zA-Z_]\w*)\s*:");

        // ─────────────────────────────────────────────────────────────
        public override void AssetsLoaded(ICoreAPI api)
        {
            LootDB.Clear();
            TradeDB.Clear();
            LoadStackRandomizer(api);
            LoadTraderPrices(api);
            api.Logger.Notification("[Rarity] {0} loot entries, {1} trader prices loaded",
                LootDB.Count, TradeDB.Count);
        }

        private void LoadStackRandomizer(ICoreAPI api)
        {
            // Читаем сырой текст — VS JSON имеет unquoted ключи, ToObject<T>() не справится
            string? rawText = null;
            var asset = api.Assets.TryGet("survival:itemtypes/meta/stackrandomizer.json");
            if (asset != null)
            {
                rawText = System.Text.Encoding.UTF8.GetString(asset.Data);
                api.Logger.Notification("[Rarity] Loaded stackrandomizer via asset API");
            }
            else
            {
                var path = System.IO.Path.Combine(GamePaths.AssetsPath, "survival",
                    "itemtypes", "meta", "stackrandomizer.json");
                if (System.IO.File.Exists(path))
                {
                    rawText = System.IO.File.ReadAllText(path);
                    api.Logger.Notification("[Rarity] Loaded stackrandomizer from disk: {0}", path);
                }
            }

            if (rawText == null)
            {
                api.Logger.Warning("[Rarity] stackrandomizer.json not found!");
                return;
            }

            // Превращаем VS JSON в стандартный JSON: `code:` → `"code":`
            // Regex не трогает уже закавыченные ключи — после word идёт " а не :
            string json = _keyRe.Replace(rawText, "\"$1\":");

            SRDef? def;
            try { def = JsonConvert.DeserializeObject<SRDef>(json, _js); }
            catch (Exception e)
            {
                api.Logger.Warning("[Rarity] Parse failed: {0}", e.Message);
                return;
            }

            if (def?.attributesByType == null)
            {
                api.Logger.Warning("[Rarity] attributesByType is null after parse");
                return;
            }

            foreach (var kv in def.attributesByType)
            {
                // Ключ вида "*-cloth-lowstatus" → "cloth-lowstatus"
                string poolKey = kv.Key.StartsWith("*-") ? kv.Key.Substring(2) : kv.Key;
                if (!TargetPools.TryGetValue(poolKey, out string? label)) continue;

                var pool = kv.Value;
                if (pool?.stacks == null || pool.stacks.Length == 0) continue;

                float total = 0;
                foreach (var s in pool.stacks) total += s.chance;
                if (total <= 0f) continue;

                foreach (var s in pool.stacks)
                {
                    if (string.IsNullOrEmpty(s.code)) continue;

                    bool tracked = false;
                    foreach (var prefix in TrackedPrefixes)
                        if (s.code.StartsWith(prefix)) { tracked = true; break; }
                    if (!tracked) continue;

                    if (!LootDB.ContainsKey(s.code))
                        LootDB[s.code] = new ItemRarityData();
                    LootDB[s.code].AddPool(s.chance, total, label!);
                }
            }
        }

        private void LoadTraderPrices(ICoreAPI api)
        {
            var dir = System.IO.Path.Combine(GamePaths.AssetsPath, "survival", "config", "tradelists");
            if (!System.IO.Directory.Exists(dir))
            {
                api.Logger.Warning("[Rarity] Tradelists directory not found: {0}", dir);
                return;
            }

            foreach (var filePath in System.IO.Directory.GetFiles(dir, "*.json"))
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
                if (!fileName.StartsWith("trader-") && !fileName.StartsWith("villager-")) continue;

                string traderName = DeriveTraderName(fileName);
                string json;
                try { json = System.IO.File.ReadAllText(filePath); }
                catch { continue; }

                TraderList? list;
                try { list = JsonConvert.DeserializeObject<TraderList>(json, _js); }
                catch { continue; }

                if (list?.selling?.list == null) continue;

                foreach (var item in list.selling.list)
                {
                    if (string.IsNullOrEmpty(item.code) || item.price == null) continue;
                    float price = item.price.avg;
                    if (!TradeDB.TryGetValue(item.code, out var existing) || price < existing.price)
                        TradeDB[item.code] = (price, traderName);
                }
            }
        }

        private static string DeriveTraderName(string fileName)
        {
            if (TraderNameOverrides.TryGetValue(fileName, out var overrideName))
                return overrideName;

            if (fileName.StartsWith("trader-"))
            {
                string n = fileName.Substring("trader-".Length);
                return char.ToUpper(n[0]) + n.Substring(1) + " trader";
            }
            if (fileName.StartsWith("villager-"))
            {
                string n = fileName.Substring("villager-".Length);
                return char.ToUpper(n[0]) + n.Substring(1) + " (villager)";
            }
            return fileName;
        }

        // ─────────────────────────────────────────────────────────────
        public override void AssetsFinalize(ICoreAPI api)
        {
            var craftable = new HashSet<string>();
            foreach (var recipe in api.World.GridRecipes)
                if (recipe?.Output?.Code != null)
                    craftable.Add(recipe.Output.Code.Path);

            foreach (var item  in api.World.Items)  TryAttach(item,  craftable);
            foreach (var block in api.World.Blocks)  TryAttach(block, craftable);
        }

        private static void TryAttach(CollectibleObject? obj, HashSet<string> craftable)
        {
            if (obj?.Code == null) return;
            string code = obj.Code.Path;

            // Только одежда — никакого оружия, брони, фонарей, картин
            bool isClothing = code.StartsWith("clothes-") || code.StartsWith("hoovedwearables-");
            if (!isClothing) return;

            bool isCraft = craftable.Contains(code);

            var arr = obj.CollectibleBehaviors;
            var nb  = new CollectibleBehavior[arr.Length + 1];
            arr.CopyTo(nb, 0);
            nb[arr.Length] = new RarityBehavior(obj, code, isCraft, isClothing);
            obj.CollectibleBehaviors = nb;
        }
    }

    // ── Tooltip behaviour ────────────────────────────────────────────
    public class RarityBehavior : CollectibleBehavior
    {
        private const string CraftColor   = "#cd853f"; // peru (бронза/дерево)
        private const string NpcColor     = "#daa520"; // goldenrod (золото/торговля)
        private const string SectionColor = "#c8aa6e"; // стандартный VS-amber

        private readonly string _code;
        private readonly bool   _isCraft;
        private readonly bool   _isClothOrPainting;

        public RarityBehavior(CollectibleObject obj, string code, bool isCraft, bool isClothOrPainting)
            : base(obj)
        {
            _code              = code;
            _isCraft           = isCraft;
            _isClothOrPainting = isClothOrPainting;
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc,
            IWorldAccessor world, bool withDebugInfo)
        {
            bool isSold  = ItemRarityMod.TradeDB.TryGetValue(_code, out var trade);
            bool hasLoot = ItemRarityMod.LootDB.TryGetValue(_code, out var data) && data!.BestPool != null;

            ItemRarityMod.Rarity rarity;
            bool showSources = false;

            if (_isCraft)
            {
                // Крафтовые → всегда Common
                rarity = ItemRarityMod.Rarity.Common;
            }
            else if (isSold)
            {
                // Продаётся у НПС → всегда Uncommon (доступность через торговца)
                rarity = ItemRarityMod.Rarity.Uncommon;
            }
            else if (hasLoot)
            {
                var   best    = data!.BestPool!;
                float bestPct = best.Chance / best.PoolTotal * 100f;
                // Legendary только если предмет встречается ИСКЛЮЧИТЕЛЬНО в особых сундуках
                bool  forceLeg = data.Pools.TrueForAll(p => ItemRarityMod.ForceLegendaryLabels.Contains(p.ContainerLabel));
                rarity = forceLeg ? ItemRarityMod.Rarity.Legendary : ItemRarityMod.GetRarity(bestPct);
                // Некрафтовые — не ниже Uncommon
                if (rarity == ItemRarityMod.Rarity.Common) rarity = ItemRarityMod.Rarity.Uncommon;
                showSources = true;
            }
            else
            {
                // Не крафтовое, не продаётся, нет в loot → минимум Uncommon
                rarity = ItemRarityMod.Rarity.Uncommon;
            }

            // Заголовок: Tier  [Craftable] [Sold] — теги только для одежды
            var (color, label) = ItemRarityMod.GetRarityDisplay(rarity);
            var header = new StringBuilder();
            header.Append($"<font color=\"{color}\"><strong>{label}</strong></font>");
            if (_isClothOrPainting)
            {
                if (_isCraft)
                    header.Append($"  <font color=\"{CraftColor}\">Craftable</font>");
                if (isSold)
                    header.Append($"  <font color=\"{NpcColor}\">Sold</font>");
            }
            header.Append('\n');
            dsc.Insert(0, header.ToString());

            // Источники из руин
            if (showSources)
            {
                dsc.AppendLine($"\n<font color=\"{SectionColor}\">Found in ruins:</font>");
                foreach (var pool in data!.Pools)
                {
                    string pct = ItemRarityMod.FormatPct(pool.Chance, pool.PoolTotal);
                    dsc.AppendLine($"  - {pool.ContainerLabel} <font color=\"#888888\">({pct})</font>");
                }
            }

            // Цена у НПС
            if (isSold)
            {
                dsc.AppendLine($"<font color=\"{SectionColor}\">Sold by:</font> {trade.trader} " +
                               $"<font color=\"#b8860b\">(~{trade.price:0} RG)</font>");
            }

            if (withDebugInfo)
                dsc.AppendLine($"<font color=\"#444444\">[Rarity] {_code}</font>");
        }
    }
}
