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

        private ILHook Hook_ModAccessorySlotPlayer_OnEquipmentLoadoutSwitched;

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

                //Second page of equips
                //canFavoriteAt[ItemSlot.Context.EquipGrapple] = true;
                //canFavoriteAt[ItemSlot.Context.EquipMount] = true;
                //canFavoriteAt[ItemSlot.Context.EquipMinecart] = true;
                //canFavoriteAt[ItemSlot.Context.EquipPet] = true;
                //canFavoriteAt[ItemSlot.Context.EquipLight] = true;

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
            if (LoadoutHelper.Advanced.RegisterPostSwapCallback((player, _, _) => { CopyLoadoutFavoritesForVanillaSlots(player); CopyLoadoutFavoritesForModLoaderSlots(player); }))
            {
                Logger.Info("Registered CopyLoadoutFavoritesForVanillaSlots() and CopyLoadoutFavoritesForModLoaderSlots() with Extra Equipment Loadouts.");
            }
            else
            {
                IL_Player.TrySwitchingLoadout += TrySwitchingLoadout_CallCopyFavorites;
                Logger.Info("Inserted CopyLoadoutFavoritesForVanillaSlots() call into Player::TrySwitchingLoadout().");

                // We have to use the ILHook class because we are patching an internal tModLoader class which they do not hookgen IL_* classes for
                Hook_ModAccessorySlotPlayer_OnEquipmentLoadoutSwitched = new ILHook(typeof(ModAccessorySlotPlayer).GetMethod(nameof(ModAccessorySlotPlayer.OnEquipmentLoadoutSwitched)), OnEquipmentLoadoutSwitched_CallCopyFavorites);
                Logger.Info("Inserted CopyLoadoutFavoritesForModLoaderSlots() call into ModAccessorySlotPlayer::OnEquipmentLoadoutSwitched().");
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

            // This should move us to right after loadouts have been swapped and the players loadout index has been updated

            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate(CopyLoadoutFavoritesForVanillaSlots);
        }

        private void OnEquipmentLoadoutSwitched_CallCopyFavorites(ILContext il)
        {
            ILCursor c = new(il);

            if (!c.TryGotoNext(MoveType.After, instr => instr.MatchCall(typeof(ModAccessorySlotPlayer), "DetectConflictsWithSharedSlots")))
            {
                throw new Exception("Failed while patching ModAccessorySlotPlayer::OnEquipmentLoadoutSwitched(): could not match (call ModAccessorySlotPlayer::DetectConflictsWithSharedSlots)");
            }

            // This moves us to immediately after tML has swapped items for its loadouts

            c.Emit(OpCodes.Ldarg_0);
            c.EmitCall(typeof(ModPlayer).GetProperty(nameof(ModPlayer.Player)).GetAccessors()[0]);
            c.EmitDelegate(CopyLoadoutFavoritesForModLoaderSlots);
        }

        /// <summary>
        /// <para>Copies the favorited item from the same slot in the earliest loadout for every empty slot in the current loadout.</para>
        /// <para>Must be called every time loadouts switch.</para>
        /// </summary>
        public void CopyLoadoutFavoritesForVanillaSlots(Player player)
        {
            int numLoadouts = LoadoutHelper.TotalLoadouts();

            /* ---- VANILLA LOADOUTS ---- */
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
        }

        /// <summary>
        /// <para>Copies the favorited item from the same ModAccessorySlot in the earliest loadout for every empty ModAccessorySlot in the current loadout.</para>
        /// <para>Must be called every time loadouts switch.</para>
        /// </summary>
        public void CopyLoadoutFavoritesForModLoaderSlots(Player player)
        {
            int numLoadouts = LoadoutHelper.TotalLoadouts();

            /* ---- MODDED LOADOUTS ---- */
            var modPlayer = player.GetModPlayer<ModAccessorySlotPlayer>();
            // tML creates unloaded slot entries at the end of the array so that players don't loose their items when they disable a mod that adds accessory slots
            // For consistency, we only want to deal with loaded slots.
            var loadedSlotCount = modPlayer.LoadedSlotCount;

            // TODO cleanup/consolodate
            for (int i = 0; i < modPlayer.LoadedSlotCount; i++)
            {
                var accessorySlot = LoaderManager.Get<AccessorySlotLoader>().Get(i);

                if (accessorySlot.FunctionalItem.IsAir)
                {
                    for (int j = 0; j < numLoadouts; j++)
                    {
                        var loadout = LoadoutHelper.Advanced.GetModLoaderLoadout(player, j);
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
                        var loadout = LoadoutHelper.Advanced.GetModLoaderLoadout(player, j);
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
                        var loadout = LoadoutHelper.Advanced.GetModLoaderLoadout(player, j);
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
                    Main.LocalPlayer.armor.First(item.IsTheSameAs).favorited = true;

                // todo same but for modded
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
            //orig(spriteBatch, inv, context, slot, position, lightColor);
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

                if (line.Name == "FavoriteDesc" && worn)//(item.wornArmor || worn))
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
        public override void LoadData(TagCompound tag)
        {
            var isFavoritedList = tag.GetList<bool>("favoritedEquips");
            Item[] allEquips = GetAllEquipsForAllLoadouts();
            for (int i = 0; i < allEquips.Length && i < isFavoritedList.Count; i++)
            {
                if (allEquips[i] is null || allEquips[i].IsAir)
                    continue;
                allEquips[i].favorited = isFavoritedList[i];
            }
        }
        public override void SaveData(TagCompound tag)// this WILL run when the game autosaves! it's not the same as onworldunload!
        {
            var list = new List<bool>();
            foreach (Item equip in GetAllEquipsForAllLoadouts())
            {
                list.Add(equip.favorited);
            }
            tag["favoritedEquips"] = list;
        }

        // TODO Save favorited items in extra loadouts and mod accessory slots
        public Item[] GetAllEquipsForAllLoadouts()
        {
            List<Item> allEquips = [];
            for (int i = 0; i < Player.Loadouts.Length; i++)
            {
                if (Player.CurrentLoadoutIndex == i)
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
        public static string ContextToString(int context)
        {
            // Get all public constant fields in ItemSlot.Context
            var fields = typeof(ItemSlot.Context).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            foreach (var field in fields)
            {
                if (field.IsLiteral && !field.IsInitOnly)
                {
                    var fieldValue = (int)field.GetRawConstantValue();
                    if (fieldValue == context)
                        return field.Name;
                }
            }

            return $"Unknown({context})";
        }
    }
}

//    private void TrySwitchingLoadoutWithFavorites(On_Player.orig_TrySwitchingLoadout orig, Player self, int loadoutIndex)
//    {
//        // --- PRE-SWITCH CHECKS (Similar to vanilla) ---
//        bool isPlayerBusy = self.itemTime > 0 || self.itemAnimation > 0;
//        if (self.whoAmI != Main.myPlayer || isPlayerBusy || self.CCed || self.dead)
//        {
//            return; // Player is busy, cannot switch
//        }

//        if (loadoutIndex == self.CurrentLoadoutIndex || loadoutIndex < 0 || loadoutIndex >= self.Loadouts.Length)
//        {
//            return; // Invalid index or switching to the same loadout
//        }

//        int currentLoadoutIndex = self.CurrentLoadoutIndex;
//        EquipmentLoadout oldLoadout = self.Loadouts[currentLoadoutIndex];
//        EquipmentLoadout newLoadout = self.Loadouts[loadoutIndex];

//        // --- ALGORITHM IMPLEMENTATION ---

//        // Step 1: Return the player's current equipment to its corresponding loadout storage.
//        // We do this for armor, accessories, and dyes.
//        for (int i = 0; i < oldLoadout.Armor.Length; i++)
//        {
//            oldLoadout.Armor[i] = self.armor[i].Clone(); // Store a copy
//            self.armor[i].TurnToAir(); // Clear the player's slot
//        }
//        for (int i = 0; i < oldLoadout.Dye.Length; i++)
//        {
//            oldLoadout.Dye[i] = self.dye[i].Clone();
//            self.dye[i].TurnToAir();
//        }
//        // For visibility toggles, we just copy the value.
//        for (int i = 0; i < oldLoadout.Hide.Length; i++)
//        {
//            oldLoadout.Hide[i] = self.hideVisibleAccessory[i];
//        }


//        // Step 2: Take all non-air items from the loadout you're swapping TO and equip them.
//        // We clear the item from the new loadout's storage as we equip it.
//        for (int i = 0; i < newLoadout.Armor.Length; i++)
//        {
//            if (!newLoadout.Armor[i].IsAir)
//            {
//                self.armor[i] = newLoadout.Armor[i].Clone();
//                newLoadout.Armor[i].TurnToAir(); // Remove from storage
//            }
//        }
//        for (int i = 0; i < newLoadout.Dye.Length; i++)
//        {
//            if (!newLoadout.Dye[i].IsAir)
//            {
//                self.dye[i] = newLoadout.Dye[i].Clone();
//                newLoadout.Dye[i].TurnToAir();
//            }
//        }
//        for (int i = 0; i < newLoadout.Hide.Length; i++)
//        {
//            self.hideVisibleAccessory[i] = newLoadout.Hide[i];
//        }


//        // Step 3: For each empty item slot, find the first loadout with a favorited item for that slot.
//        // This searches through ALL loadouts (0, 1, 2) in order.

//        // Armor and Accessories
//        for (int i = 0; i < self.armor.Length; i++)
//        {
//            if (self.armor[i].IsAir) // Check if the slot is still empty
//            {
//                // Search all loadouts for a favorited item
//                for (int j = 0; j < self.Loadouts.Length; j++)
//                {
//                    Item potentialItem = self.Loadouts[j].Armor[i];
//                    if (!potentialItem.IsAir && potentialItem.favorited)
//                    {
//                        self.armor[i] = potentialItem.Clone(); // Equip the favorited item
//                        potentialItem.TurnToAir(); // Remove it from its original storage
//                        break; // Stop searching for this slot and move to the next
//                    }
//                }
//            }
//        }

//        // Dyes
//        for (int i = 0; i < self.dye.Length; i++)
//        {
//            if (self.dye[i].IsAir) // Check if the dye slot is empty
//            {
//                for (int j = 0; j < self.Loadouts.Length; j++)
//                {
//                    Item potentialDye = self.Loadouts[j].Dye[i];
//                    if (!potentialDye.IsAir && potentialDye.favorited)
//                    {
//                        self.dye[i] = potentialDye.Clone();
//                        potentialDye.TurnToAir();
//                        break;
//                    }
//                }
//            }
//        }

//        // --- FINALIZE THE SWITCH (Copied from vanilla) ---
//        self.CurrentLoadoutIndex = loadoutIndex;

//        // These calls are crucial for effects, sounds, and multiplayer synchronization.
//        Main.mouseLeftRelease = false;
//        ItemSlot.RecordLoadoutChange();
//        SoundEngine.PlaySound(SoundID.Grab);
//        NetMessage.TrySendData(MessageID.SyncLoadout, -1, -1, null, self.whoAmI, loadoutIndex); //TODO: this would need a custom senddata handler too, probably just use a modpacket
//        ParticleOrchestrator.RequestParticleSpawn(clientOnly: false, ParticleOrchestraType.LoadoutChange, new ParticleOrchestraSettings
//        {
//            PositionInWorld = self.Center,
//            UniqueInfoPiece = loadoutIndex
//        }, self.whoAmI);
//    }
//}   
/*
            case 147:
            {
                int num209 = this.reader.ReadByte();
                if (Main.netMode == 2)
                {
                    num209 = this.whoAmI;
                }
                int num219 = this.reader.ReadByte();
                Main.player[num209].TrySwitchingLoadout(num219);
                MessageBuffer.ReadAccessoryVisibility(this.reader, Main.player[num209].hideVisibleAccessory);
                if (Main.netMode == 2)
                {
                    NetMessage.TrySendData(b, -1, num209, null, num209, num219);
                }
                break;
            }
*/