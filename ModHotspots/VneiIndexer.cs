using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using FastStartup.Core;
using HarmonyLib;
using UnityEngine;
using VNEI;
using VNEI.Logic;
using VNEI.Logic.Compatibility;
using VNEI.Patches;
using Object = UnityEngine.Object;

namespace FastStartup.ModHotspots
{
    /// <summary>
    /// VNEI 0.17.6 <c>Plugin.Update</c> + <c>Indexing.IndexAll</c> reimplemented from the decompile (VNEI's code is the
    /// reference only). Same calls on the same objects in the same order (VNEI's public API: <c>Item</c> /
    /// <c>RecipeInfo</c> constructors, <c>Indexing.AddItem/DisableItem/AddRecipeToItems</c>, its events, its log
    /// lines), but run as an iterator that is stepped <see cref="BudgetMs"/> per frame while the loading screen waits
    /// for the spawn. <c>Indexing.HasIndexed</c> reads false while the iterator runs, so no VNEI UI sees a half-built
    /// index. The spawn frame then does what the original did after <c>IndexAll</c>: <c>UpdateKnown</c> and the three
    /// subscriptions; whatever the loading screen did not finish is finished there first.
    /// Game collections that are walked across frames (prefab list, recipes, piece lists) are copied when their phase
    /// starts; a change to them during the loading screen is reported (<see cref="Signature"/>) at the spawn.
    /// WIP, not equivalent yet (in-game 2026-10-10, run 20261010-054643): before the spawn, IndexRecipes throws a
    /// NullReferenceException inside <c>new RecipeInfo(recipe, quality)</c> (IL_0022, the
    /// <c>GetRequiredStation</c>/<c>GetRequiredStationLevel</c> calls, which other mods patch, e.g. AdventureBackpacks'
    /// <c>GetRequiredStationLevel</c> postfix) and the index ends with 1267 of 4383 recipes. Next step: build only
    /// the item phases (IndexItems, DisableItems) behind the loading screen and the recipe phases in the spawn frame.
    /// </summary>
    internal static class VneiIndexer
    {
        private const double BudgetMs = 6.0;

        private static ManualLogSource _log;
        private static ConfigEntry<bool> _showOnlyKnown;
        private static ConfigEntry<bool> _forceShowOnlyKnown;
        private static Regex _richTag;
        private static FieldInfo _items;
        private static FieldInfo _namedPrefabs;
        private static FieldInfo _tooltipCurrent;
        private static FieldInfo _onIndexingItems;
        private static FieldInfo _onDisableItems;
        private static FieldInfo _afterDisableItems;
        private static FieldInfo _onIndexingRecipes;
        private static FieldInfo _onIndexingItemRecipes;
        private static FieldInfo _afterIndexingItems;
        private static FieldInfo _afterIndexingRecipes;
        private static FieldInfo _indexFinished;

        private static IEnumerator _run;
        private static bool _inProgress;
        private static bool _stepping;
        private static long _maxStep;
        private static bool _ready;
        private static string _signature;
        private static int _frames;
        private static long _ticks;

        public static void Install(Harmony harmony)
        {
            Type log = typeof(Plugin).Assembly.GetType("VNEI.Log");
            _log = (ManualLogSource)Field(log, "_logSource").GetValue(null);
            _showOnlyKnown = (ConfigEntry<bool>)Field(typeof(Plugin), "showOnlyKnown").GetValue(null);
            _forceShowOnlyKnown = (ConfigEntry<bool>)Field(typeof(Plugin), "forceShowOnlyKnown").GetValue(null);
            _richTag = (Regex)Field(typeof(Plugin), "RichTagRegex").GetValue(null);
            _items = Field(typeof(Indexing), "<Items>k__BackingField");
            _namedPrefabs = Field(typeof(ZNetScene), "m_namedPrefabs");
            _tooltipCurrent = Field(typeof(UITooltip), "m_current");
            _onIndexingItems = Field(typeof(Indexing), "OnIndexingItems");
            _onDisableItems = Field(typeof(Indexing), "OnDisableItems");
            _afterDisableItems = Field(typeof(Indexing), "AfterDisableItems");
            _onIndexingRecipes = Field(typeof(Indexing), "OnIndexingRecipes");
            _onIndexingItemRecipes = Field(typeof(Indexing), "OnIndexingItemRecipes");
            _afterIndexingItems = Field(typeof(Indexing), "AfterIndexingItems");
            _afterIndexingRecipes = Field(typeof(Indexing), "AfterIndexingRecipes");
            _indexFinished = Field(typeof(Indexing), "IndexFinished");
            if (_log == null || _showOnlyKnown == null || _forceShowOnlyKnown == null || _richTag == null)
            {
                throw new InvalidOperationException("VNEI not initialized (Plugin.Awake has not run)");
            }
            // HasIndexed first: callers JIT-compiled after this cannot inline the original body.
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Indexing), nameof(Indexing.HasIndexed)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(VneiIndexer), nameof(HasIndexedPrefix))));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.Logout)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(VneiIndexer), nameof(LogoutPrefix))));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Plugin), "Update"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(VneiIndexer), nameof(UpdatePrefix))));
        }

        private static FieldInfo Field(Type type, string name) =>
            AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException(type.FullName, name);

        // Outside the iterator only: handlers VNEI calls from inside the index see the real value, as in the original
        // (true from the first AddItem on).
        private static bool HasIndexedPrefix(ref bool __result)
        {
            if (_inProgress && !_stepping)
            {
                __result = false;
                return false;
            }
            return true;
        }

        // Leaving the world while the loading screen indexes: finish now, while the scene's objects still exist.
        private static void LogoutPrefix()
        {
            if (_inProgress)
            {
                try
                {
                    Drain();
                    _ready = true;
                    Log.Info("ModHotspots: VNEI index finished at logout (left the world before the spawn)");
                }
                catch (Exception e)
                {
                    _log.LogError(e);
                }
            }
        }

        private static bool UpdatePrefix()
        {
            Update();
            return false;
        }

        private static int ItemCount() => ((Dictionary<string, Item>)_items.GetValue(null)).Count;

        /// <summary>Plugin.Update (VNEI 0.17.6), with the index built ahead in <see cref="Step"/>.</summary>
        private static void Update()
        {
            if (Plugin.openHotkey.Value.IsKeyDown())
            {
                Plugin.OpenUI();
            }
            bool player = (bool)Player.m_localPlayer;
            if (player && (_ready || !Indexing.HasIndexed()))
            {
                long before = Stopwatch.GetTimestamp();
                bool ahead = _ready;
                if (!_ready)
                {
                    Drain();
                }
                _ready = false;
                Report(ahead, Stopwatch.GetTimestamp() - before);
                Indexing.UpdateKnown();
                _showOnlyKnown.SettingChanged += delegate { Indexing.UpdateKnown(); };
                _forceShowOnlyKnown.SettingChanged += delegate { Indexing.UpdateKnown(); };
                KnownRecipesPatches.OnUpdateKnownRecipes += Indexing.UpdateKnown;
            }
            else if (!player && !_ready)
            {
                Step();
            }
            if (!Plugin.viewRecipeHotkey.Value.IsKeyDown())
            {
                return;
            }
            UITooltip current = (UITooltip)_tooltipCurrent.GetValue(null);
            if (!current)
            {
                return;
            }
            string text = current.m_topic.Trim();
            string text2 = current.m_text.Trim();
            Item item = null;
            if (text.Length > 0)
            {
                item = Indexing.GetItem(text);
            }
            if (item == null && text2.Length > 0)
            {
                item = Indexing.GetItem(text2);
            }
            if (item == null && _richTag.IsMatch(text))
            {
                item = Indexing.GetItem(_richTag.Replace(text, string.Empty).Trim());
            }
            if (item != null)
            {
                if (Plugin.attachToCrafting.Value)
                {
                    Plugin.GetMainUI().SetTabActive();
                }
                Plugin.GetMainUI().GetBaseUI().ShowRecipe(item, trackHistory: true);
            }
        }

        /// <summary>Loading screen: start once the world waits for the spawn, then run up to <see cref="BudgetMs"/>.</summary>
        private static void Step()
        {
            if (_run == null)
            {
                if (!Game.instance || !Game.instance.WaitingForRespawn() || !ZNetScene.instance || !ObjectDB.instance ||
                    ItemCount() > 0)
                {
                    return;
                }
                _signature = Signature();
                _run = Run();
                _inProgress = true;
            }
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
            _frames++;
            _stepping = true;
            try
            {
                while (Stopwatch.GetTimestamp() - start < budget)
                {
                    if (!_run.MoveNext())
                    {
                        _run = null;
                        _inProgress = false;
                        _ready = true;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                // As an exception out of the original IndexAll: the index stays as far as it got, nothing more runs.
                _run = null;
                _inProgress = false;
                _log.LogError(e);
            }
            finally
            {
                _stepping = false;
            }
            long took = Stopwatch.GetTimestamp() - start;
            _ticks += took;
            _maxStep = Math.Max(_maxStep, took);
        }

        private static void Drain()
        {
            if (_run == null)
            {
                _run = Run();
            }
            _inProgress = true;
            _stepping = true;
            try
            {
                while (_run.MoveNext())
                {
                }
            }
            finally
            {
                _run = null;
                _inProgress = false;
                _stepping = false;
            }
        }

        private static void Report(bool ahead, long spawnTicks)
        {
            string now = Signature();
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "ModHotspots: VNEI index {0}: {1} loading frames, {2:F1} ms before the spawn (longest frame {3:F1} ms), {4:F1} ms in the spawn frame",
                ahead ? "built behind the loading screen" : _frames > 0 ? "partly built behind the loading screen" : "built in the spawn frame",
                _frames, _ticks * 1000.0 / Stopwatch.Frequency, _maxStep * 1000.0 / Stopwatch.Frequency,
                spawnTicks * 1000.0 / Stopwatch.Frequency));
            if (_signature != null && now != _signature)
            {
                Log.Warning($"ModHotspots: VNEI index inputs changed during the loading screen ({_signature} -> {now}); " +
                            "set [ModHotspots] VNEIIndexing = false if VNEI misses items");
            }
            _signature = null;
        }

        private static string Signature()
        {
            if (!ZNetScene.instance || !ObjectDB.instance)
            {
                return "no scene";
            }
            var named = (Dictionary<int, GameObject>)_namedPrefabs.GetValue(ZNetScene.instance);
            // Recipe content too (what IndexRecipes reads), hashed in-process: a config sync that edits recipes in
            // place during the loading screen shows up even when no count changes.
            int hash = 17;
            foreach (Recipe recipe in ObjectDB.instance.m_recipes)
            {
                if (!recipe)
                {
                    continue;
                }
                hash = hash * 31 + (recipe.m_enabled ? 1 : 0);
                hash = hash * 31 + recipe.m_amount;
                hash = hash * 31 + (recipe.m_item ? recipe.m_item.name.GetHashCode() : 0);
                hash = hash * 31 + (recipe.m_craftingStation ? recipe.m_craftingStation.name.GetHashCode() : 0);
                hash = hash * 31 + recipe.m_minStationLevel;
                foreach (Piece.Requirement requirement in recipe.m_resources ?? new Piece.Requirement[0])
                {
                    hash = hash * 31 + (requirement.m_resItem ? requirement.m_resItem.name.GetHashCode() : 0);
                    hash = hash * 31 + requirement.m_amount * 7 + requirement.m_amountPerLevel;
                }
            }
            return $"prefabs {ZNetScene.instance.m_prefabs.Count}/{named.Count}, recipes {ObjectDB.instance.m_recipes.Count} " +
                   $"#{hash:x8}, items {ObjectDB.instance.m_items.Count}";
        }

        private static void Invoke<T>(FieldInfo field, T arg)
        {
            try
            {
                ((Action<T>)field.GetValue(null))?.Invoke(arg);
            }
            catch (Exception data)
            {
                _log.LogError(data);
            }
        }

        private static void Invoke(FieldInfo field)
        {
            try
            {
                ((Action)field.GetValue(null))?.Invoke();
            }
            catch (Exception data)
            {
                _log.LogError(data);
            }
        }

        /// <summary>Indexing.IndexAll; one step per prefab / recipe / piece.</summary>
        private static IEnumerator Run()
        {
            if (ItemCount() > 0)
            {
                yield break;
            }
            if (!ZNetScene.instance)
            {
                _log.LogWarning("Cannot index: ZNetScene.instance is null");
                yield break;
            }
            _log.LogInfo("Index items and recipes");
            ModNames.IndexModNames();
            yield return null;
            var pieceTables = new Dictionary<string, PieceTable>();
            List<GameObject> prefabs = GetPrefabs();
            yield return null;
            foreach (object step in IndexItems(prefabs, pieceTables))
            {
                yield return step;
            }
            foreach (object step in DisableItems(prefabs))
            {
                yield return step;
            }
            foreach (object step in IndexRecipes())
            {
                yield return step;
            }
            foreach (object step in IndexItemRecipes(prefabs, pieceTables))
            {
                yield return step;
            }
            List<RecipeInfo> recipes = RecipeInfo.Recipes;
            for (int i = 0; i < recipes.Count; i++)
            {
                recipes[i].CalculateIsOnBlacklist();
                recipes[i].CalculateWidth();
                if (i % 64 == 63)
                {
                    yield return null;
                }
            }
            FavouritesSave.Load();
            _log.LogInfo($"Loaded {Indexing.GetActiveItems().Count()} items and {RecipeInfo.Recipes.Count} recipes");
            Invoke(_indexFinished);
        }

        private static List<GameObject> GetPrefabs()
        {
            var first = new HashSet<GameObject>(ZNetScene.instance.m_prefabs);
            var second = new HashSet<GameObject>(((Dictionary<int, GameObject>)_namedPrefabs.GetValue(ZNetScene.instance)).Values);
            List<GameObject> list = first.Union(second).ToList();
            list.RemoveAll(prefab => !prefab);
            return list;
        }

        private static IEnumerable IndexItems(List<GameObject> prefabs, Dictionary<string, PieceTable> pieceTables)
        {
            Indexing.AddItem(new Item("vnei_any_item", "$vnei_any_item", string.Empty, null, ItemType.Undefined, null));
            Indexing.AddItem(new Item("vnei_unknown_item", "$vnei_unknown_item", string.Empty, null, ItemType.Undefined, null));
            Indexing.AddItem(new Item("vnei_all_stations", "$vnei_all_stations", "", null, ItemType.Undefined, null));
            Indexing.AddItem(new Item("vnei_hand_station", "$vnei_hand_station", "", Plugin.Instance.inventoryIcon, ItemType.Undefined, null));
            Indexing.AddItem(new Item("vnei_no_station", "$vnei_no_station", "", null, ItemType.Undefined, null));
            Plugin.Instance.allStations = Indexing.GetItem("vnei_all_stations");
            Plugin.Instance.handStation = Indexing.GetItem("vnei_hand_station");
            Plugin.Instance.noStation = Indexing.GetItem("vnei_no_station");
            Indexing.DisableItem("vnei_any_item", "is not an item");
            Indexing.DisableItem("vnei_unknown_item", "is not an item");
            Indexing.DisableItem("vnei_all_stations", "is not an item");
            Indexing.DisableItem("vnei_hand_station", "is not an item");
            Indexing.DisableItem("vnei_no_station", "is not an item");
            foreach (GameObject prefab in prefabs)
            {
                string name = prefab.name;
                string text = string.Empty;
                if (prefab.TryGetComponent(out HoverText hoverText))
                {
                    text = hoverText.m_text;
                }
                if (string.IsNullOrEmpty(text) && prefab.TryGetComponent(out CraftingStation station))
                {
                    text = station.m_name;
                }
                if (name.StartsWith("TreasureChest"))
                {
                    TryAddItem<Piece>(prefab, i => i.m_name, text, ItemType.Piece, i => i.m_description, i => i.m_icon);
                }
                if (prefab.TryGetComponent(out ItemDrop itemDrop))
                {
                    ItemDrop.ItemData itemData = itemDrop.m_itemData;
                    Sprite icon = null;
                    if ((bool)itemDrop && itemData.m_shared.m_icons.Length != 0)
                    {
                        icon = itemData.GetIcon();
                    }
                    if (itemData.m_shared.m_damageModifiers == null)
                    {
                        itemData.m_shared.m_damageModifiers = new List<HitData.DamageModPair>();
                    }
                    ItemType itemType = ItemTypeHelper.GetItemType(itemData);
                    ItemDrop.ItemData.SharedData shared = itemData.m_shared;
                    var item = new Item(name, shared.m_name, shared.m_description, icon, itemType, prefab, shared.m_maxQuality);
                    item.itemDropType = itemData.m_shared.m_itemType;
                    Indexing.AddItem(item);
                    if ((bool)itemData.m_shared.m_buildPieces)
                    {
                        pieceTables.Add(Indexing.CleanupName(name), itemData.m_shared.m_buildPieces);
                    }
                }
                TryAddItem<Character>(prefab, i => i.m_name, text, ItemType.Creature);
                TryAddItem<MineRock>(prefab, i => i.m_name, text, ItemType.Undefined);
                TryAddItem<MineRock5>(prefab, i => i.m_name, text, ItemType.Undefined);
                TryAddItem<DropOnDestroyed>(prefab, i => string.Empty, text, ItemType.Undefined);
                TryAddItem<Pickable>(prefab, i => string.Empty, text, ItemType.Undefined);
                TryAddItem<SpawnArea>(prefab, i => string.Empty, text, ItemType.Creature);
                TryAddItem<Destructible>(prefab, i => string.Empty, text, ItemType.Undefined);
                TryAddItem<TreeBase>(prefab, i => string.Empty, text, ItemType.Undefined);
                TryAddItem<Trader>(prefab, i => i.m_name, text, ItemType.Undefined);
                Invoke(_onIndexingItems, prefab);
                yield return null;
            }
            foreach (PieceTable table in pieceTables.Values)
            {
                foreach (GameObject piece in new List<GameObject>(table.m_pieces))
                {
                    TryAddItem<Piece>(piece, i => i.m_name, piece.name, ItemType.Piece, i => i.m_description, i => i.m_icon);
                    Invoke(_onIndexingItems, piece);
                    yield return null;
                }
            }
            Invoke(_afterIndexingItems);
        }

        private static IEnumerable DisableItems(List<GameObject> prefabs)
        {
            foreach (GameObject prefab in prefabs)
            {
                if (prefab.TryGetComponent(out Piece piece) && Indexing.GetItem(prefab.name) == null)
                {
                    _log.LogDebug("not indexed piece " + piece.name + ": not buildable");
                }
                if (prefab.TryGetComponent(out Humanoid humanoid))
                {
                    DisableArray(prefab, humanoid.m_defaultItems);
                    DisableArray(prefab, humanoid.m_randomWeapon);
                    DisableArray(prefab, humanoid.m_randomShield);
                    DisableArray(prefab, humanoid.m_randomArmor);
                    if (humanoid.m_randomSets != null)
                    {
                        Humanoid.ItemSet[] randomSets = humanoid.m_randomSets;
                        for (int i = 0; i < randomSets.Length; i++)
                        {
                            DisableArray(prefab, randomSets[i]?.m_items);
                        }
                    }
                    DisableEffectList(prefab, humanoid.m_hitEffects);
                    DisableEffectList(prefab, humanoid.m_critHitEffects);
                    DisableEffectList(prefab, humanoid.m_backstabHitEffects);
                    DisableEffectList(prefab, humanoid.m_deathEffects);
                    DisableEffectList(prefab, humanoid.m_waterEffects);
                    DisableEffectList(prefab, humanoid.m_tarEffects);
                    DisableEffectList(prefab, humanoid.m_slideEffects);
                    DisableEffectList(prefab, humanoid.m_jumpEffects);
                    DisableEffectList(prefab, humanoid.m_pickupEffects);
                    DisableEffectList(prefab, humanoid.m_dropEffects);
                    DisableEffectList(prefab, humanoid.m_consumeItemEffects);
                    DisableEffectList(prefab, humanoid.m_equipEffects);
                    DisableEffectList(prefab, humanoid.m_perfectBlockEffect);
                }
                Invoke(_onDisableItems, prefab);
                yield return null;
            }
            Invoke(_afterDisableItems);
        }

        private static void DisableArray(GameObject from, GameObject[] array)
        {
            if (array == null)
            {
                return;
            }
            foreach (GameObject item in array.Where(i => (bool)i))
            {
                ItemDrop component = item.GetComponent<ItemDrop>();
                if ((bool)component)
                {
                    ItemDrop.ItemData itemData = component.m_itemData;
                    if (itemData != null && itemData.m_shared?.m_icons?.Length > 0)
                    {
                        _log.LogDebug("Not disabling item " + item.name + " because it has icons");
                        continue;
                    }
                }
                Indexing.DisableItem(item.name, "is defaultItem from " + from.name);
            }
        }

        private static void DisableEffectList(GameObject from, EffectList effectList)
        {
            if (effectList?.m_effectPrefabs == null)
            {
                return;
            }
            foreach (EffectList.EffectData item in effectList.m_effectPrefabs.Where(i => i != null && (bool)i.m_prefab))
            {
                Indexing.DisableItem(item.m_prefab.name, "is defaultItem from " + from.name);
            }
        }

        private static IEnumerable IndexRecipes()
        {
            if (!ObjectDB.instance)
            {
                _log.LogWarning("Cannot index recipes: ObjectDB.instance is null");
                yield break;
            }
            foreach (Recipe recipe in new List<Recipe>(ObjectDB.instance.m_recipes))
            {
                bool flag = WackysDatabaseCompat.HasRecipeEnableOverride(recipe);
                if (!(recipe.m_enabled | flag) || !recipe.m_item)
                {
                    continue;
                }
                for (int i = 1; i <= recipe.m_item.m_itemData.m_shared.m_maxQuality; i++)
                {
                    if ((!flag || i != 1 || !WackysDatabaseCompat.CanBeUpgraded(recipe)) &&
                        (!flag || i <= 1 || !WackysDatabaseCompat.CanBeCrafted(recipe)))
                    {
                        Indexing.AddRecipeToItems(new RecipeInfo(recipe, i));
                    }
                }
                Invoke(_onIndexingRecipes, recipe);
                yield return null;
            }
        }

        private static IEnumerable IndexItemRecipes(List<GameObject> prefabs, Dictionary<string, PieceTable> pieceTables)
        {
            foreach (GameObject prefab in prefabs)
            {
                TryAddRecipeToItemsForEach<Smelter, Smelter.ItemConversion>(prefab, i => i.m_conversion, (s, i) => new RecipeInfo(i, s));
                TryAddRecipeToItemsForEach<Fermenter, Fermenter.ItemConversion>(prefab, i => i.m_conversion, (f, i) => new RecipeInfo(i, f));
                TryAddRecipeToItemsForEach<CookingStation, CookingStation.ItemConversion>(prefab, i => i.m_conversion, (c, i) => new RecipeInfo(i, c));
                if (prefab.TryGetComponent(out Incinerator incinerator))
                {
                    Indexing.AddRecipeToItems(new RecipeInfo(incinerator));
                    foreach (Incinerator.IncineratorConversion conversion in incinerator.m_conversions)
                    {
                        if (conversion.m_requireOnlyOneIngredient)
                        {
                            foreach (Incinerator.Requirement requirement in conversion.m_requirements)
                            {
                                Indexing.AddRecipeToItems(new RecipeInfo(conversion, requirement, incinerator));
                            }
                        }
                        else
                        {
                            Indexing.AddRecipeToItems(new RecipeInfo(conversion, incinerator));
                        }
                    }
                }
                TryAddRecipeToItems<CharacterDrop>(prefab, i => new RecipeInfo(i), i => (bool)i.GetComponent<Character>());
                TryAddRecipeToItems<Growup>(prefab, i => new RecipeInfo(i));
                TryAddRecipeToItems<MineRock>(prefab, i => new RecipeInfo(prefab, i.m_dropItems));
                TryAddRecipeToItems<MineRock5>(prefab, i => new RecipeInfo(prefab, i.m_dropItems));
                TryAddRecipeToItems<DropOnDestroyed>(prefab, i => new RecipeInfo(prefab, i.m_dropWhenDestroyed));
                prefab.TryGetComponent(out Piece piece);
                if ((bool)piece && prefab.TryGetComponent(out Container container) && container.m_defaultItems.m_drops.Count > 0)
                {
                    Indexing.AddRecipeToItems(new RecipeInfo(container.gameObject, container.m_defaultItems));
                }
                TryAddRecipeToItems<Pickable>(prefab, i => new RecipeInfo(prefab, i));
                TryAddRecipeToItems<SpawnArea>(prefab, i => new RecipeInfo(i));
                if (prefab.TryGetComponent(out Destructible destructible))
                {
                    if ((bool)destructible.m_spawnWhenDestroyed && Indexing.GetItem(destructible.m_spawnWhenDestroyed.name) != null)
                    {
                        Indexing.AddRecipeToItems(new RecipeInfo(destructible));
                    }
                    else if (destructible.enabled && !prefab.GetComponent<DropOnDestroyed>() && !prefab.GetComponent<Plant>())
                    {
                        Indexing.DisableItem(prefab.name, "destructible.m_spawnWhenDestroyed is null or not indexed");
                    }
                }
                TryAddRecipeToItems<TreeBase>(prefab, i => new RecipeInfo(i));
                TryAddRecipeToItemsForEach<Trader, Trader.TradeItem>(prefab, i => i.m_items, (t, i) => new RecipeInfo(t, i));
                Invoke(_onIndexingItemRecipes, prefab);
                yield return null;
            }
            foreach (KeyValuePair<string, PieceTable> pieceTable in pieceTables)
            {
                if (!pieceTable.Value)
                {
                    _log.LogDebug("IndexItemRecipes: pieceTable " + pieceTable.Key + " is null");
                    continue;
                }
                foreach (GameObject piece in new List<GameObject>(pieceTable.Value.m_pieces))
                {
                    if (!piece)
                    {
                        _log.LogDebug("IndexItemRecipes: prefab in pieceTables " + pieceTable.Key + " is null");
                    }
                    else if (piece.TryGetComponent(out Piece component) && !component.m_repairPiece)
                    {
                        Indexing.AddRecipeToItems(new RecipeInfo(piece, component, Indexing.GetItem(pieceTable.Key)));
                    }
                    yield return null;
                }
            }
            Invoke(_afterIndexingRecipes);
        }

        private static void TryAddItem<T>(GameObject target, Func<T, string> getName, string fallbackLocalizedName, ItemType itemType,
            Func<T, string> getDescription = null, Func<T, Sprite> getIcon = null) where T : Component
        {
            if (!target.TryGetComponent(out T arg))
            {
                return;
            }
            try
            {
                string description = getDescription?.Invoke(arg) ?? string.Empty;
                Sprite icon = getIcon?.Invoke(arg);
                string text = getName(arg);
                if (string.IsNullOrEmpty(text))
                {
                    text = fallbackLocalizedName;
                }
                Indexing.AddItem(new Item(target.name, text, description, icon, itemType, target));
            }
            catch (Exception ex)
            {
                _log.LogError(target.name + Environment.NewLine + ex + Environment.NewLine);
            }
        }

        private static void TryAddRecipeToItems<T>(GameObject target, Func<T, RecipeInfo> getRecipe, Func<T, bool> check = null) where T : Component
        {
            if (!target.TryGetComponent(out T arg) || (check != null && !check(arg)))
            {
                return;
            }
            RecipeInfo recipeInfo;
            try
            {
                recipeInfo = getRecipe(arg);
            }
            catch (Exception ex)
            {
                _log.LogError(target.name + Environment.NewLine + ex + Environment.NewLine);
                return;
            }
            Indexing.AddRecipeToItems(recipeInfo);
        }

        private static void TryAddRecipeToItemsForEach<T1, T2>(GameObject target, Func<T1, List<T2>> getArray, Func<T1, T2, RecipeInfo> getRecipe)
            where T1 : Component
        {
            if (!target.TryGetComponent(out T1 component))
            {
                return;
            }
            foreach (T2 item in getArray(component))
            {
                try
                {
                    Indexing.AddRecipeToItems(getRecipe(component, item));
                }
                catch (Exception ex)
                {
                    _log.LogError(target.name + Environment.NewLine + ex + Environment.NewLine);
                }
            }
        }
    }
}
