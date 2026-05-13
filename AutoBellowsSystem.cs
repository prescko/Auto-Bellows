using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace AutoBellows
{
    public class AutoBellowsSystem : ModSystem
    {
        private const string HotkeyCode = "autobellows-toggle";
        private const int ScanRadius = 30;
        private const int ScanRadiusSquared = ScanRadius * ScanRadius;
        private const int PumpIntervalMs = 215;
        private const int ScanIntervalMs = 1000;
        private const int MaxInteractionsPerTick = 256;

        private readonly List<BlockPos> bellowsPositions = new List<BlockPos>();
        private ICoreClientAPI? capi;
        private bool enabled;
        private long tickListenerId;
        private long nextScanAtMs;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

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

            tickListenerId = api.Event.RegisterGameTickListener(OnClientTick, PumpIntervalMs);
        }

        private bool OnToggleKey(KeyCombination comb)
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return true;
            }

            enabled = !enabled;
            bellowsPositions.Clear();
            nextScanAtMs = 0;

            return true;
        }

        private void OnClientTick(float dt)
        {
            if (!enabled || capi == null || capi.IsGamePaused || capi.World?.Player?.Entity == null)
            {
                return;
            }

            long now = capi.ElapsedMilliseconds;
            if (now >= nextScanAtMs)
            {
                ScanNearbyBellows();
                nextScanAtMs = now + ScanIntervalMs;
            }

            PumpKnownBellows();
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

            for (int dx = -ScanRadius; dx <= ScanRadius; dx++)
            {
                for (int dy = -ScanRadius; dy <= ScanRadius; dy++)
                {
                    for (int dz = -ScanRadius; dz <= ScanRadius; dz++)
                    {
                        if (dx * dx + dy * dy + dz * dz > ScanRadiusSquared)
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

        private Vec3d GetDebugHitPosition(BlockPos targetPos)
        {
            if (capi?.World?.Player?.Entity == null)
            {
                return new Vec3d(0.5, 0.5, 0.5);
            }

            Vec3d eyePos = capi.World.Player.Entity.Pos.XYZ.Add(0, capi.World.Player.Entity.LocalEyePos.Y, 0);
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
            base.Dispose();
        }
    }
}
