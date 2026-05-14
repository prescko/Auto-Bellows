using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AutoBellows
{
    public class AutoBellowsSystem : ModSystem
    {
        private const string HotkeyCode = "autobellows-toggle";
        private const string PouringHotkeyCode = "autopouring-toggle";
        private const string SettingsHotkeyCode = "autobellows-settings";
        private const string ConfigFileName = "AutoBellows.json";
        private const int BellowsScanRadius = 30;
        private const int BellowsScanRadiusSquared = BellowsScanRadius * BellowsScanRadius;
        private const int PourScanRadius = 50;
        private const int PourScanRadiusSquared = PourScanRadius * PourScanRadius;
        private const int PumpIntervalMs = 215;
        private const int ScanIntervalMs = 1000;
        private const int MaxInteractionsPerTick = 256;
        private const int PourScanIntervalMs = 1000;
        private const int PourBurstCooldownMs = 1200;
        private const int PourWarmupSteps = 36;
        private const int PourSafetySteps = 6;
        private const int PourUnitsPerStep = 2;
        private const int MaxPourTargetsPerScan = 256;

        private static readonly FieldInfo? ToolMoldRequiredUnitsField =
            typeof(BlockEntityToolMold).GetField("requiredUnits", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<BlockPos> bellowsPositions = new List<BlockPos>();
        private readonly List<PourTarget> pourTargets = new List<PourTarget>();
        private ICoreClientAPI? capi;
        private AutoBellowsSettings settings = new AutoBellowsSettings();
        private AutoBellowsSettingsDialog? settingsDialog;
        private bool enabled;
        private bool pouringEnabled;
        private long tickListenerId;
        private long nextScanAtMs;
        private long nextPourScanAtMs;
        private long nextPourActionAtMs;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            settings = LoadSettings(api);

            api.Input.RegisterHotKey(
                HotkeyCode,
                "Toggle Auto Bellows",
                GlKeys.Z,
                HotkeyType.GUIOrOtherControls,
                false,
                true,
                false
            );
            api.Input.SetHotKeyHandler(HotkeyCode, OnToggleKey);

            api.Input.RegisterHotKey(
                PouringHotkeyCode,
                "Toggle Auto Pouring",
                GlKeys.X,
                HotkeyType.GUIOrOtherControls,
                false,
                true,
                false
            );
            api.Input.SetHotKeyHandler(PouringHotkeyCode, OnTogglePouringKey);

            api.Input.RegisterHotKey(
                SettingsHotkeyCode,
                "Auto Bellows Settings",
                GlKeys.F1,
                HotkeyType.GUIOrOtherControls,
                false,
                false,
                false
            );
            api.Input.SetHotKeyHandler(SettingsHotkeyCode, OnSettingsKey);

            settingsDialog = new AutoBellowsSettingsDialog(api, this);

            tickListenerId = api.Event.RegisterGameTickListener(OnClientTick, PumpIntervalMs);
        }

        private bool OnToggleKey(KeyCombination comb)
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return true;
            }

            SetBellowsEnabled(!enabled);

            return true;
        }

        private bool OnTogglePouringKey(KeyCombination comb)
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return true;
            }

            SetPouringEnabled(!pouringEnabled);

            return true;
        }

        private bool OnSettingsKey(KeyCombination comb)
        {
            if (capi == null)
            {
                return true;
            }

            settingsDialog ??= new AutoBellowsSettingsDialog(capi, this);

            if (settingsDialog.IsOpened())
            {
                settingsDialog.TryClose();
            }
            else
            {
                settingsDialog.TryOpen(true);
            }

            return true;
        }

        private void OnClientTick(float dt)
        {
            if (capi == null || capi.IsGamePaused || capi.World?.Player?.Entity == null)
            {
                return;
            }

            long now = capi.ElapsedMilliseconds;
            if (enabled)
            {
                if (now >= nextScanAtMs)
                {
                    ScanNearbyBellows();
                    nextScanAtMs = now + ScanIntervalMs;
                }

                PumpKnownBellows();
            }

            if (pouringEnabled)
            {
                HandleAutoPouring(now);
            }
        }

        private void ScanNearbyBellows()
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return;
            }

            bellowsPositions.Clear();

            BlockPos playerPos = capi.World.Player.Entity.Pos.AsBlockPos;
            IBlockAccessor blockAccessor = capi.World.BlockAccessor;

            for (int dx = -BellowsScanRadius; dx <= BellowsScanRadius; dx++)
            {
                for (int dy = -BellowsScanRadius; dy <= BellowsScanRadius; dy++)
                {
                    for (int dz = -BellowsScanRadius; dz <= BellowsScanRadius; dz++)
                    {
                        if (dx * dx + dy * dy + dz * dz > BellowsScanRadiusSquared)
                        {
                            continue;
                        }

                        BlockPos pos = new BlockPos(
                            playerPos.X + dx,
                            playerPos.Y + dy,
                            playerPos.Z + dz,
                            playerPos.dimension
                        );

                        if (!blockAccessor.IsValidPos(pos))
                        {
                            continue;
                        }

                        Block block = blockAccessor.GetBlock(pos);
                        if (IsBellows(block))
                        {
                            bellowsPositions.Add(pos);
                        }
                    }
                }
            }
        }

        private void PumpKnownBellows()
        {
            if (capi == null)
            {
                return;
            }

            IBlockAccessor blockAccessor = capi.World.BlockAccessor;
            int sent = 0;

            foreach (BlockPos pos in bellowsPositions)
            {
                if (sent >= MaxInteractionsPerTick)
                {
                    break;
                }

                Block block = blockAccessor.GetBlock(pos);
                if (!IsBellows(block))
                {
                    continue;
                }

                SendRightClick(pos, block);
                sent++;
            }
        }

        private void SendRightClick(BlockPos pos, Block block)
        {
            if (capi == null)
            {
                return;
            }

            BlockSelection blockSelection = new BlockSelection(pos.Copy(), GetFacingFromCode(block), block)
            {
                HitPosition = GetDebugHitPosition(pos),
                SelectionBoxIndex = 0
            };

            try
            {
                block.OnBlockInteractStart(capi.World, capi.World.Player, blockSelection);
            }
            catch
            {
            }

            capi.Network.SendHandInteraction(
                (int)EnumMouseButton.Right,
                blockSelection,
                null,
                EnumHandInteract.BlockInteract,
                (int)EnumHandInteractNw.StartBlockUse,
                true,
                EnumItemUseCancelReason.ReleasedMouse
            );
        }

        private void HandleAutoPouring(long now)
        {
            if (capi == null)
            {
                return;
            }

            if (IsManualHandUseActive())
            {
                nextPourActionAtMs = now + 250;
                return;
            }

            if (now < nextPourActionAtMs)
            {
                return;
            }

            if (now >= nextPourScanAtMs)
            {
                ScanPouringTargets();
                nextPourScanAtMs = now + PourScanIntervalMs;
            }

            BurstPourTargets(now);
        }

        private void ScanPouringTargets()
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return;
            }

            pourTargets.Clear();

            PourSource? source = GetActivePourSource();
            if (source == null)
            {
                int placedContainers = CountNearbySmeltedContainers();
                LogPour("skipped scan: no active hotbar BlockSmeltedContainer with liquid metal; nearby placed smelted containers=" + placedContainers + ". Vanilla pouring uses held-item interaction.");
                return;
            }

            if (!settings.AllowIngotMolds && !settings.AllowToolMolds)
            {
                LogPour("skipped scan: all pouring target mold types are disabled");
                return;
            }

            List<PourTarget> candidates = new List<PourTarget>();
            ScanNearbyMolds(source, candidates, out int moldSlots, out int partialSlots, out int emptySlots, out int fullSlots, out int skippedSlots);

            candidates.Sort(ComparePourTargets);

            int remainingUnits = source.Units;
            PourTarget? leftoverTarget = null;
            foreach (PourTarget target in candidates)
            {
                if (pourTargets.Count >= MaxPourTargetsPerScan)
                {
                    LogPour("scan limit reached: " + MaxPourTargetsPerScan + " planned targets");
                    break;
                }

                if (target.NeededUnits > remainingUnits)
                {
                    if (remainingUnits > 0 && leftoverTarget == null)
                    {
                        leftoverTarget = target;
                    }
                    continue;
                }

                target.TransferUnits = target.NeededUnits;
                pourTargets.Add(target);
                remainingUnits -= target.NeededUnits;
            }

            if (remainingUnits > 0 && leftoverTarget != null && pourTargets.Count < MaxPourTargetsPerScan)
            {
                leftoverTarget.TransferUnits = remainingUnits;
                leftoverTarget.IsLeftoverPour = true;
                pourTargets.Add(leftoverTarget);
                remainingUnits = 0;
            }

            int reserved = source.Units - remainingUnits;
            LogPour("found active crucible: metal=" + FormatStack(source.MetalStack) + ", units=" + source.Units);
            LogPour("found molds: slots=" + moldSlots + ", partial=" + partialSlots + ", empty=" + emptySlots + ", full=" + fullSlots + ", skipped=" + skippedSlots);
            LogPour("calculated planned pours=" + pourTargets.Count + ", fullFills=" + CountFullFillTargets() + ", leftoverPours=" + CountLeftoverTargets() + ", reservedUnits=" + reserved + ", unitsAfterPlan=" + remainingUnits);
        }

        private void ScanNearbyMolds(PourSource source, List<PourTarget> candidates, out int moldSlots, out int partialSlots, out int emptySlots, out int fullSlots, out int skippedSlots)
        {
            moldSlots = 0;
            partialSlots = 0;
            emptySlots = 0;
            fullSlots = 0;
            skippedSlots = 0;

            if (capi?.World?.Player?.Entity == null)
            {
                return;
            }

            BlockPos playerPos = capi.World.Player.Entity.Pos.AsBlockPos;
            IBlockAccessor blockAccessor = capi.World.BlockAccessor;

            for (int dx = -PourScanRadius; dx <= PourScanRadius; dx++)
            {
                for (int dy = -PourScanRadius; dy <= PourScanRadius; dy++)
                {
                    for (int dz = -PourScanRadius; dz <= PourScanRadius; dz++)
                    {
                        if (dx * dx + dy * dy + dz * dz > PourScanRadiusSquared)
                        {
                            continue;
                        }

                        BlockPos pos = new BlockPos(
                            playerPos.X + dx,
                            playerPos.Y + dy,
                            playerPos.Z + dz,
                            playerPos.dimension
                        );

                        if (!blockAccessor.IsValidPos(pos))
                        {
                            continue;
                        }

                        BlockEntity blockEntity = blockAccessor.GetBlockEntity(pos);
                        if (blockEntity is BlockEntityToolMold toolMold)
                        {
                            if (!settings.AllowToolMolds)
                            {
                                continue;
                            }

                            moldSlots++;
                            AddToolMoldTarget(pos, toolMold, source, candidates, ref partialSlots, ref emptySlots, ref fullSlots, ref skippedSlots);
                        }
                        else if (blockEntity is BlockEntityIngotMold ingotMold)
                        {
                            if (!settings.AllowIngotMolds)
                            {
                                continue;
                            }

                            AddIngotMoldTargets(pos, ingotMold, source, candidates, ref moldSlots, ref partialSlots, ref emptySlots, ref fullSlots, ref skippedSlots);
                        }
                    }
                }
            }
        }

        private void AddToolMoldTarget(BlockPos pos, BlockEntityToolMold mold, PourSource source, List<PourTarget> candidates, ref int partialSlots, ref int emptySlots, ref int fullSlots, ref int skippedSlots)
        {
            if (!mold.CanReceiveAny)
            {
                skippedSlots++;
                LogPour("skip tool mold at " + FormatPos(pos) + ": CanReceiveAny=false");
                return;
            }

            if (mold.IsFull)
            {
                fullSlots++;
                return;
            }

            if (!mold.CanReceive(source.MetalStack))
            {
                skippedSlots++;
                LogPour("skip tool mold at " + FormatPos(pos) + ": cannot receive " + FormatStack(source.MetalStack));
                return;
            }

            int requiredUnits = GetToolMoldRequiredUnits(mold);
            if (requiredUnits <= 0)
            {
                skippedSlots++;
                LogPour("skip tool mold at " + FormatPos(pos) + ": invalid requiredUnits=" + requiredUnits);
                return;
            }

            int fillLevel = Math.Max(0, mold.FillLevel);
            int neededUnits = requiredUnits - fillLevel;
            if (neededUnits <= 0)
            {
                fullSlots++;
                return;
            }

            bool isPartial = fillLevel > 0;
            if (isPartial)
            {
                partialSlots++;
            }
            else
            {
                emptySlots++;
            }

            candidates.Add(new PourTarget(
                pos.Copy(),
                "tool mold",
                neededUnits,
                isPartial,
                GetDistanceSqToPlayer(pos),
                null,
                new Vec3d(0.5, 0.5, 0.5)
            ));
        }

        private void AddIngotMoldTargets(BlockPos pos, BlockEntityIngotMold mold, PourSource source, List<PourTarget> candidates, ref int moldSlots, ref int partialSlots, ref int emptySlots, ref int fullSlots, ref int skippedSlots)
        {
            AddIngotMoldSideTarget(pos, mold, source, candidates, false, ref moldSlots, ref partialSlots, ref emptySlots, ref fullSlots, ref skippedSlots);

            if (mold.QuantityMolds > 1)
            {
                AddIngotMoldSideTarget(pos, mold, source, candidates, true, ref moldSlots, ref partialSlots, ref emptySlots, ref fullSlots, ref skippedSlots);
            }
        }

        private void AddIngotMoldSideTarget(BlockPos pos, BlockEntityIngotMold mold, PourSource source, List<PourTarget> candidates, bool rightSide, ref int moldSlots, ref int partialSlots, ref int emptySlots, ref int fullSlots, ref int skippedSlots)
        {
            moldSlots++;

            string sideName = rightSide ? "right" : "left";
            ItemStack? moldStack = rightSide ? mold.MoldRight : mold.MoldLeft;
            if (moldStack == null)
            {
                skippedSlots++;
                LogPour("skip ingot mold " + sideName + " at " + FormatPos(pos) + ": no mold item");
                return;
            }

            if (rightSide ? mold.ShatteredRight : mold.ShatteredLeft)
            {
                skippedSlots++;
                LogPour("skip ingot mold " + sideName + " at " + FormatPos(pos) + ": shattered");
                return;
            }

            if (!IsFiredOrBurnedMold(moldStack))
            {
                skippedSlots++;
                LogPour("skip ingot mold " + sideName + " at " + FormatPos(pos) + ": mold is not fired/burned");
                return;
            }

            bool isFull = rightSide ? mold.IsFullRight : mold.IsFullLeft;
            if (isFull)
            {
                fullSlots++;
                return;
            }

            ItemStack? contents = rightSide ? mold.ContentsRight : mold.ContentsLeft;
            if (contents != null && !StacksEqual(contents, source.MetalStack))
            {
                skippedSlots++;
                LogPour("skip ingot mold " + sideName + " at " + FormatPos(pos) + ": contains " + FormatStack(contents) + ", source=" + FormatStack(source.MetalStack));
                return;
            }

            if (contents == null && !mold.CanReceive(source.MetalStack))
            {
                skippedSlots++;
                LogPour("skip ingot mold " + sideName + " at " + FormatPos(pos) + ": CanReceive=false for " + FormatStack(source.MetalStack));
                return;
            }

            int requiredUnits = mold.RequiredUnits;
            int fillLevel = Math.Max(0, rightSide ? mold.FillLevelRight : mold.FillLevelLeft);
            int neededUnits = requiredUnits - fillLevel;
            if (requiredUnits <= 0 || neededUnits <= 0)
            {
                fullSlots++;
                return;
            }

            if (!TryGetLocalIngotHitForSide(mold, rightSide, out Vec3d localHit))
            {
                skippedSlots++;
                LogPour("skip ingot mold " + sideName + " at " + FormatPos(pos) + ": could not resolve local hit side");
                return;
            }

            if (!TryBuildIngotHitPosition(pos, mold, rightSide, localHit, out Vec3d hitPosition, out string skipReason))
            {
                skippedSlots++;
                LogPour("skip ingot mold " + sideName + " at " + FormatPos(pos) + ": " + skipReason);
                return;
            }

            bool isPartial = fillLevel > 0;
            if (isPartial)
            {
                partialSlots++;
            }
            else
            {
                emptySlots++;
            }

            candidates.Add(new PourTarget(
                pos.Copy(),
                "ingot mold " + sideName,
                neededUnits,
                isPartial,
                GetDistanceSqToPlayer(pos),
                rightSide,
                hitPosition
            ));
        }

        private void BurstPourTargets(long now)
        {
            if (capi == null || pourTargets.Count == 0)
            {
                return;
            }

            if (IsManualHandUseActive())
            {
                LogPour("cooldown wait: player is already using held item/block, skipping burst");
                return;
            }

            PourSource? source = GetActivePourSource();
            if (source == null)
            {
                pourTargets.Clear();
                LogPour("skipped burst: active hotbar item is no longer a liquid metal crucible");
                return;
            }

            int sent = 0;
            int sentUnits = 0;
            foreach (PourTarget target in pourTargets)
            {
                if (!RefreshPourTarget(target, source, out string skipReason))
                {
                    LogPour("skip target " + target.Label + " at " + FormatPos(target.Position) + ": " + skipReason);
                    continue;
                }

                if (target.TransferUnits <= 0)
                {
                    LogPour("skip target " + target.Label + " at " + FormatPos(target.Position) + ": no planned transfer units");
                    continue;
                }

                SendPourBurst(target);
                sent++;
                sentUnits += target.TransferUnits;
            }

            pourTargets.Clear();
            nextPourActionAtMs = now + PourBurstCooldownMs;
            nextPourScanAtMs = now + PourBurstCooldownMs;
            LogPour("burst sent: targets=" + sent + ", plannedUnits=" + sentUnits + ", cooldownMs=" + PourBurstCooldownMs);
        }

        private bool RefreshPourTarget(PourTarget target, PourSource source, out string skipReason)
        {
            skipReason = "";

            if (capi == null)
            {
                skipReason = "client API unavailable";
                return false;
            }

            BlockEntity blockEntity = capi.World.BlockAccessor.GetBlockEntity(target.Position);
            if (blockEntity is BlockEntityToolMold toolMold)
            {
                if (!settings.AllowToolMolds)
                {
                    skipReason = "tool mold target type disabled";
                    return false;
                }

                if (!toolMold.CanReceiveAny || toolMold.IsFull || !toolMold.CanReceive(source.MetalStack))
                {
                    skipReason = "tool mold no longer receivable";
                    return false;
                }

                int requiredUnits = GetToolMoldRequiredUnits(toolMold);
                int neededUnits = requiredUnits - Math.Max(0, toolMold.FillLevel);
                if (neededUnits <= 0)
                {
                    skipReason = "tool mold already full";
                    return false;
                }

                target.NeededUnits = neededUnits;
                target.TransferUnits = Math.Min(target.TransferUnits, neededUnits);
                target.HitPosition = GetDebugHitPosition(target.Position);
                return true;
            }

            if (blockEntity is BlockEntityIngotMold ingotMold && target.IngotRightSide.HasValue)
            {
                if (!settings.AllowIngotMolds)
                {
                    skipReason = "ingot mold target type disabled";
                    return false;
                }

                bool rightSide = target.IngotRightSide.Value;
                bool isFull = rightSide ? ingotMold.IsFullRight : ingotMold.IsFullLeft;
                ItemStack? contents = rightSide ? ingotMold.ContentsRight : ingotMold.ContentsLeft;

                if (isFull)
                {
                    skipReason = "ingot mold side already full";
                    return false;
                }

                if (contents != null && !StacksEqual(contents, source.MetalStack))
                {
                    skipReason = "ingot mold side now contains another metal";
                    return false;
                }

                if (contents == null && !ingotMold.CanReceive(source.MetalStack))
                {
                    skipReason = "ingot mold no longer accepts source metal";
                    return false;
                }

                int fillLevel = Math.Max(0, rightSide ? ingotMold.FillLevelRight : ingotMold.FillLevelLeft);
                int neededUnits = ingotMold.RequiredUnits - fillLevel;
                if (neededUnits <= 0)
                {
                    skipReason = "ingot mold side has no remaining capacity";
                    return false;
                }

                if (!TryGetLocalIngotHitForSide(ingotMold, rightSide, out Vec3d localHit)
                    || !TryBuildIngotHitPosition(target.Position, ingotMold, rightSide, localHit, out Vec3d hitPosition, out skipReason))
                {
                    return false;
                }

                target.NeededUnits = neededUnits;
                target.TransferUnits = Math.Min(target.TransferUnits, neededUnits);
                target.HitPosition = hitPosition;
                return true;
            }

            skipReason = "target block entity changed";
            return false;
        }

        private void SendPourBurst(PourTarget target)
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return;
            }

            Block block = capi.World.BlockAccessor.GetBlock(target.Position);
            BlockSelection blockSelection = new BlockSelection(target.Position.Copy(), BlockFacing.UP, block)
            {
                HitPosition = target.HitPosition,
                SelectionBoxIndex = 0
            };

            int useSteps = CalculateUseSteps(target.TransferUnits);
            EntityControls controls = capi.World.Player.Entity.Controls;
            int previousUsingCount = controls.UsingCount;

            controls.UsingCount = 0;
            capi.Network.SendHandInteraction(
                (int)EnumMouseButton.Right,
                blockSelection,
                null,
                EnumHandInteract.HeldItemInteract,
                (int)EnumHandInteractNw.StartHeldItemUse,
                true,
                EnumItemUseCancelReason.ReleasedMouse
            );

            controls.UsingCount = useSteps;
            capi.Network.SendHandInteraction(
                (int)EnumMouseButton.Right,
                blockSelection,
                null,
                EnumHandInteract.HeldItemInteract,
                (int)EnumHandInteractNw.StopHeldItemUse,
                false,
                EnumItemUseCancelReason.ReleasedMouse
            );

            controls.UsingCount = previousUsingCount;

            LogPour("sent pour burst: target=" + target.Label + " at " + FormatPos(target.Position) + ", transferUnits=" + target.TransferUnits + ", capacityUnits=" + target.NeededUnits + ", useSteps=" + useSteps + (target.IsLeftoverPour ? ", leftover=true" : ""));
        }

        private bool IsManualHandUseActive()
        {
            EntityControls? controls = capi?.World?.Player?.Entity?.Controls;
            return controls != null && controls.HandUse != EnumHandInteract.None;
        }

        private PourSource? GetActivePourSource()
        {
            if (capi?.World?.Player?.InventoryManager == null)
            {
                return null;
            }

            ItemSlot? slot = capi.World.Player.InventoryManager.ActiveHotbarSlot;
            ItemStack? stack = slot?.Itemstack;
            if (stack?.Collectible is not BlockSmeltedContainer container)
            {
                return null;
            }

            KeyValuePair<ItemStack, int> contents = container.GetContents(capi.World, stack);
            if (contents.Key == null || contents.Value <= 0)
            {
                return null;
            }

            if (container.HasSolidifed(stack, contents.Key, capi.World))
            {
                LogPour("skipped source: active crucible contents are solidified");
                return null;
            }

            if (slot == null)
            {
                return null;
            }

            return new PourSource(slot, stack, container, contents.Key, contents.Value);
        }

        private int CountNearbySmeltedContainers()
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return 0;
            }

            int count = 0;
            BlockPos playerPos = capi.World.Player.Entity.Pos.AsBlockPos;
            IBlockAccessor blockAccessor = capi.World.BlockAccessor;

            for (int dx = -PourScanRadius; dx <= PourScanRadius; dx++)
            {
                for (int dy = -PourScanRadius; dy <= PourScanRadius; dy++)
                {
                    for (int dz = -PourScanRadius; dz <= PourScanRadius; dz++)
                    {
                        if (dx * dx + dy * dy + dz * dz > PourScanRadiusSquared)
                        {
                            continue;
                        }

                        BlockPos pos = new BlockPos(playerPos.X + dx, playerPos.Y + dy, playerPos.Z + dz, playerPos.dimension);
                        if (blockAccessor.IsValidPos(pos) && blockAccessor.GetBlock(pos) is BlockSmeltedContainer)
                        {
                            count++;
                        }
                    }
                }
            }

            return count;
        }

        private bool TryBuildIngotHitPosition(BlockPos pos, BlockEntityIngotMold mold, bool rightSide, Vec3d localHit, out Vec3d hitPosition, out string skipReason)
        {
            if (IsWithinPickingRange(pos, localHit))
            {
                hitPosition = localHit;
                skipReason = "";
                return true;
            }

            foreach (Vec3d debugHit in GetDebugReachHitCandidates(pos))
            {
                if (WouldSelectRightIngotSide(mold, debugHit) == rightSide)
                {
                    hitPosition = debugHit;
                    skipReason = "";
                    return true;
                }
            }

            hitPosition = localHit;
            skipReason = "debug reach hit would select the other ingot side";
            return false;
        }

        private IEnumerable<Vec3d> GetDebugReachHitCandidates(BlockPos targetPos)
        {
            Vec3d eyePos = GetEyePosition();
            double range = Math.Max(1, GetPickingRange() - 0.5);

            yield return new Vec3d(eyePos.X - targetPos.X, eyePos.Y - targetPos.Y, eyePos.Z - targetPos.Z);

            double[] offsets = new[] { range, -range, range * 0.5, -range * 0.5 };
            foreach (double offset in offsets)
            {
                yield return new Vec3d(eyePos.X + offset - targetPos.X, eyePos.Y - targetPos.Y, eyePos.Z - targetPos.Z);
                yield return new Vec3d(eyePos.X - targetPos.X, eyePos.Y - targetPos.Y, eyePos.Z + offset - targetPos.Z);
            }

            double diagonal = range * 0.6;
            yield return new Vec3d(eyePos.X + diagonal - targetPos.X, eyePos.Y - targetPos.Y, eyePos.Z + diagonal - targetPos.Z);
            yield return new Vec3d(eyePos.X + diagonal - targetPos.X, eyePos.Y - targetPos.Y, eyePos.Z - diagonal - targetPos.Z);
            yield return new Vec3d(eyePos.X - diagonal - targetPos.X, eyePos.Y - targetPos.Y, eyePos.Z + diagonal - targetPos.Z);
            yield return new Vec3d(eyePos.X - diagonal - targetPos.X, eyePos.Y - targetPos.Y, eyePos.Z - diagonal - targetPos.Z);
        }

        private bool TryGetLocalIngotHitForSide(BlockEntityIngotMold mold, bool rightSide, out Vec3d localHit)
        {
            Vec3d[] candidates =
            {
                new Vec3d(0.25, 0.5, 0.5),
                new Vec3d(0.75, 0.5, 0.5),
                new Vec3d(0.5, 0.5, 0.25),
                new Vec3d(0.5, 0.5, 0.75)
            };

            foreach (Vec3d candidate in candidates)
            {
                if (WouldSelectRightIngotSide(mold, candidate) == rightSide)
                {
                    localHit = candidate;
                    return true;
                }
            }

            localHit = new Vec3d(0.5, 0.5, 0.5);
            return false;
        }

        private static bool WouldSelectRightIngotSide(BlockEntityIngotMold mold, Vec3d hitPosition)
        {
            bool previous = mold.IsRightSideSelected;
            mold.SetSelectedSide(hitPosition);
            bool selected = mold.IsRightSideSelected;
            mold.IsRightSideSelected = previous;
            return selected;
        }

        private bool IsWithinPickingRange(BlockPos targetPos, Vec3d hitPosition)
        {
            Vec3d eyePos = GetEyePosition();
            double x = targetPos.X + hitPosition.X - eyePos.X;
            double y = targetPos.Y + hitPosition.Y - eyePos.Y;
            double z = targetPos.Z + hitPosition.Z - eyePos.Z;
            double range = GetPickingRange();
            return x * x + y * y + z * z <= range * range;
        }

        private double GetPickingRange()
        {
            if (capi?.World?.Player?.WorldData != null)
            {
                return Math.Max(1, capi.World.Player.WorldData.PickingRange);
            }

            return 4.5;
        }

        private Vec3d GetEyePosition()
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return new Vec3d();
            }

            return capi.World.Player.Entity.Pos.XYZ.Add(0, capi.World.Player.Entity.LocalEyePos.Y, 0);
        }

        private int CalculateUseSteps(int units)
        {
            return PourWarmupSteps + Math.Max(1, (int)Math.Ceiling(units / (double)PourUnitsPerStep)) + PourSafetySteps;
        }

        private int CountFullFillTargets()
        {
            int count = 0;
            foreach (PourTarget target in pourTargets)
            {
                if (!target.IsLeftoverPour)
                {
                    count++;
                }
            }

            return count;
        }

        private int CountLeftoverTargets()
        {
            int count = 0;
            foreach (PourTarget target in pourTargets)
            {
                if (target.IsLeftoverPour)
                {
                    count++;
                }
            }

            return count;
        }

        private int GetToolMoldRequiredUnits(BlockEntityToolMold mold)
        {
            if (ToolMoldRequiredUnitsField?.GetValue(mold) is int requiredUnits)
            {
                return requiredUnits;
            }

            return 0;
        }

        private bool IsFiredOrBurnedMold(ItemStack moldStack)
        {
            string? type = null;
            moldStack.Block?.Variant?.TryGetValue("type", out type);
            if (type == "fired")
            {
                return true;
            }

            return moldStack.Block?.Code?.Path?.Contains("burned") == true;
        }

        private static bool StacksEqual(ItemStack a, ItemStack b)
        {
            return a.Collectible.Equals(a, b, GlobalConstants.IgnoredStackAttributes);
        }

        private int ComparePourTargets(PourTarget a, PourTarget b)
        {
            int partialCompare = b.IsPartial.CompareTo(a.IsPartial);
            if (partialCompare != 0)
            {
                return partialCompare;
            }

            int neededCompare = a.NeededUnits.CompareTo(b.NeededUnits);
            if (neededCompare != 0)
            {
                return neededCompare;
            }

            return a.DistanceSq.CompareTo(b.DistanceSq);
        }

        private double GetDistanceSqToPlayer(BlockPos pos)
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return 0;
            }

            Vec3d playerPos = capi.World.Player.Entity.Pos.XYZ;
            double dx = pos.X + 0.5 - playerPos.X;
            double dy = pos.Y + 0.5 - playerPos.Y;
            double dz = pos.Z + 0.5 - playerPos.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private string FormatStack(ItemStack stack)
        {
            return stack.Collectible?.Code?.ToString() ?? stack.GetName();
        }

        private static string FormatPos(BlockPos pos)
        {
            return pos.X + "," + pos.Y + "," + pos.Z;
        }

        private void LogPour(string message)
        {
            capi?.Logger.Notification("[AutoBellows] AutoPour: " + message);
        }

        private void ShowChatStatus(string featureName, bool isEnabled)
        {
            capi?.ShowChatMessage("[AutoBellows] " + featureName + ": " + StateText(isEnabled));
        }

        private string T(string uk, string en)
        {
            return settings.LanguageCode == "uk" ? uk : en;
        }

        private string StateText(bool isEnabled)
        {
            return isEnabled ? T("УВІМК", "ON") : T("ВИМК", "OFF");
        }

        private string AutoBellowsLabel()
        {
            return T("Авто роздув", "Auto Bellows");
        }

        private string AutoPouringLabel()
        {
            return T("Авто заливання", "Auto Pouring");
        }

        private string IngotMoldsLabel()
        {
            return T("Форми для злитків", "Ingot molds");
        }

        private string ToolMoldsLabel()
        {
            return T("Форми для інструментів", "Tool molds");
        }

        private AutoBellowsSettings LoadSettings(ICoreClientAPI api)
        {
            try
            {
                AutoBellowsSettings? loaded = api.LoadModConfig<AutoBellowsSettings>(ConfigFileName);
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                api.Logger.Notification("[AutoBellows] Could not load config: " + ex.Message);
            }

            AutoBellowsSettings defaults = new AutoBellowsSettings();
            try
            {
                api.StoreModConfig(defaults, ConfigFileName);
            }
            catch (Exception ex)
            {
                api.Logger.Notification("[AutoBellows] Could not create config: " + ex.Message);
            }

            return defaults;
        }

        private void SaveSettings()
        {
            if (capi == null)
            {
                return;
            }

            try
            {
                capi.StoreModConfig(settings, ConfigFileName);
            }
            catch (Exception ex)
            {
                capi.Logger.Notification("[AutoBellows] Could not save config: " + ex.Message);
            }
        }

        private void SetAllowIngotMolds(bool allow)
        {
            if (settings.AllowIngotMolds == allow)
            {
                return;
            }

            settings.AllowIngotMolds = allow;
            OnPourTargetSettingsChanged(IngotMoldsLabel(), allow);
        }

        private void SetAllowToolMolds(bool allow)
        {
            if (settings.AllowToolMolds == allow)
            {
                return;
            }

            settings.AllowToolMolds = allow;
            OnPourTargetSettingsChanged(ToolMoldsLabel(), allow);
        }

        private void OnPourTargetSettingsChanged(string label, bool allow)
        {
            SaveSettings();
            pourTargets.Clear();
            nextPourScanAtMs = 0;
            nextPourActionAtMs = 0;

            LogPour("target setting changed: " + label + "=" + allow);
            capi?.ShowChatMessage("[AutoBellows] " + T("Ціль заливання", "Auto Pouring target") + " " + label + ": " + StateText(allow));
        }

        private void SetBellowsEnabled(bool value)
        {
            if (enabled == value)
            {
                return;
            }

            enabled = value;
            bellowsPositions.Clear();
            nextScanAtMs = 0;

            ShowChatStatus(AutoBellowsLabel(), enabled);
            settingsDialog?.SyncSwitches();
        }

        private void SetPouringEnabled(bool value)
        {
            if (pouringEnabled == value)
            {
                return;
            }

            pouringEnabled = value;
            pourTargets.Clear();
            nextPourScanAtMs = 0;
            nextPourActionAtMs = 0;

            LogPour("Auto Pouring " + (pouringEnabled ? "enabled" : "disabled"));
            ShowChatStatus(AutoPouringLabel(), pouringEnabled);
            settingsDialog?.SyncSwitches();
        }

        private void SetUkrainianLanguage(bool value)
        {
            string languageCode = value ? "uk" : "en";
            if (settings.LanguageCode == languageCode)
            {
                return;
            }

            settings.LanguageCode = languageCode;
            settings.Normalize();
            SaveSettings();

            capi?.ShowChatMessage("[AutoBellows] " + T("Мова: Українська", "Language: English"));
            settingsDialog?.RecomposeDialog();
        }

        private Vec3d GetDebugHitPosition(BlockPos targetPos)
        {
            Vec3d eyePos = GetEyePosition();
            return new Vec3d(
                eyePos.X - targetPos.X,
                eyePos.Y - targetPos.Y,
                eyePos.Z - targetPos.Z
            );
        }

        private static bool IsBellows(Block? block)
        {
            string? path = block?.Code?.Path;
            return path != null && path.StartsWith("bellows-", StringComparison.Ordinal);
        }

        private static BlockFacing GetFacingFromCode(Block block)
        {
            string? path = block.Code?.Path;
            if (path == null)
            {
                return BlockFacing.NORTH;
            }

            int lastDash = path.LastIndexOf('-');
            if (lastDash < 0 || lastDash == path.Length - 1)
            {
                return BlockFacing.NORTH;
            }

            return BlockFacing.FromCode(path.Substring(lastDash + 1)) ?? BlockFacing.NORTH;
        }

        public override void Dispose()
        {
            if (capi != null && tickListenerId != 0)
            {
                capi.Event.UnregisterGameTickListener(tickListenerId);
            }

            bellowsPositions.Clear();
            pourTargets.Clear();
            base.Dispose();
        }

        private sealed class AutoBellowsSettings
        {
            public bool AllowIngotMolds { get; set; } = true;
            public bool AllowToolMolds { get; set; }
            public string LanguageCode { get; set; } = "uk";

            public void Normalize()
            {
                if (LanguageCode != "uk" && LanguageCode != "en")
                {
                    LanguageCode = "uk";
                }
            }
        }

        private sealed class AutoBellowsSettingsDialog : GuiDialog
        {
            private const string BellowsSwitchKey = "autoBellowsSwitch";
            private const string PouringSwitchKey = "autoPouringSwitch";
            private const string IngotSwitchKey = "ingotMoldsSwitch";
            private const string ToolSwitchKey = "toolMoldsSwitch";
            private const string LanguageSwitchKey = "languageSwitch";

            private readonly AutoBellowsSystem system;

            public AutoBellowsSettingsDialog(ICoreClientAPI capi, AutoBellowsSystem system) : base(capi)
            {
                this.system = system;
                ComposeDialog();
            }

            public override string ToggleKeyCombinationCode => SettingsHotkeyCode;

            public override void OnGuiOpened()
            {
                base.OnGuiOpened();
                SyncSwitches();
            }

            public void RecomposeDialog()
            {
                bool wasOpened = IsOpened();
                if (wasOpened)
                {
                    TryClose();
                }

                ComposeDialog();

                if (wasOpened)
                {
                    TryOpen(true);
                }
            }

            private void ComposeDialog()
            {
                ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog;
                ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
                bgBounds.BothSizing = ElementSizing.FitToChildren;

                SingleComposer = capi.Gui.CreateCompo("autobellows-settings", dialogBounds)
                    .AddShadedDialogBG(bgBounds, true, 5, 0.75f)
                    .AddDialogTitleBar(system.T("Налаштування Auto Bellows", "Auto Bellows Settings"), () => TryClose(), CairoFont.WhiteSmallishText(), ElementStdBounds.TitleBar(), "titlebar")
                    .BeginChildElements(bgBounds)
                    .AddStaticText(system.T("Функції", "Features"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(20, 42, 360, 24), "featuresTitle")
                    .AddStaticText(system.AutoBellowsLabel(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(20, 76, 280, 24), "autoBellowsLabel")
                    .AddSwitch(system.SetBellowsEnabled, ElementBounds.Fixed(332, 72, 42, 24), BellowsSwitchKey, 20, 4)
                    .AddStaticText(system.AutoPouringLabel(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(20, 110, 280, 24), "autoPouringLabel")
                    .AddSwitch(system.SetPouringEnabled, ElementBounds.Fixed(332, 106, 42, 24), PouringSwitchKey, 20, 4)
                    .AddStaticText(system.T("Цілі заливання", "Auto Pouring targets"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(20, 150, 360, 24), "pourTargetsTitle")
                    .AddStaticText(system.IngotMoldsLabel(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(20, 184, 280, 24), "ingotMoldsLabel")
                    .AddSwitch(system.SetAllowIngotMolds, ElementBounds.Fixed(332, 180, 42, 24), IngotSwitchKey, 20, 4)
                    .AddStaticText(system.ToolMoldsLabel(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(20, 218, 280, 24), "toolMoldsLabel")
                    .AddSwitch(system.SetAllowToolMolds, ElementBounds.Fixed(332, 214, 42, 24), ToolSwitchKey, 20, 4)
                    .AddStaticText(system.T("Українська мова", "Ukrainian language"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(20, 258, 280, 24), "languageLabel")
                    .AddSwitch(system.SetUkrainianLanguage, ElementBounds.Fixed(332, 254, 42, 24), LanguageSwitchKey, 20, 4)
                    .EndChildElements()
                    .Compose(true);

                SyncSwitches();
            }

            public void SyncSwitches()
            {
                if (SingleComposer == null)
                {
                    return;
                }

                Vintagestory.API.Client.GuiComposerHelpers.GetSwitch(SingleComposer, BellowsSwitchKey).On = system.enabled;
                Vintagestory.API.Client.GuiComposerHelpers.GetSwitch(SingleComposer, PouringSwitchKey).On = system.pouringEnabled;
                Vintagestory.API.Client.GuiComposerHelpers.GetSwitch(SingleComposer, IngotSwitchKey).On = system.settings.AllowIngotMolds;
                Vintagestory.API.Client.GuiComposerHelpers.GetSwitch(SingleComposer, ToolSwitchKey).On = system.settings.AllowToolMolds;
                Vintagestory.API.Client.GuiComposerHelpers.GetSwitch(SingleComposer, LanguageSwitchKey).On = system.settings.LanguageCode == "uk";
            }
        }

        private sealed class PourSource
        {
            public PourSource(ItemSlot slot, ItemStack stack, BlockSmeltedContainer container, ItemStack metalStack, int units)
            {
                Slot = slot;
                Stack = stack;
                Container = container;
                MetalStack = metalStack;
                Units = units;
            }

            public ItemSlot Slot { get; }
            public ItemStack Stack { get; }
            public BlockSmeltedContainer Container { get; }
            public ItemStack MetalStack { get; }
            public int Units { get; }
        }

        private sealed class PourTarget
        {
            public PourTarget(BlockPos position, string label, int neededUnits, bool isPartial, double distanceSq, bool? ingotRightSide, Vec3d hitPosition)
            {
                Position = position;
                Label = label;
                NeededUnits = neededUnits;
                IsPartial = isPartial;
                DistanceSq = distanceSq;
                IngotRightSide = ingotRightSide;
                HitPosition = hitPosition;
            }

            public BlockPos Position { get; }
            public string Label { get; }
            public int NeededUnits { get; set; }
            public int TransferUnits { get; set; }
            public bool IsPartial { get; }
            public bool IsLeftoverPour { get; set; }
            public double DistanceSq { get; }
            public bool? IngotRightSide { get; }
            public Vec3d HitPosition { get; set; }
        }
    }
}
