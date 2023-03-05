using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.Sniper.Models;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.Sniper.Services
{
    public class SniperService
    {
        public const string PetItemKey = "petItem";
        public static int MIN_TARGET = 200_000;
        public ConcurrentDictionary<string, PriceLookup> Lookups = new ConcurrentDictionary<string, PriceLookup>();

        private ConcurrentQueue<LogEntry> Logs = new ConcurrentQueue<LogEntry>();
        private ConcurrentQueue<(SaveAuction, ReferenceAuctions)> LbinUpdates = new();
        private AuctionKey defaultKey = new AuctionKey();
        public SniperState State { get; set; } = SniperState.LadingLbin;
        private PropertyMapper mapper = new();
        private string[] EmptyArray = new string[0];

        public event Action<LowPricedAuction> FoundSnipe;
        private readonly HashSet<string> IncludeKeys = new HashSet<string>()
        {
            "baseStatBoostPercentage", // has an effect on drops from dungeons, is filtered to only max level

            "dye_item",
            "backpack_color",
            "party_hat_color",
            "color", // armour
            "model", // abicase
            // potion "level", // not engough impact
            // "item_tier", // mostly found on armor, unsure what it does
            "talisman_enrichment", // talismans can be enriched with additional stats
            "drill_part_engine",
            "drill_part_fuel_tank",
            // deemend to low difference "drill_part_upgrade_module",
            "ability_scroll", // applied to hyperions worth ~50m https://discord.com/channels/267680588666896385/1031668335731019886/1031668607479975976
            // magma armor is to cheap "magmaCubesKilled"
            "captured_player", // cake soul 
            "event", // year+eventtype
            "wood_singularity_count", // self explanatory
            "art_of_war_count", //       ^^
            "artOfPeaceApplied",
            "new_years_cake", // year of the cake
            "heldItem", // pet held item
            "skin", // cosmetic skins
            "candyUsed", // candy count
            "exp", // collected experience of pets
            "rarity_upgrades", // recomb
            "winning_bid", // price paid for midas
            "dungeon_item_level", "upgrade_level", // "stars"
            "farming_for_dummies_count",
            "unlocked_slots", // available gemstone slots
            "gemstone_slots", // old unlocked slots
            "zombie_kills", // slayer kills
            "spider_kills", // slayer kills
            "eman_kills", // slayer kills
            "expertise_kills", // unkown kind of kills
            "bow_kills", // huricane bow
            "raider_kills", // raiders axe
            "sword_kills",
            "yogsKilled", // yog armor
            "ethermerge",
            "edition", // great spook stuff
            "hpc", // hot potato books
            //"tuned_transmission", // aotv upgrade
            //"power_ability_scroll", // disabled as suggested by Coyu because comonly not worth 1m (up to 2m at most)
            "captured_player", // cake souls
            "MUSIC", //rune
            "DRAGON", //rune
            "TIDAL", //rune
            "GRAND_SEARING", //rune
            "ENCHANT" // rune
        };

        /// <summary>
        /// Keys containing itemTags that should be added separately
        /// </summary>
        private readonly HashSet<string> ItemKeys = new()
        {
            "drill_part_engine",
            "drill_part_fuel_tank",
            "drill_part_upgrade_module",
        };

        private static readonly Dictionary<string, short> ShardAttributes = new(){
            {"mana_pool", 1},
            {"breeze", 1},
            {"speed", 2},
            {"life_regeneration", 2}, // especially valuable in combination with mana_pool
            {"fishing_experience", 2},
            {"ignition", 2},
            {"blazing_fortune", 2},
            {"double_hook", 3},
            {"mana_regeneration", 2},
            {"mending", 3},
            {"dominance", 3},
            {"magic_find", 2}
            //{"lifeline", 3} to low volume
            // life recovery 3
        };

        // combos that are worth more starting at lvl 1 because they are together
        private readonly Dictionary<string, string> AttributeCombos = new(){
            {"blazing_fortune", "fishing_experience"},
            {"life_regeneration", "mana_pool"},
            {"veteran", "mending"},
            {"mana_regeneration", "mana_pool"}
        };
        private readonly ConcurrentDictionary<string, HashSet<string>> AttributeComboLookup = new();

        public void FinishedUpdate()
        {
            while (LbinUpdates.TryDequeue(out var update))
            {
                var (auction, bucket) = update;
                var key = auction.Uuid;
                var item = CreateReferenceFromAuction(auction);
                if (bucket.Lbins == null)
                    bucket.Lbins = new();
                if (!bucket.Lbins.Contains(item))
                {
                    bucket.Lbins.Add(item);
                    bucket.Lbins.Sort(ReferencePrice.Compare);
                }
            }
        }

        private readonly Dictionary<string, string> ModifierItemPrefixes = new()
        {
            {"drill_part_engine", String.Empty},
            {"drill_part_fuel_tank", String.Empty},
            {"drill_part_upgrade_module", String.Empty},
            {"skin", String.Empty},
            {"petItem", "PET_ITEM_"}
        };

        private Dictionary<Core.Enchantment.EnchantmentType, byte> MinEnchantMap = new();

        /** NOTES
yogsKilled - needs further be looked into
skeletorKills - to low volume to include 
farmed_cultivating - tells the state of the cultivating enchant (already taken care of)

*/
        /* select helper
SELECT l.AuctionId,l.KeyId,l.Value,a.StartingBid, a.HighestBidAmount,a.Uuid,a.Tag,a.End FROM `NBTLookups` l, Auctions a
where KeyId = 128
and auctionid > 305000000  
and AuctionId = a.Id  
ORDER BY l.`AuctionId`  DESC;

        */

        // stuff changing value by 10+M
        public static HashSet<string> VeryValuable = new HashSet<string>()
        {
            "upgrade_level", // lvl 8+ are over 10m
            "rarity_upgrades",
            "winning_bid",
            "exp",
            "color",
            "dye_item",
            "ethermerge",
            "unlocked_slots",
            "new_years_cake" // not that valuable but the only attribute
        };

        // 200m+
        public static HashSet<string> Increadable = new HashSet<string>()
        {
            "ability_scroll",
            "skin",
            "color"
        };

        public static HashSet<string> NeverDrop = new()
        {
            "exp", // this helps with closest match 
            "party_hat_color", // closest to clean would mix them up
            "ability_scroll", // most expensive attribues in the game
            "new_years_cake" // not that valuable but the only attribute
        };

        private static KeyValuePair<string, string> Ignore = new KeyValuePair<string, string>(string.Empty, string.Empty);


        public SniperService()
        {

            this.FoundSnipe += la =>
            {
                if (la.Finder == LowPricedAuction.FinderType.SNIPER && (float)la.Auction.StartingBid / la.TargetPrice < 0.8 && la.TargetPrice > 1_000_000)
                    Console.WriteLine($"A: {la.Auction.Uuid} {la.Auction.StartingBid} -> {la.TargetPrice}  {KeyFromSaveAuction(la.Auction)}");
            };
            foreach (var item in AttributeCombos.ToList())
            {
                AttributeComboLookup.GetOrAdd(item.Key, a => new()).Add(item.Value);
                AttributeComboLookup.GetOrAdd(item.Value, a => new()).Add(item.Key);
            }
            foreach (var item in AttributeCombos)
            {
                IncludeKeys.Add(item.Key);
            }
            foreach (var item in ShardAttributes)
            {
                IncludeKeys.Add(item.Key);
            }

            foreach (var enchant in Enum.GetValues<Core.Enchantment.EnchantmentType>())
            {
                MinEnchantMap[enchant] = 6;
            }

            foreach (var item in Coflnet.Sky.Core.Constants.RelevantEnchants)
            {
                MinEnchantMap[item.Type] = item.Level;
            }
        }

        public PriceEstimate GetPrice(SaveAuction auction)
        {
            if (auction == null || auction.Tag == null)
                return null;

            var result = new PriceEstimate();
            if (Lookups.TryGetValue(auction.Tag, out PriceLookup lookup))
            {
                var l = lookup.Lookup;
                var itemKey = KeyFromSaveAuction(auction);
                result.ItemKey = itemKey.ToString();
                if (l.TryGetValue(itemKey, out ReferenceAuctions bucket))
                {
                    if (result.Lbin.AuctionId == default && bucket.Lbin.AuctionId != default)
                    {
                        result.Lbin = bucket.Lbin;
                        result.LbinKey = itemKey.ToString();
                    }
                    if (result.Median == default && bucket.Price != default)
                    {
                        AssignMedian(result, itemKey, bucket);
                    }
                }

                if (result.Median == default)
                {
                    if (itemKey.GetHashCode() % 3 == 0 && DateTime.UtcNow.Millisecond % 30 == 0)
                        Console.WriteLine("Finding closest median brute for " + auction.Tag + itemKey);
                    var closest = FindClosestTo(l, itemKey);

                    if (closest.Key != default)
                    {
                        AssignMedian(result, closest.Key, closest.Value);
                        AdjustMedianForModifiers(result, itemKey, closest);
                    }

                }
                if (result.Lbin.Price == default && l.Count > 0)
                {
                    var closest = l.Where(l => l.Key != null && l.Value?.Lbin.Price > 0).OrderByDescending(m => itemKey.Similarity(m.Key) + Math.Min(m.Value.Volume, 2)).FirstOrDefault();
                    if (closest.Key != default)
                    {
                        result.Lbin = closest.Value.Lbin;
                        result.LbinKey = closest.Key.ToString();
                    }
                }
            }
            return result;
        }

        private void AdjustMedianForModifiers(PriceEstimate result, AuctionKey itemKey, KeyValuePair<AuctionKey, ReferenceAuctions> closest)
        {
            var missingModifiers = closest.Key.Modifiers.Where(m => !itemKey.Modifiers.Contains(m)).ToList();
            if (missingModifiers.Count > 0)
            {
                long median = GetPriceSumForModifiers(missingModifiers, itemKey.Modifiers);
                if (median > 0)
                {
                    result.Median -= median;
                    result.MedianKey += $"- {string.Join(",", missingModifiers.Select(m => m.Value))}";
                }
            }
        }

        private long GetPriceSumForModifiers(List<KeyValuePair<string, string>> missingModifiers, List<KeyValuePair<string, string>> modifiers)
        {
            if(missingModifiers == null)
                return 0;
            var values = missingModifiers.SelectMany<KeyValuePair<string, string>, string>(m =>
            {
                if (ModifierItemPrefixes.TryGetValue(m.Key, out var prefix))
                    return new string[] { prefix + m.Value.ToUpper() };
                if (m.Value == "PERFECT")
                    return new string[] { $"PERFECT_{m.Key.Split('_').First()}_GEM" };
                if (m.Value == "FLAWLESS")
                    return new string[] { $"FLAWLESS_{m.Key.Split('_').First()}_GEM" };
                if (mapper.TryGetIngredients(m.Key, m.Value, modifiers?.Where(mi => mi.Key == m.Key).Select(mi => mi.Value).FirstOrDefault(), out var ingredients))
                {
                    return ingredients;
                }
                return EmptyArray;
            }).Where(m => m != null).Select(k =>
            {
                if (Lookups.TryGetValue(k, out var lookup))
                {
                    return lookup.Lookup;
                }
                return null;
            }).Where(m => m != null).ToList();
            var median = values.SelectMany(m => m.Values).Select(m => m.Price).DefaultIfEmpty(0).Sum();
            return median;
        }

        private static KeyValuePair<AuctionKey, ReferenceAuctions> FindClosestTo(ConcurrentDictionary<AuctionKey, ReferenceAuctions> l, AuctionKey itemKey)
        {
            return FindClosest(l, itemKey).FirstOrDefault();
        }
        public static IEnumerable<KeyValuePair<AuctionKey, ReferenceAuctions>> FindClosest(ConcurrentDictionary<AuctionKey, ReferenceAuctions> l, AuctionKey itemKey)
        {
            var minDay = GetDay() - 8;
            return l.Where(l => l.Key != null && l.Value?.References != null && l.Value.Price > 0)
                            .OrderByDescending(m => itemKey.Similarity(m.Key) + (m.Value.OldestRef > minDay ? 0 : -10));
        }

        private static void AssignMedian(PriceEstimate result, AuctionKey key, ReferenceAuctions bucket)
        {
            result.Median = bucket.Price;
            result.Volume = bucket.Volume;
            result.MedianKey = key.ToString();
        }

        internal void Move(string tag, long auctionId, AuctionKey from, AuctionKey to)
        {
            var oldBucket = Lookups[tag].Lookup[from];
            var newBucket = GetOrAdd(to, Lookups[tag]);

            var toChange = oldBucket.References.Where(e => e.AuctionId == auctionId).First();
            var newList = oldBucket.References.Where(e => e.AuctionId != auctionId).ToList();
            oldBucket.References = new ConcurrentQueue<ReferencePrice>(newList);

            if (!newBucket.References.Contains(toChange))
                newBucket.References.Enqueue(toChange);

            UpdateMedian(oldBucket);
            UpdateMedian(newBucket);
        }

        public IEnumerable<long> GetReferenceUids(SaveAuction auction)
        {
            if (TryGetReferenceAuctions(auction, out ReferenceAuctions bucket))
                return bucket.References.Select(r => r.AuctionId);
            return new long[0];
        }

        /// <summary>
        /// Adds persisted lookup data
        /// </summary>
        /// <param name="itemTag"></param>
        /// <param name="loadedVal"></param>
        public void AddLookupData(string itemTag, PriceLookup loadedVal)
        {
            Lookups.AddOrUpdate(itemTag, loadedVal, (key, value) =>
            {
                foreach (var item in loadedVal.Lookup)
                {
                    if (!value.Lookup.TryGetValue(item.Key, out ReferenceAuctions existingBucket))
                    {
                        value.Lookup[item.Key] = item.Value;
                        continue;
                    }
                    existingBucket.References = item.Value.References;
                    existingBucket.Price = item.Value.Price;
                    if (item.Value.Lbins == null)
                        item.Value.Lbins = new();
                    // load all non-empty lbins
                    foreach (var binAuction in item.Value.Lbins)
                    {
                        if (!existingBucket.Lbins.Contains(binAuction) && binAuction.Price > 0)
                            existingBucket.Lbins.Add(binAuction);
                    }
                    item.Value.Lbins.Sort(ReferencePrice.Compare);
                }
                return value;
            });
        }

        public void AddSoldItem(SaveAuction auction)
        {
            ReferenceAuctions bucket = GetBucketForAuction(auction);
            if (bucket.References.Where(r => r.AuctionId == auction.UId).Any())
                return; // duplicate
            var reference = CreateReferenceFromAuction(auction);
            // move reference to sold
            bucket.References.Enqueue(reference);
            bucket.Lbins.Remove(reference);
            bucket.HitsSinceCalculating = 0;
            UpdateMedian(bucket);
        }

        public static void UpdateMedian(ReferenceAuctions bucket)
        {
            var size = bucket.References.Count;
            if (size > 90)
                bucket.References.TryDequeue(out ReferencePrice ra);
            var deduplicated = bucket.References
                .OrderByDescending(b => b.Day)
                .Take(60)
                .GroupBy(a => a.Seller)
                .Select(a => a.Last())  // only use one (the last) price from each seller
                .ToList();
            size = deduplicated.Count();
            if (size <= 3)
            {
                bucket.Price = 0; // to low vol
                return;
            }
            // short term protects against price drops after updates
            var shortTermList = deduplicated.OrderByDescending(b => b.Day).ThenBy(b => b.Price).Take(3).ToList();
            var shortTermPrice = GetMedian(shortTermList);
            bucket.OldestRef = shortTermList.Min(s => s.Day);
            // long term protects against market manipulation
            var longSpanPrice = GetMedian(deduplicated.Take(45).ToList());
            bucket.Price = Math.Min(shortTermPrice, longSpanPrice);
        }

        public ReferenceAuctions GetBucketForAuction(SaveAuction auction)
        {
            if (!Lookups.ContainsKey(auction.Tag))
            {
                Lookups[auction.Tag] = new PriceLookup();
            }
            return GetOrAdd(KeyFromSaveAuction(auction), Lookups[auction.Tag]);
        }

        private static long GetMedian(List<ReferencePrice> deduplicated)
        {
            return deduplicated
                .OrderByDescending(b => b.Price)
                .Skip(deduplicated.Count / 2)
                .Select(b => b.Price)
                .First();
        }

        private ReferenceAuctions CreateAndAddBucket(SaveAuction auction, int dropLevel = 0)
        {
            var key = KeyFromSaveAuction(auction, dropLevel);
            var itemBucket = Lookups[auction.Tag];
            return GetOrAdd(key, itemBucket);
        }

        private static ReferenceAuctions GetOrAdd(AuctionKey key, PriceLookup itemBucket)
        {
            return itemBucket.Lookup.GetOrAdd(key, (k) => new ReferenceAuctions());
        }

        private static ReferencePrice CreateReferenceFromAuction(SaveAuction auction)
        {
            return new ReferencePrice()
            {
                AuctionId = auction.UId,
                Day = GetDay(auction.End),
                Price = auction.HighestBidAmount == 0 ? auction.StartingBid : auction.HighestBidAmount,
                Seller = auction.AuctioneerId == null ? (short)(auction.SellerId % (2 << 14)) : Convert.ToInt16(auction.AuctioneerId.Substring(0, 4), 16)
            };
        }

        public static short GetDay(DateTime date = default)
        {
            if (date == default)
                date = DateTime.UtcNow;
            return (short)(date - new DateTime(2021, 9, 25)).TotalDays;
        }

        private bool TryGetReferenceAuctions(SaveAuction auction, out ReferenceAuctions bucket)
        {
            bucket = null;
            if (!Lookups.TryGetValue(auction.Tag, out PriceLookup lookup))
                return false;
            var l = lookup.Lookup;
            if (l.TryGetValue(KeyFromSaveAuction(auction), out bucket))
                return true;
            if (l.TryGetValue(KeyFromSaveAuction(auction, 1), out bucket))
                return true;
            if (l.TryGetValue(KeyFromSaveAuction(auction, 2), out bucket))
                return true;
            return l.TryGetValue(KeyFromSaveAuction(auction, 3), out bucket);
        }

        private static List<KeyValuePair<string, string>> EmptyModifiers = new();
        private static List<KeyValuePair<string, string>> EmptyPetModifiers = new() { new("exp", "0"), new("candyUsed", "0") };
        private static DateTime UnlockedIntroduction = new DateTime(2021, 9, 4);
        private static List<string> GemPurities = new() { "PERFECT", "FLAWLESS", "FINE", "ROUGH" };
        public AuctionKey KeyFromSaveAuction(SaveAuction auction, int dropLevel = 0)
        {
            var key = new AuctionKey();

            var shouldIncludeReforge = Coflnet.Sky.Core.Constants.RelevantReforges.Contains(auction.Reforge) && dropLevel < 3;
            key.Reforge = shouldIncludeReforge ? auction.Reforge : ItemReferences.Reforge.Any;
            if (dropLevel == 0)
            {
                key.Enchants = auction.Enchantments
                    ?.Where(e => e.Level >= MinEnchantMap[e.Type])
                    .Select(e => new Models.Enchantment() { Lvl = e.Level, Type = e.Type }).ToList();

                key.Modifiers = auction.FlatenedNBT?.Where(n =>
                                       IncludeKeys.Contains(n.Key)
                                    || n.Value == "PERFECT"
                                    || n.Key.StartsWith("MASTER_CRYPT_TANK_ZOMBIE")
                                    || n.Key.StartsWith("MINOS_CHAMPION_")
                                    || n.Key == "MINOS_INQUISITOR_750"
                                    || n.Key.StartsWith("MASTER_CRYPT_UNDEAD_") && n.Key.Length > 23) // admins
                                .OrderByDescending(n => n.Key)
                                .Select(i => NormalizeData(i, auction))
                                .Where(i => i.Key != Ignore.Key).ToList();
                if (auction.ItemCreatedAt < UnlockedIntroduction && auction.FlatenedNBT.Any(v => GemPurities.Contains(v.Value)))
                    key.Modifiers.Add(new KeyValuePair<string, string>("unlocked_slots", "all"));
            }
            else if (dropLevel == 1 || dropLevel == 2)
            {
                key.Modifiers = auction.FlatenedNBT?.Where(n => VeryValuable.Contains(n.Key) || Increadable.Contains(n.Key) || n.Value == "PERFECT")
                            .OrderByDescending(n => n.Key)
                            .ToList();
                key.Enchants = auction.Enchantments
                    ?.Where(e => Coflnet.Sky.Core.Constants.RelevantEnchants.Where(el => el.Type == e.Type && el.Level <= e.Level).Any())
                    .Select(e => new Models.Enchantment() { Lvl = e.Level, Type = e.Type }).ToList();
                if (key?.Enchants?.Count == 0)
                {
                    var enchant = Constants.SelectBest(auction.Enchantments);
                    key.Enchants = new List<Models.Enchantment>() { new Models.Enchantment() { Lvl = enchant.Level, Type = enchant.Type } };
                }
            }
            else if (dropLevel == 3)
            {
                var enchant = Constants.SelectBest(auction.Enchantments);
                if (enchant == default)
                    key.Enchants = new List<Models.Enchantment>();
                else
                    key.Enchants = new List<Models.Enchantment>() { new Models.Enchantment() { Lvl = enchant.Level, Type = enchant.Type } };
                AssignEmptyModifiers(auction, key);
            }
            else
            {
                //key.Modifiers = new List<KeyValuePair<string, string>>();
                key.Enchants = new List<Models.Enchantment>();
                AssignEmptyModifiers(auction, key);
            }

            if (key.Enchants == null)
                key.Enchants = new List<Models.Enchantment>();
            key.Tier = auction.Tier;
            if (auction.Tag == "ENCHANTED_BOOK")
            {
                // rarities don't matter for enchanted books and often used for scamming
                key.Tier = Tier.UNCOMMON;
            }
            key.Count = (byte)auction.Count;

            // order attributes
            if (key.Modifiers != null)
                key.Modifiers = key.Modifiers.OrderBy(m => m.Key).ToList();
            // order enchants
            if (key.Enchants != null)
                key.Enchants = key.Enchants.OrderBy(e => e.Type).ToList();

            return key;
        }

        private static void AssignEmptyModifiers(SaveAuction auction, AuctionKey key)
        {
            if (auction.FlatenedNBT.Any(n => NeverDrop.Contains(n.Key)))
                key.Modifiers = auction.FlatenedNBT.Where(n => NeverDrop.Contains(n.Key)).ToList();
            else
                key.Modifiers = EmptyModifiers;
            if (auction.Tag.StartsWith("PET_") && !auction.Tag.StartsWith("PET_ITEM") && !auction.Tag.StartsWith("PET_SKIN"))
                if (auction.FlatenedNBT.TryGetValue("heldItem", out var val) && val == "PET_ITEM_TIER_BOOST")
                    key.Modifiers = new(EmptyPetModifiers) { new(PetItemKey, "TB") };
                else
                    key.Modifiers = EmptyPetModifiers;
        }

        private KeyValuePair<string, string> NormalizeData(KeyValuePair<string, string> s, SaveAuction auction)
        {
            if (s.Key == "exp")
                if (auction.Tag == "PET_GOLDEN_DRAGON")
                    return NormalizeNumberTo(s, 30_036_483, 7);
                else
                    return NormalizeNumberTo(s, 4_225_538, 6);
            if (s.Key == "winning_bid")
                return NormalizeNumberTo(s, 10_000_000);
            if (s.Key.EndsWith("_kills"))
                return NormalizeNumberTo(s, 10_000);
            if (s.Key == "yogsKilled")
                return NormalizeNumberTo(s, 5_000, 2);
            if (s.Key == "candyUsed") // all candied are the same
                return new KeyValuePair<string, string>(s.Key, (double.Parse(s.Value) > 0 ? 1 : 0).ToString());
            if (s.Key == "edition")
            {
                var val = int.Parse(s.Value);
                if (val < 100)
                    return new KeyValuePair<string, string>(s.Key, "0");
                if (val < 1000)
                    return new KeyValuePair<string, string>(s.Key, "1000");
                if (val < 10000)
                    return new KeyValuePair<string, string>(s.Key, "10k");
                return new KeyValuePair<string, string>(s.Key, "100k");
            }
            if (s.Key == "hpc")
                return GetNumeric(s) switch
                {
                    15 => new(s.Key, "1"),
                    > 10 => new(s.Key, "0"),
                    _ => Ignore
                };
            if (s.Key == "heldItem")
            {
                var heldItem = s.Value switch
                {
                    "MINOS_RELIC" => "MINOS_RELIC",
                    "DWARF_TURTLE_SHELMET" => "DWARF_TURTLE_SHELMET",
                    "QUICK_CLAW" => "QUICK_CLAW",
                    "PET_ITEM_QUICK_CLAW" => "QUICK_CLAW",
                    "PET_ITEM_TIER_BOOST" => "TB",
                    "PET_ITEM_LUCKY_CLOVER" => "LUCKY",
                    "PET_ITEM_LUCKY_CLOVER_DROP" => "LUCKY",
                    _ => null
                };
                if (heldItem == null)
                    return Ignore;
                return new KeyValuePair<string, string>(PetItemKey, heldItem);
            }
            if (s.Key == "dungeon_item_level")
                return new KeyValuePair<string, string>("upgrade_level", s.Value);
            if (s.Key == "dungeon_item_level" && auction.FlatenedNBT.TryGetValue("upgrade_level", out _))
                return Ignore; // upgrade level is always higher (newer)
            if (ShardAttributes.TryGetValue(s.Key, out var minLvl))
            {
                if (int.Parse(s.Value) >= minLvl)
                    return s;
                if (HasAttributeCombo(s, auction))
                    return s;
                return Ignore;
            }
            if (s.Key == "talisman_enrichment")
                return new KeyValuePair<string, string>("talisman_enrichment", "yes");
            if (s.Key == "baseStatBoostPercentage")
            {
                var val = int.Parse(s.Value);
                if (val < 46)
                    return Ignore;
                //if (val < 50)
                //    return new KeyValuePair<string, string>("baseStatBoost", "46-49");
                if (val == 50) // max level found
                    return new KeyValuePair<string, string>("baseStatBoost", "50");
                if (val > 50)
                    return new KeyValuePair<string, string>("baseStatBoost", ">50");
            }

            return s;
        }

        /// <summary>
        /// Matches valuable attribute combinations
        /// </summary>
        /// <param name="s"></param>
        /// <param name="auction"></param>
        /// <returns></returns>
        private bool HasAttributeCombo(KeyValuePair<string, string> s, SaveAuction auction)
        {
            return AttributeComboLookup.TryGetValue(s.Key, out var otherKeys) && otherKeys.Any(otherKey => auction.FlatenedNBT.TryGetValue(otherKey, out _));
        }

        /// <summary>
        /// Returns keys that are higher value and have to be checked before something is declared to be a snipe
        /// </summary>
        /// <param name="baseKey">The actual auction key</param>
        /// <returns></returns>
        private IEnumerable<AuctionKey> HigherValueKeys(AuctionKey baseKey, ConcurrentDictionary<AuctionKey, ReferenceAuctions> l, double lbinPrice)
        {
            var exp = baseKey.Modifiers.Where(m => m.Key == "exp").FirstOrDefault();
            if (exp.Key != default && exp.Value != "6")
            {
                for (int i = int.Parse(exp.Value) + 1; i < 7; i++)
                {
                    yield return new AuctionKey(baseKey)
                    {
                        Modifiers = baseKey.Modifiers.Where(m => m.Key != "exp").Append(new("exp", i.ToString())).OrderBy(m => m.Key).ToList()
                    };
                }
            }
            foreach (var item in baseKey.Modifiers.Where(m => ShardAttributes.ContainsKey(m.Key)))
            {
                for (int i = int.Parse(item.Value) + 1; i < 10; i++)
                {
                    yield return new AuctionKey(baseKey)
                    {
                        Modifiers = baseKey.Modifiers.Where(m => m.Key != item.Key).Append(new(item.Key, i.ToString())).OrderBy(m => m.Key).ToList()
                    };
                }
            }
            if (baseKey.Count <= 1 && lbinPrice > MIN_TARGET * 20)
            {
                for (int i = (int)baseKey.Tier; i < (int)Tier.VERY_SPECIAL + 1; i++)
                {
                    yield return new AuctionKey(baseKey)
                    {
                        Modifiers = baseKey.Modifiers.Append(new("rarity_upgrades", "1")).OrderBy(m => m.Key).ToList(),
                        Tier = (Tier)(i + 1)
                    };
                    yield return new AuctionKey(baseKey)
                    {
                        Tier = (Tier)(i + 1)
                    };
                }
                foreach (var item in l.Keys.Where(k => k != baseKey && baseKey.Modifiers
                    .All(m => k.Modifiers.Any(km => km.Key == m.Key && km.Value == m.Value))
                            && baseKey.Enchants
                    .All(e => k.Enchants.Any(ek => e.Type == ek.Type && ek.Lvl == e.Lvl)) && k.Tier == baseKey.Tier))
                {
                    if (l[item].Price == 0)
                        continue;
                    Console.WriteLine($"Found higher tier {item} for {baseKey} with {l[item].Lbin.Price} lbin price {l[item].Price}");
                    yield return item;
                }
            }

            if (baseKey.Count > 1 && baseKey.Count < 64)
                yield return new AuctionKey(baseKey) { Count = 64 };
            if (baseKey.Count > 1 && baseKey.Count < 16)
                yield return new AuctionKey(baseKey) { Count = 16 };
        }

        public static KeyValuePair<string, string> NormalizeNumberTo(KeyValuePair<string, string> s, int groupingSize, int highestGroup = int.MaxValue)
        {
            var group = GetNumeric(s) / groupingSize;
            return new KeyValuePair<string, string>(s.Key, Math.Min(group, highestGroup).ToString());
        }

        private static long GetNumeric(KeyValuePair<string, string> s)
        {
            try
            {
                return ((long)double.Parse(s.Value));
            }
            catch (Exception)
            {
                Console.WriteLine($"could not parse {s.Key} {s.Value}");
                throw;
            }
        }

        public void TestNewAuction(SaveAuction auction, bool triggerEvents = true)
        {
            var lookup = Lookups.GetOrAdd(auction.Tag, key => new PriceLookup());
            var l = lookup.Lookup;
            var cost = auction.StartingBid;
            var lbinPrice = auction.StartingBid * 1.03;
            var medPrice = auction.StartingBid * 1.05;
            var lastKey = new AuctionKey();
            var shouldTryToFindClosest = false;
            for (int i = 0; i < 5; i++)
            {
                var key = KeyFromSaveAuction(auction, i);
                if (i > 0 && key == lastKey)
                {
                    if (i < 4)
                        shouldTryToFindClosest = true;
                    continue; // already checked that
                }
                lastKey = key;

                if (!l.TryGetValue(key, out ReferenceAuctions bucket))
                {
                    if (triggerEvents && i == 4)
                    {
                        Console.WriteLine($"could not find bucket {key} for {auction.Tag} {l.Count} {auction.Uuid}");
                        if (this.State < SniperState.Ready)
                        {
                            if (auction.UId % 10 == 2)
                                Console.WriteLine($"closest is not available yet, state is {this.State}");
                            return;
                        }
                        var closests = FindClosest(l, key).Take(5).ToList();
                        foreach (var item in closests)
                        {
                            Console.WriteLine($"Closest bucket clean: {item.Key}");
                        }
                        if (!closests.Any())
                            return;
                        bucket = closests.FirstOrDefault().Value;
                        key = closests.FirstOrDefault().Key;
                        if (bucket.HitsSinceCalculating > 2)
                        {
                            Console.WriteLine($"Bucket {key} for {auction.Uuid} has been hit {bucket.HitsSinceCalculating} times, skipping");
                            TryFindClosestRisky(auction, l, ref lbinPrice, ref medPrice);
                            return;
                        }
                        bucket.HitsSinceCalculating++;
                        shouldTryToFindClosest = true;
                    }
                    else if (i != 0)
                        continue;
                    else
                        bucket = CreateAndAddBucket(auction);
                }
                if (bucket == null)
                {
                    Console.WriteLine("is null");
                }
                if (triggerEvents)
                {
                    long extraValue = GetExtraValue(auction, key);
                    FindFlip(auction, lbinPrice, medPrice, bucket, key, l, extraValue);
                }
                UpdateLbin(auction, bucket);
            }
            if (shouldTryToFindClosest && triggerEvents && this.State == SniperState.Ready)
            {
                TryFindClosestRisky(auction, l, ref lbinPrice, ref medPrice);
            }
        }

        private void TryFindClosestRisky(SaveAuction auction, ConcurrentDictionary<AuctionKey, ReferenceAuctions> l, ref double lbinPrice, ref double medPrice)
        {
            // special case for items that have no reference bucket, search using most similar
            var key = KeyFromSaveAuction(auction, 0);
            var closest = FindClosestTo(l, key);
            medPrice *= 1.10; // increase price a bit to account for the fact that we are not using the exact same item
            if (closest.Value == null)
                Logs.Enqueue(new LogEntry()
                {
                    Key = key,
                    LBin = -1,
                    Median = -1,
                    Uuid = auction.Uuid,
                    Volume = -1
                });
            else
            {
                if (closest.Key == key)
                    Console.WriteLine($"Found exact match for {key} {closest.Value.Volume} {auction.Uuid}");
                else
                    Console.WriteLine($"Would estimate closest to {key} {closest.Key} {auction.Uuid} for {closest.Value.Price}");
                if (closest.Value.Price > medPrice)
                {
                    var props = new Dictionary<string, string>() { { "closest", closest.Key.ToString() } };
                    var missingModifiers = closest.Key.Modifiers.Where(m => !key.Modifiers.Contains(m)).ToList();
                    long toSubstract = 0;
                    if (missingModifiers.Count > 0)
                    {
                        toSubstract = GetPriceSumForModifiers(missingModifiers, key.Modifiers);
                        props.Add("missingModifiers", string.Join(",", missingModifiers.Select(m => $"{m.Key}:{m.Value}")) + $" ({toSubstract})");
                    }
                    var missingEnchants = closest.Key.Enchants.Where(m => !key.Enchants.Contains(m)).ToList();
                    if (missingEnchants.Count > 0)
                    {
                        toSubstract += GetPriceSumForEnchants(missingEnchants);
                        props.Add("missingEnchants", string.Join(",", missingEnchants.Select(e => $"{e.Type}_{e.Lvl}")) + $" ({toSubstract})");
                    }
                    var targetPrice = (long)((closest.Value.Price - toSubstract) * 0.9);
                    FoundAFlip(auction, closest.Value, LowPricedAuction.FinderType.STONKS, targetPrice, props);
                }
            }
        }

        private long GetPriceSumForEnchants(List<Models.Enchantment> missingEnchants)
        {
            long toSubstract = 0;
            foreach (var item in missingEnchants)
            {
                if (Lookups.TryGetValue($"ENCHANTMENT_{item.Type}_{item.Lvl}".ToUpper(), out var enchantLookup))
                {
                    var prices = enchantLookup.Lookup.Values.First();
                    toSubstract += prices.Price;
                }
            }
            return toSubstract;
        }

        private long GetExtraValue(SaveAuction auction, AuctionKey key)
        {
            long extraValue = 0;
            foreach (var item in ItemKeys)
            {
                if (key.Modifiers.Any(m => m.Key == item))
                    continue;
                if (auction.FlatenedNBT.TryGetValue(item, out var value))
                {
                    if (!Lookups.TryGetValue(value.ToUpper(), out var itemLookup))
                        continue;
                    var prices = itemLookup.Lookup.Values.First();
                    extraValue += prices.Lbin.Price == 0 ? prices.Price : Math.Min(prices.Price, prices.Lbin.Price);
                }
            }
            var gemValue = 0L;
            foreach (var item in auction.FlatenedNBT)
            {
                if (item.Value == "PERFECT")
                    if (Lookups.TryGetValue($"PERFECT_{item.Key.Split('_').First()}_GEM", out var gemLookup) && !key.Modifiers.Any(m => m.Key == item.Key))
                        gemValue += gemLookup.Lookup.Values.First().Price - 500_000;
                if (item.Value == "FLAWLESS")
                    if (Lookups.TryGetValue($"FLAWLESS_{item.Key.Split('_').First()}_GEM", out var gemLookup) && !key.Modifiers.Any(m => m.Key == item.Key))
                        gemValue += gemLookup.Lookup.Values.First().Price - 100_000;
            }
            extraValue += gemValue;

            return extraValue;
        }

        private void FindFlip(SaveAuction auction, double lbinPrice, double minMedPrice, ReferenceAuctions bucket, AuctionKey key, ConcurrentDictionary<AuctionKey, ReferenceAuctions> l, long extraValue = 0)
        {
            var volume = bucket.Volume;
            var medianPrice = bucket.Price + extraValue;
            if (bucket.Lbin.Price > lbinPrice && (MaxMedianPriceForSnipe(bucket) > lbinPrice) && volume > 0.2f
               )// || bucket.Price == 0))
            {
                PotentialSnipe(auction, lbinPrice, bucket, key, l, extraValue);
            }
            if (medianPrice > minMedPrice && BucketHasEnoughReferencesForPrice(bucket))
            {
                long adjustedMedianPrice = CheckHigherValueKeyForLowerPrice(bucket, key, l, medianPrice);
                if (adjustedMedianPrice + extraValue < minMedPrice)
                {
                    LogNonFlip(auction, bucket, key, extraValue, volume, medianPrice);
                    return;
                }
                var props = CreateReference(bucket.References.Last().AuctionId, key, extraValue);
                props["med"] = string.Join(',', bucket.References.Reverse().Take(10).Select(a => AuctionService.Instance.GetUuid(a.AuctionId)));
                FoundAFlip(auction, bucket, LowPricedAuction.FinderType.SNIPER_MEDIAN, adjustedMedianPrice + extraValue, props);
            }
            else
            {
                LogNonFlip(auction, bucket, key, extraValue, volume, medianPrice);
            }

            void LogNonFlip(SaveAuction auction, ReferenceAuctions bucket, AuctionKey key, long extraValue, float volume, long medianPrice)
            {
                if (auction.UId % 10 == 0)
                    Console.Write("p");
                if (volume == 0 || bucket.Lbin.Price == 0 || bucket.Price == 0 || bucket.Price > MIN_TARGET)
                    Logs.Enqueue(new LogEntry()
                    {
                        Key = key.ToString() + $"+{extraValue}",
                        LBin = bucket.Lbin.Price,
                        Median = medianPrice,
                        Uuid = auction.Uuid,
                        Volume = bucket.Volume
                    });
                if (Logs.Count > 2000)
                    PrintLogQueue();
            }
        }

        /// <summary>
        /// Checks higher value keys for a lower median price
        /// </summary>
        /// <param name="bucket"></param>
        /// <param name="key"></param>
        /// <param name="l"></param>
        /// <param name="medianPrice"></param>
        /// <returns></returns>
        private long CheckHigherValueKeyForLowerPrice(ReferenceAuctions bucket, AuctionKey key, ConcurrentDictionary<AuctionKey, ReferenceAuctions> l, long medianPrice)
        {
            var higherValueLowerPrice = HigherValueKeys(key, l, medianPrice).Select(k =>
            {
                if (l.TryGetValue(k, out ReferenceAuctions altBucket))
                {
                    if (altBucket.Price != 0)
                        return altBucket.Price;
                }
                return long.MaxValue;
            }).DefaultIfEmpty(long.MaxValue).Min();
            var adjustedMedianPrice = Math.Min(bucket.Price, higherValueLowerPrice);
            return adjustedMedianPrice;
        }

        private static bool BucketHasEnoughReferencesForPrice(ReferenceAuctions bucket)
        {
            // high value items need more volume to pop up
            return bucket.Price < 200_000_000 || bucket.References.Count > 5;
        }

        public void UpdateBazaar(dev.BazaarPull bazaar)
        {
            foreach (var item in bazaar.Products)
            {
                if (item.SellSummary.FirstOrDefault()?.PricePerUnit < 1_000_000)
                    continue;
                if (!Lookups.TryGetValue(item.ProductId, out var lookup))
                {
                    lookup = new();
                    Lookups[item.ProductId] = lookup;
                    Console.WriteLine($"Added {item.ProductId} to lookup");
                }
                var refernces = lookup.Lookup.GetOrAdd(defaultKey, _ => new());
                if (item.SellSummary.Any())
                    refernces.Price = (long)item.SellSummary.First().PricePerUnit;
            }
            Console.WriteLine($"Updated bazaar {Lookups.Count} items");
        }

        private void PotentialSnipe(SaveAuction auction, double lbinPrice, ReferenceAuctions bucket, AuctionKey key, ConcurrentDictionary<AuctionKey, ReferenceAuctions> l, long extraValue)
        {
            var higherValueLowerBin = bucket.Lbin.Price;
            if (HigherValueKeys(key, l, lbinPrice).Any(k =>
            {
                if (l.TryGetValue(k, out ReferenceAuctions altBucket))
                {
                    if (altBucket.Lbin.Price != 0 && altBucket.Lbin.Price < lbinPrice)
                    {
                        return true;
                    }
                    if (altBucket.Lbin.Price != 0 && altBucket.Lbin.Price < higherValueLowerBin)
                        higherValueLowerBin = altBucket.Lbin.Price;// cheaper lbin found
                }
                return false;
            }))
                return;
            var props = CreateReference(bucket.Lbin.AuctionId, key, extraValue);
            props["med"] = string.Join(',', bucket.References.Reverse().Take(10).Select(a => AuctionService.Instance.GetUuid(a.AuctionId)));
            props["mVal"] = bucket.Price.ToString();
            var targetPrice = Math.Min(higherValueLowerBin, MaxMedianPriceForSnipe(bucket)) + extraValue;
            if (targetPrice < auction.StartingBid * 1.03)
                return;
            FoundAFlip(auction, bucket, LowPricedAuction.FinderType.SNIPER, targetPrice, props);
        }

        private static long MaxMedianPriceForSnipe(ReferenceAuctions bucket)
        {
            return bucket.Price * 11 / 10;
        }

        public void PrintLogQueue()
        {
            while (Logs.TryDequeue(out LogEntry result))
            {
                var finderName = result.Finder == LowPricedAuction.FinderType.UNKOWN ? "NF" : result.Finder.ToString();
                Console.WriteLine($"Info: {finderName} {result.Uuid} {result.Median} \t{result.LBin} {result.Volume} {result.Key}");
            }
        }

        private void UpdateLbin(SaveAuction auction, ReferenceAuctions bucket)
        {
            LbinUpdates.Enqueue((auction, bucket));
        }

        private void FoundAFlip(SaveAuction auction, ReferenceAuctions bucket, LowPricedAuction.FinderType type, long targetPrice, Dictionary<string, string> props)
        {
            if (targetPrice < MIN_TARGET)
                return; // to low
            var refAge = (GetDay() - bucket.OldestRef);
            if (refAge > 60)
                return; // too old
            props["refAge"] = refAge.ToString();
            FoundSnipe?.Invoke(new LowPricedAuction()
            {
                Auction = auction,
                Finder = type,
                TargetPrice = targetPrice,
                DailyVolume = bucket.Volume,
                AdditionalProps = props
            });
            Logs.Enqueue(new LogEntry()
            {
                Key = props.GetValueOrDefault("key"),
                LBin = bucket.Lbin.Price,
                Median = bucket.Price,
                Uuid = auction.Uuid,
                Volume = bucket.Volume,
                Finder = type
            });
        }

        private static Dictionary<string, string> CreateReference(long reference, AuctionKey key, long extraValue = 0)
        {
            var dict = new Dictionary<string, string>() {
                { "reference", AuctionService.Instance.GetUuid(reference) },
                { "key", key.ToString() + (extraValue == 0 ? "" : $" +{extraValue}")}
            };
            if (extraValue != 0)
                dict["extraValue"] = extraValue.ToString();
            return dict;
        }
    }
}
