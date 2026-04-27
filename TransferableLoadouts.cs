using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using ReLogic.Content;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.Default;
using Terraria.ModLoader.IO;
using Terraria.UI;

namespace TransferableLoadouts
{
    public class TransferableLoadouts : Mod
    {
        /// <summary>
        /// <see cref="ItemSlot"/> contexts indicating that this is an equipment slot
        /// </summary>
        public static readonly int[] EquipSlots = [
            ItemSlot.Context.EquipArmor,
            ItemSlot.Context.EquipArmorVanity,
            ItemSlot.Context.EquipAccessory,
            ItemSlot.Context.EquipAccessoryVanity,
            ItemSlot.Context.EquipDye,

            ItemSlotContext_ModAccessorySlot_EquipAccessory,
            ItemSlotContext_ModAccessorySlot_EquipAccessoryVanity,
            ItemSlotContext_ModAccessorySlot_EquipDye
        ];

        public const int ItemSlotContext_ModAccessorySlot_EquipAccessory = -10;
        public const int ItemSlotContext_ModAccessorySlot_EquipAccessoryVanity = -11;
        public const int ItemSlotContext_ModAccessorySlot_EquipDye = -12;

        public override void Load()
        {
            // The type that contains the field we want to change.
            var itemSlotType = typeof(ItemSlot);

            // Get the FieldInfo for the private static field "canFavoriteAt".
            // We use BindingFlags to find non-public (private) and static members.
            FieldInfo canFavoriteAtField = itemSlotType.GetField("canFavoriteAt", BindingFlags.NonPublic | BindingFlags.Static);

            // It's good practice to check if the field was found.
            // This prevents your mod from crashing if a future game update renames or removes the field.
            if (canFavoriteAtField != null)
            {
                // Get the actual boolean array from the field.
                // For static fields, the first argument of GetValue is always null.
                bool[] canFavoriteAt = (bool[])canFavoriteAtField.GetValue(null);

                // Now, modify the array to allow favoriting in equipment slots.
                // Using the ItemSlot.Context constants makes the code readable and safe from number changes.
                canFavoriteAt[ItemSlot.Context.EquipArmor] = true;
                canFavoriteAt[ItemSlot.Context.EquipArmorVanity] = true;
                canFavoriteAt[ItemSlot.Context.EquipAccessory] = true;
                canFavoriteAt[ItemSlot.Context.EquipAccessoryVanity] = true;
                canFavoriteAt[ItemSlot.Context.EquipDye] = true;

                // Since we modified the array object directly, we don't need to call SetValue.
                // The changes are already applied to the game's instance of the array.

                Logger.Info("Successfully modified ItemSlot.canFavoriteAt using Reflection.");
            }
            else
            {
                Logger.Error("Could not find field 'canFavoriteAt' in Terraria.UI.ItemSlot. This may be due to a game update.");
            }

            // We edit the same method as Extra Equipment Loadouts, so we try to register our callback with them to avoid any possible incompatabilities
            // This also causes our callback to be called when switching to and from loadouts added by them, which would not happen if we only edited the Vanilla method
            if (LoadoutHelper.Advanced.RegisterPostSwapCallback((player, _, _) => CopyLoadoutFavorites(player)))
            {
                Logger.Info("Registered CopyLoadoutFavorites() with Extra Equipment Loadouts.");
            }
            else
            {
                IL_Player.TrySwitchingLoadout += TrySwitchingLoadout_CallCopyFavorites;
                Logger.Info("Inserted CopyLoadoutFavorites() call into Player::TrySwitchingLoadout().");
            }


            On_ItemSlot.Draw_SpriteBatch_ItemArray_int_int_Vector2_Color += DrawWithFavoritedOverlay;
            On_ItemSlot.LeftClick_ItemArray_int_int += DontUnfavorite1;//WhenLeftClickEquipping;
            On_ItemSlot.ArmorSwap += DontUnfavorite2; //whenrightClickingInventoryItemToArmor"
            //On_ItemSlot.EquipSwap += DontUnfavorite3; // whenrightclickinginventoryitemto"BonusEquips"
        }

        private void TrySwitchingLoadout_CallCopyFavorites(ILContext il)
        {
            ILCursor c = new(il);

            if (!c.TryGotoNext(MoveType.After, instr => instr.MatchCall(typeof(PlayerLoader), nameof(PlayerLoader.OnEquipmentLoadoutSwitched))))
            {
                throw new Exception("Failed while patching Player::TrySwitchingLoadout(): could not match (call PlayerLoader::OnEquipmentLoadoutSwitched)");
            }

            // After all the code for loadout switching as run, we need to copy over favorited items

            c.Emit(OpCodes.Ldarg_0);                // Load the Player object that this method is being called on onto the stack
            c.EmitDelegate(CopyLoadoutFavorites);   // Emit a call to CopyLoadoutFavoties, passing in the Player object from the top of the stack 
        }

        //TODO: possible design solution: only favorite one accessory per slot, or first loadout only. but then what if you have, e.g, wings,
        //but you want to replace the wings with boots in the second slot if they're incompatible? just dont do that and use the same slot for wings always, i guess

        /// <summary>
        /// <para>Copies the favorited item from the same slot in the earliest loadout for every empty slot in the current loadout.</para>
        /// <para>Must be called every time loadouts switch.</para>
        /// </summary>
        public void CopyLoadoutFavorites(Player player)
        {
            int numLoadouts = LoadoutHelper.TotalLoadouts();

            // Fill empty slots with first valid favorited items
            for (int i = 0; i < player.armor.Length; i++)
            {
                if (!player.armor[i].IsAir) continue;

                for (int j = 0; j < numLoadouts; j++)
                {
                    EquipmentLoadout loadout = LoadoutHelper.GetLoadout(player, j);
                    Item potentialItem = loadout.Armor[i];
                    if (potentialItem.IsAir || !potentialItem.favorited) continue;

                    bool vanity = i >= 10;
                    bool canEquip = true;

                    if (potentialItem.accessory && !IsAccessoryCompatible(player, potentialItem, vanity))
                        canEquip = false;

                    if (canEquip)
                    {
                        // Swapping the item rather than cloning it create ghost copies of the item for some reason, so don't do that
                        player.armor[i] = potentialItem.Clone();
                        potentialItem.TurnToAir(true);
                        break;
                    }
                }
            }

            // Same for dye
            for (int i = 0; i < player.dye.Length; i++)
            {
                if (!player.armor[i].IsAir) continue;

                for (int j = 0; j < numLoadouts; j++)
                {
                    EquipmentLoadout loadout = LoadoutHelper.GetLoadout(player, j);
                    Item potentialDye = loadout.Dye[i];
                    if (potentialDye.IsAir || !potentialDye.favorited) continue;

                    player.dye[i] = potentialDye.Clone();
                    potentialDye.TurnToAir(true);
                    break;
                }
            }

            var modPlayer = player.GetModPlayer<ModAccessorySlotPlayer>();
            var slotCount = modPlayer.SlotCount;

            for (int i = 0; i < slotCount; i++)
            {
                var accessorySlot = LoaderManager.Get<AccessorySlotLoader>().Get(i);

                if (accessorySlot.FunctionalItem.IsAir)
                {
                    for (int j = 0; j < numLoadouts; j++)
                    {
                        var loadout = LoadoutHelper.Advanced.GetModLoaderLoadoutSlots(player, j);
                        Item potentialItem = loadout.Items[i];
                        if (potentialItem.IsAir || !potentialItem.favorited) continue;

                        bool canEquip = true;

                        if (potentialItem.accessory && !IsAccessoryCompatible(player, potentialItem, false))
                            canEquip = false;

                        if (canEquip)
                        {
                            accessorySlot.FunctionalItem = potentialItem.Clone();
                            potentialItem.TurnToAir(true);
                            break;
                        }
                    }
                }

                if (accessorySlot.VanityItem.IsAir)
                {
                    for (int j = 0; j < numLoadouts; j++)
                    {
                        var loadout = LoadoutHelper.Advanced.GetModLoaderLoadoutSlots(player, j);
                        int vanityOffset = loadout.Items.Length / 2;
                        Item potentialItem = loadout.Items[i + vanityOffset];
                        if (potentialItem.IsAir || !potentialItem.favorited) continue;

                        bool canEquip = true;

                        if (potentialItem.accessory && !IsAccessoryCompatible(player, potentialItem, true))
                            canEquip = false;

                        if (canEquip)
                        {
                            accessorySlot.VanityItem = potentialItem.Clone();
                            potentialItem.TurnToAir(true);
                            break;
                        }
                    }
                }

                if (accessorySlot.DyeItem.IsAir)
                {
                    for (int j = 0; j < numLoadouts; j++)
                    {
                        var loadout = LoadoutHelper.Advanced.GetModLoaderLoadoutSlots(player, j);
                        Item potentialItem = loadout.Dye[i];
                        if (potentialItem.IsAir || !potentialItem.favorited) continue;

                        accessorySlot.DyeItem = potentialItem.Clone();
                        potentialItem.TurnToAir(true);
                        break;
                    }
                }
            }
        }

        private void DontUnfavorite1(On_ItemSlot.orig_LeftClick_ItemArray_int_int orig, Item[] inv, int context, int slot)
        {
            Item oldMouseItem = Main.mouseItem.Clone();
            bool triedToEquipItem = !Main.mouseItem.IsAir && Main.mouseLeftRelease && Main.mouseLeft && Main.cursorOverride == -1
                && EquipSlots.Contains(context);

            bool mouseItemWasFavorited = Main.mouseItem.favorited;
            bool slotItemWasFavorited = inv[slot].favorited;

            orig(inv, context, slot);

            if (triedToEquipItem) //vanilla tries to unfavorite even if it doesn't and can't actually equip (e.g, wrong slot)
            {
                bool failedToEquip = oldMouseItem.IsTheSameAs(Main.mouseItem) && !Main.mouseItem.IsTheSameAs(inv[slot]); //sort of fragile, assumes swaps of same item type will always work
                if (failedToEquip)
                {
                    Main.mouseItem.favorited = mouseItemWasFavorited;
                    inv[slot].favorited = slotItemWasFavorited;
                }
                else
                    inv[slot].favorited = mouseItemWasFavorited;
            } //we love using detours instead of IL
        }

        private Item DontUnfavorite2(On_ItemSlot.orig_ArmorSwap orig, Item item, out bool success)
        {
            bool wasFavorited = item.favorited;
            Item result = orig(item, out success);
            if (success && wasFavorited)
            {
                if (Main.LocalPlayer.armor.FirstOrDefault(item.IsTheSameAs) is Item equippedItem)
                    equippedItem.favorited = true;

                var modLoaderSlots = LoadoutHelper.Advanced.GetModLoaderCurrentSlots(Main.LocalPlayer);
                if (modLoaderSlots.Items.FirstOrDefault(item.IsTheSameAs) is Item mEquippedItem)
                    mEquippedItem.favorited = true;
            }
            return result;
        }

        private void DrawWithFavoritedOverlay(On_ItemSlot.orig_Draw_SpriteBatch_ItemArray_int_int_Vector2_Color orig, SpriteBatch spriteBatch, Item[] inv, int context, int slot, Vector2 position, Color lightColor)
        {
            orig(spriteBatch, inv, context, slot, position, lightColor);
            if (EquipSlots.Contains(context) && inv[slot].favorited)
            {
                inv[slot].favorited = false;
                orig(spriteBatch, inv, context, slot, position, lightColor);
                inv[slot].favorited = true; //Could This Be The Hackiest Code Of All Time?

                // We need to take the absolute value of context because tModLoader accessory slots are negative
                Color borderColor = ItemSlot.GetColorByLoadout(slot, int.Abs(context)).Brighten(1.6f);
                Texture2D tex = ModContent.Request<Texture2D>("TransferableLoadouts/Inventory_Back13_FavoriteOverlay", AssetRequestMode.ImmediateLoad).Value;
                spriteBatch.Draw(tex, position, null, borderColor, 0f, default, Main.inventoryScale, SpriteEffects.None, 0f);
            }
            //else
            //    orig(spriteBatch, inv, context, slot, position, lightColor);
        }

        /// <summary>
        /// Whether an equippable accessory can be put into a specific slot.
        /// This method assumes that the item is allowed to be generally equipped, since it is used on already equipped accessories.
        /// </summary>
        /// <param name="player">The player instance.</param>
        /// <param name="favoriteItem">The favorited accessory we are trying to equip.</param>
        /// <param name="vanity">Whether we're trying to place the item in an vanity slot or a functional slot.</param>
        /// <returns>True if the item can be equipped with the player's other equipment, false otherwise.</returns>
        private bool IsAccessoryCompatible(Player player, Item favoriteItem, bool vanity)
        {
            //NO accessory can be a duplicate, vanity or not
            for (int i = 0; i < player.armor.Length; i++)
            {
                if (!player.armor[i].IsAir && favoriteItem.IsTheSameAs(player.armor[i]))
                    return false;
            }

            int firstAccessoryIndex = (vanity ? 13 : 3);
            int lastAccessoryIndex = (vanity ? 19 : 9);

            //items of the same group (vanity/functional) can't both be wings/other modded restrictions
            for (int i = firstAccessoryIndex; i <= lastAccessoryIndex; i++)
            {
                Item equippedItem = player.armor[i];
                if (equippedItem.IsAir)
                    continue;

                // Can't equip two wings
                if (favoriteItem.wingSlot > 0 && equippedItem.wingSlot > 0)
                    return false;

                // General compatibility hook
                if (!ItemLoader.CanAccessoryBeEquippedWith(equippedItem, favoriteItem))
                    return false;
            }
            return true;
        }
    }

    public class TooltipChange : GlobalItem
    {
        //public static Item hoverItem;
        public bool worn = false;
        public override bool InstancePerEntity => true;

        public override void ModifyTooltips(Item item, List<TooltipLine> tooltips)
        {
            for (int i = 0; i < tooltips.Count; i++)
            {
                TooltipLine line = tooltips[i];

                if (line.Name == "FavoriteDesc" && worn)
                    tooltips[i] = new TooltipLine(Mod, "FavoriteEquipDesc", Language.GetText("Mods.TransferableLoadouts.FavoriteEquipDesc").Value);
            }
        }
    }

    public class HoverItemTracker : ModSystem
    {
        public override void Load()
        {
            On_ItemSlot.MouseHover_ItemArray_int_int += GetHoverIndex;
        }

        //inv can be inventory, armor, shop, dye or hover item, or chest. we gotta use context.
        private void GetHoverIndex(On_ItemSlot.orig_MouseHover_ItemArray_int_int orig, Item[] inv, int context, int slot)
        {
            orig(inv, context, slot);
            if (context is ItemSlot.Context.EquipArmor or ItemSlot.Context.EquipAccessory or ItemSlot.Context.EquipArmorVanity or ItemSlot.Context.EquipAccessoryVanity or ItemSlot.Context.EquipDye
                && inv[slot].TryGetGlobalItem<TooltipChange>(out var global))
                global.worn = true;
        }
    }

    public class SaveFavoritedItems : ModPlayer //messing with vanilla I/O seems like a horrible idea
    {
        public const int CurrentDataVersion = 1;
        public const string DataVersionKey = "version";
        public const string DataTagKey = "data";

        public const string VanillaLoadoutKey = "vanilla";
        public const string ModLoaderLoadoutKey = "modLoader";

        public const string LoadoutKeyPrefix = "loadout";

        public const string VanillaArmorKey = "armor";
        public const string VanillaDye = "dye";

        public const string ModLoaderFunctionalKey = "functional";
        public const string ModLoaderVanityKey = "vanity";
        public const string ModLoaderDyeKey = "dye";

        // ModAccessorySlotPlayer::slots stores all slots on the player, which is not necessarily just the loaded slots! It also includes unloaded slots that still have items
        // There is no other way to access this data, so we need to get it via reflection
        private static readonly FieldInfo F_ModAccessorySlotPlayer_slots = typeof(ModAccessorySlotPlayer).GetField("slots", BindingFlags.NonPublic | BindingFlags.Instance);

        public override void SaveData(TagCompound tag)// this WILL run when the game autosaves! it's not the same as onworldunload!
        {
            tag[DataVersionKey] = CurrentDataVersion;
            var saveData = new TagCompound();

            int numLoadouts = LoadoutHelper.TotalLoadouts();
            var modLoaderSlots = F_ModAccessorySlotPlayer_slots?.GetValue(Player.GetModPlayer<ModAccessorySlotPlayer>()) as Dictionary<string, int>;

            if (modLoaderSlots is null)
            {
                Mod.Logger.Error("Could not access ModAccessorySlotPlayer::slots. Modded accessory slots cannot be saved");
            }

            for (int i = 0; i < numLoadouts; i++)
            {
                var loadoutTag = new TagCompound();

                var (vanillaArmor, vanillaDye) = GetVanillaItemsForLoadout(i);
                var modLoaderItems = GetModLoaderItemsForLoadout(i);

                var vanillaFavArmor = vanillaArmor.Select(item => item.favorited).ToList();
                var vanillaFavDye = vanillaDye.Select(item => item.favorited).ToList();
                loadoutTag[VanillaLoadoutKey] = new TagCompound()
                {
                    { VanillaArmorKey, vanillaFavArmor },
                    { VanillaDye, vanillaFavDye },
                };

                if (modLoaderSlots is null)
                {
                    continue;
                }

                var modLoaderTag = new TagCompound();

                foreach ((var slotName, int index) in modLoaderSlots)
                {
                    modLoaderTag[slotName] = new TagCompound()
                    {
                        { ModLoaderFunctionalKey, modLoaderItems.FunctionalItems[index].favorited },
                        { ModLoaderVanityKey, modLoaderItems.VanityItems[index].favorited },
                        { ModLoaderDyeKey, modLoaderItems.Dye[index].favorited }
                    };
                }

                loadoutTag[ModLoaderLoadoutKey] = modLoaderTag;

                saveData[LoadoutKeyPrefix + i] = loadoutTag;
            }

            tag[DataTagKey] = saveData;
        }

        public override void LoadData(TagCompound tag)
        {
            // If there isn't a data version saved, that indicates that this save file was made before save data versioning as implemented
            if (!tag.TryGet<int>(DataVersionKey, out var savedDataVersion))
            {
                LoadData_0(tag);
                return;
            }

            if (!tag.TryGet<TagCompound>(DataTagKey, out var dataTag))
            {
                // This should never happen, but we want to catch this case and log an error in case it does
                Mod.Logger.Error($"while loading player {Player.name}: tag contains data version ({savedDataVersion}) but no data tag!");
                return;
            }

            switch (savedDataVersion)
            {
                case 1:
                    LoadData_1(dataTag);
                    break;
                default:
                    Mod.Logger.Error($"while loading player {Player.name}: unsupported data version {savedDataVersion}!");
                    break;
            }
        }

        private void LoadData_1(TagCompound dataTag)
        {
            void ApplyFavoriteList(IList<bool> favorited, Item[] toItemArray)
            {
                if (favorited.Count != toItemArray.Length)
                {
                    Mod.Logger.Warn($"Applying a favorite list to an item array of a different length (favorite list: {favorited.Count},  item array:{toItemArray.Length})");
                }

                int len = int.Min(favorited.Count, toItemArray.Length);
                for (int i = 0; i < len; i++)
                {
                    toItemArray[i].favorited = favorited[i];
                }
            }

            // Only try to load as many loadouts as currently exist
            int numLoadouts = LoadoutHelper.TotalLoadouts();
            var modLoaderSlots = F_ModAccessorySlotPlayer_slots?.GetValue(Player.GetModPlayer<ModAccessorySlotPlayer>()) as Dictionary<string, int>;

            if (modLoaderSlots is null)
            {
                Mod.Logger.Error("Could not access ModAccessorySlotPlayer::slots. Modded accessory slots cannot be saved");
            }

            for (int i = 0; i < numLoadouts; i++)
            {
                if (!dataTag.TryGet<TagCompound>(LoadoutKeyPrefix + i, out var loadoutTag))
                {
                    continue;
                }

                var (vanillaArmor, vanillaDye) = GetVanillaItemsForLoadout(i);
                var modLoaderItems = GetModLoaderItemsForLoadout(i);

                // Load vanilla loadout favourites
                var vanillaTag = loadoutTag.Get<TagCompound>(VanillaLoadoutKey);
                var vanillaFavArmor = vanillaTag.GetList<bool>(VanillaArmorKey);
                var vanillaFavDye = vanillaTag.GetList<bool>(VanillaDye);


                ApplyFavoriteList(vanillaFavArmor, vanillaArmor);
                ApplyFavoriteList(vanillaFavDye, vanillaDye);

                if (modLoaderSlots is null)
                {
                    continue;
                }

                // Load ModAccessorySlot loadout favourites
                var modLoaderTag = loadoutTag.Get<TagCompound>(ModLoaderLoadoutKey);

                foreach ((var fullName, var tagObj) in modLoaderTag)
                {
                    var tag = tagObj as TagCompound;
                    int slotIndex;

                    // Try to look up loaded slots with ModContent instead of searching ModAccessorySlotPlayer.slots right away, to account for legacy names
                    if (ModContent.TryFind<ModAccessorySlot>(fullName, out var slot))
                    {
                        slotIndex = slot.Type;
                    }
                    // Unloaded slots are tracked in ModAccessorySlotPlayer.slots, so we get their index from there
                    else if (modLoaderSlots.TryGetValue(fullName, out var index))
                    {
                        slotIndex = index;
                    }
                    else
                    {
                        // The slot is not currently loaded and the tML system that keeps track of unloaded slots with saved data doesn't recognize it,
                        // so we're screwed.
                        Mod.Logger.Error($"while loading ModAccessorySlot favorites: saved slot with full name \"{fullName}\" is neither loaded nor a known unloaded slot");
                        continue;
                    }

                    modLoaderItems.FunctionalItems[slotIndex].favorited = tag.Get<bool>(ModLoaderFunctionalKey);
                    modLoaderItems.VanityItems[slotIndex].favorited = tag.Get<bool>(ModLoaderVanityKey);
                    modLoaderItems.Dye[slotIndex].favorited = tag.Get<bool>(ModLoaderDyeKey);
                }
            }
        }

        private void LoadData_0(TagCompound tag)
        {
            Item[] GetAllEquipsForAllLoadouts()
            {
                List<Item> allEquips = [];
                for (int i = 0; i < Player.Loadouts.Length; i++)
                {
                    if (LoadoutHelper.CurrentLoadoutIndex(Player) == i)
                    {
                        allEquips.AddRange(Player.armor);
                        allEquips.AddRange(Player.dye);
                    }
                    else
                    {
                        allEquips.AddRange(Player.Loadouts[i].Armor);
                        allEquips.AddRange(Player.Loadouts[i].Dye);
                    }
                }
                return allEquips.ToArray();
            }

            var isFavoritedList = tag.GetList<bool>("favoritedEquips");
            Item[] allEquips = GetAllEquipsForAllLoadouts();
            for (int i = 0; i < allEquips.Length && i < isFavoritedList.Count; i++)
            {
                if (allEquips[i] is null || allEquips[i].IsAir)
                    continue;
                allEquips[i].favorited = isFavoritedList[i];
            }
        }

        public (Item[] items, Item[] dye) GetVanillaItemsForLoadout(int loadoutIndex)
        {
            if (loadoutIndex == LoadoutHelper.CurrentLoadoutIndex(Player))
            {
                return (Player.armor, Player.dye);
            }
            else
            {
                var loadout = LoadoutHelper.GetLoadout(Player, loadoutIndex);
                return (loadout.Armor, loadout.Dye);
            }
        }

        public LoadoutHelper.Advanced.IModLoaderSlotsView GetModLoaderItemsForLoadout(int loadoutIndex)
        {
            if (loadoutIndex == LoadoutHelper.CurrentLoadoutIndex(Player))
            {
                return LoadoutHelper.Advanced.GetModLoaderCurrentSlots(Player);
            }
            else
            {
                return LoadoutHelper.Advanced.GetModLoaderLoadoutSlots(Player, loadoutIndex);
            }
        }
    }

    public static class Utils
    {
        public static bool IsTheSameAs(this Item item, Item compareItem)
        {
            if (item.netID == compareItem.netID)
            {
                return item.type == compareItem.type;
            }
            return false;
        }

        /// <summary>
        /// Returns a new color, with each RGB component multiplied by the factor and capped at 255.
        /// </summary>
        public static Color Brighten(this Color color, float factor)
        {
            // factor > 1.0 makes it brighter, e.g. 1.2f = 20% brighter
            int r = (int)(color.R * factor);
            int g = (int)(color.G * factor);
            int b = (int)(color.B * factor);

            // clamp to 255
            r = Math.Min(255, r);
            g = Math.Min(255, g);
            b = Math.Min(255, b);

            return new Color(r, g, b, color.A);
        }
    }
}
