using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Checksums;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopGuard;

internal static class ToolkitForensicsRuntime
{
    private static int _checkpointObserverAvailable;
    private static int _contributionObserversAvailable;

    public static bool CheckpointObserverAvailable =>
        Volatile.Read(ref _checkpointObserverAvailable) != 0;

    public static bool ContributionObserversAvailable =>
        Volatile.Read(ref _contributionObserversAvailable) != 0;

    public static void EnableCheckpointObserver() =>
        Volatile.Write(ref _checkpointObserverAvailable, 1);

    public static void EnableContributionObservers() =>
        Volatile.Write(ref _contributionObserversAvailable, 1);

    public static void OnCheckpoint(
        NetChecksumData data,
        string context,
        NetFullCombatState snapshot)
    {
        try
        {
            ToolkitDiagnosticsRuntime.PublishNaturalCheckpoint(
                data.id,
                context,
                Project(snapshot));
            ToolkitRuntime.RecordNaturalCheckpoint(
                data.id,
                context);
        }
        catch (Exception ex)
        {
            Main.Log.Error(
                "Optional hierarchical checkpoint observation was contained: "
                + ex.GetType().Name);
        }
    }

    public static void SampleLocalHand()
    {
        try
        {
            if (!ToolkitDiagnosticsRuntime.TryGetLocalIdentity(
                    out ulong localId,
                    out _))
            {
                return;
            }

            MegaCrit.Sts2.Core.Entities.Players.Player? player =
                RunManager.Instance.DebugOnlyGetState()?.Players.FirstOrDefault(
                    item => item.NetId == localId);
            IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel>? cards =
                player?.PlayerCombatState?.Hand.Cards;
            if (cards == null
                || cards.Count > ToolkitHandSnapshotCodec.MaxCards)
            {
                return;
            }

            ToolkitHandCard[] snapshot = cards.Select(card =>
            {
                int cost = card.EnergyCost.CostsX
                    ? short.MinValue
                    : card.EnergyCost.GetWithModifiers(CostModifiers.All);
                return new ToolkitHandCard(
                    card.Id.Entry,
                    (byte)Math.Clamp(card.CurrentUpgradeLevel, 0, byte.MaxValue),
                    (short)Math.Clamp(cost, short.MinValue, short.MaxValue));
            }).ToArray();
            ToolkitDiagnosticsRuntime.PublishHand(snapshot);
        }
        catch
        {
            // No active combat or unsupported card projection: do not send.
        }
    }

    private static F1CategoryDigest[] Project(
        NetFullCombatState snapshot)
    {
        if (snapshot.Players.Count > 16
            || snapshot.Creatures.Count > 48)
        {
            return Enumerable.Range(1, 8)
                .Select(index =>
                    F1SchemaV1.Unsupported((F1Category)index))
                .ToArray();
        }

        Dictionary<ulong, NetFullCombatState.CreatureState> players =
            snapshot.Creatures
                .Where(item => item.playerId.HasValue)
                .GroupBy(item => item.playerId!.Value)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single());
        List<F1Player> playerRows = [];
        List<F1CardCounts> countRows = [];
        bool cardCountsAvailable = true;
        foreach (NetFullCombatState.PlayerState player in snapshot.Players)
        {
            if (!ToolkitDiagnosticsRuntime.TryGetOrdinal(
                    player.playerId,
                    out byte ordinal)
                || !players.TryGetValue(
                    player.playerId,
                    out NetFullCombatState.CreatureState creature))
            {
                return Enumerable.Range(1, 8)
                    .Select(index =>
                        F1SchemaV1.Unsupported((F1Category)index))
                    .ToArray();
            }

            bool hasHand = TryPileCount(
                player,
                PileType.Hand,
                out ushort hand);
            playerRows.Add(new(
                hasHand ? (ushort)0x5F : (ushort)0x1F,
                ordinal,
                creature.currentHp,
                creature.maxHp,
                creature.block,
                player.energy,
                false,
                hand));
            if (hasHand
                && TryPileCount(
                    player,
                    PileType.Draw,
                    out ushort draw)
                && TryPileCount(
                    player,
                    PileType.Discard,
                    out ushort discard)
                && TryPileCount(
                    player,
                    PileType.Exhaust,
                    out ushort exhaust))
            {
                countRows.Add(new(
                    0x1F,
                    ordinal,
                    hand,
                    draw,
                    discard,
                    exhaust));
            }
            else
            {
                cardCountsAvailable = false;
            }
        }

        int round = snapshot.Players.Count == 0
            ? 0
            : snapshot.Players.Max(item => item.turnNumber);
        ushort phase = snapshot.Players.Any(
            item => item.phase == PlayerTurnPhase.Play)
                ? (ushort)2
                : snapshot.Players.All(
                    item => item.phase == PlayerTurnPhase.None)
                    ? (ushort)3
                    : (ushort)4;
        F1Combat combat = new(
            0x7,
            round,
            phase,
            snapshot.lastExecutedActionId.HasValue);

        F1ModContribution[] contributions =
            OptionalDiagnosticsRegistry.SnapshotAtCheckpoint();
        F1CategoryDigest run = RunManager.Instance.DebugOnlyGetState()
            is RunState state
                ? F1SchemaV1.Run(new(
                    0x7,
                    state.CurrentActIndex + 1,
                    state.ActFloor,
                    RoomKind(state.CurrentRoom?.RoomType)))
                : F1SchemaV1.Unsupported(F1Category.RunPublic);
        return
        [
            run,
            F1SchemaV1.Combat(combat),
            F1SchemaV1.Players(playerRows),
            F1SchemaV1.Unsupported(F1Category.MonstersPublic),
            cardCountsAvailable
                ? F1SchemaV1.CardCounts(countRows)
                : F1SchemaV1.Unsupported(
                    F1Category.PublicCardCounts),
            F1SchemaV1.Unsupported(F1Category.PublicEffects),
            ToolkitRngCounter.IsAvailable
                ? F1SchemaV1.RngCounts(ToolkitRngCounter.Snapshot())
                : F1SchemaV1.Unsupported(
                    F1Category.RngConsumptionCounts),
            F1SchemaV1.Contributions(contributions)
        ];
    }

    private static bool TryPileCount(
        NetFullCombatState.PlayerState player,
        PileType type,
        out ushort count)
    {
        count = 0;
        NetFullCombatState.CombatPileState[] matches = player.piles
            .Where(pile => pile.pileType == type)
            .Take(2)
            .ToArray();
        if (matches.Length != 1
            || matches[0].cards.Count > ushort.MaxValue)
        {
            return false;
        }

        count = (ushort)matches[0].cards.Count;
        return true;
    }

    private static ushort RoomKind(RoomType? room) => room switch
    {
        RoomType.Monster => 1,
        RoomType.Elite => 2,
        RoomType.Boss => 3,
        RoomType.Event => 4,
        RoomType.Shop => 5,
        RoomType.RestSite => 6,
        RoomType.Treasure => 7,
        RoomType.Map => 8,
        _ => 0
    };
}

[HarmonyPatch(
    typeof(ChecksumTracker),
    MethodType.Constructor,
    [typeof(INetGameService), typeof(IRunState)])]
internal static class ToolkitCheckpointObserverPatch
{
    private static void Postfix(ChecksumTracker __instance) =>
        __instance.ChecksumGenerated +=
            ToolkitForensicsRuntime.OnCheckpoint;
}

internal static class ToolkitRngPatch
{
    public static void Count(Rng instance, int stream)
    {
        if (!ReferenceEquals(instance, Rng.Chaotic))
        {
            ToolkitRngCounter.Increment(stream);
        }
    }
}

[HarmonyPatch(typeof(Rng), nameof(Rng.NextBool))]
internal static class ToolkitRngBoolPatch
{
    private static void Postfix(Rng __instance) =>
        ToolkitRngPatch.Count(__instance, 0);
}

[HarmonyPatch(typeof(Rng), nameof(Rng.NextInt), [typeof(int)])]
internal static class ToolkitRngIntMaxPatch
{
    private static void Postfix(Rng __instance) =>
        ToolkitRngPatch.Count(__instance, 1);
}

[HarmonyPatch(
    typeof(Rng),
    nameof(Rng.NextInt),
    [typeof(int), typeof(int)])]
internal static class ToolkitRngIntRangePatch
{
    private static void Postfix(Rng __instance) =>
        ToolkitRngPatch.Count(__instance, 2);
}

[HarmonyPatch(
    typeof(Rng),
    nameof(Rng.NextUnsignedInt),
    [typeof(uint), typeof(uint)])]
internal static class ToolkitRngUIntRangePatch
{
    private static void Postfix(Rng __instance) =>
        ToolkitRngPatch.Count(__instance, 3);
}

[HarmonyPatch(
    typeof(Rng),
    nameof(Rng.NextUnsignedLong),
    new Type[] { })]
internal static class ToolkitRngULongPatch
{
    private static void Postfix(Rng __instance) =>
        ToolkitRngPatch.Count(__instance, 4);
}

[HarmonyPatch(
    typeof(Rng),
    nameof(Rng.NextUnsignedLong),
    [typeof(ulong), typeof(ulong)])]
internal static class ToolkitRngULongRangePatch
{
    private static void Postfix(Rng __instance) =>
        ToolkitRngPatch.Count(__instance, 5);
}

[HarmonyPatch(
    typeof(Rng),
    nameof(Rng.NextFloat),
    [typeof(float), typeof(float)])]
internal static class ToolkitRngFloatRangePatch
{
    private static void Postfix(Rng __instance) =>
        ToolkitRngPatch.Count(__instance, 6);
}

[HarmonyPatch(
    typeof(Rng),
    nameof(Rng.NextDouble),
    new Type[] { })]
internal static class ToolkitRngDoublePatch
{
    private static void Postfix(Rng __instance) =>
        ToolkitRngPatch.Count(__instance, 7);
}

[HarmonyPatch(
    typeof(Rng),
    nameof(Rng.NextDouble),
    [typeof(double), typeof(double)])]
internal static class ToolkitRngDoubleRangePatch
{
    private static void Postfix(Rng __instance) =>
        ToolkitRngPatch.Count(__instance, 8);
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Combat.History.CombatHistory))]
[HarmonyPatch(nameof(
    MegaCrit.Sts2.Core.Combat.History.CombatHistory.DamageReceived))]
internal static class ToolkitContributionDamagePatch
{
    private static readonly ConditionalWeakTable<DamageResult, object>
        Seen = new();
    private static long _event;

    private static void Postfix(
        Creature? dealer,
        DamageResult result)
    {
        try
        {
            if (dealer?.Player == null
                || !Seen.TryAdd(result, new object())
                || !ToolkitDiagnosticsRuntime.TryGetOrdinal(
                    dealer.Player.NetId,
                    out byte ordinal))
            {
                return;
            }

            ulong eventId = (2UL << 60)
                | (unchecked((ulong)Interlocked.Increment(ref _event))
                    & 0x0FFF_FFFF_FFFF_FFFFUL);
            ToolkitDiagnosticsRuntime.RecordContribution(
                ordinal,
                eventId,
                damage: (ulong)Math.Max(0, result.UnblockedDamage),
                kills: result.WasTargetKilled ? 1UL : 0UL);
        }
        catch
        {
            // Optional entertainment counters never affect combat.
        }
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Combat.History.CombatHistory))]
[HarmonyPatch(nameof(
    MegaCrit.Sts2.Core.Combat.History.CombatHistory.BlockGained))]
internal static class ToolkitContributionBlockPatch
{
    private static long _event;

    private static void Postfix(
        Creature receiver,
        int amount,
        CardPlay? cardPlay)
    {
        try
        {
            if (amount <= 0
                || cardPlay?.Player == null
                || receiver.Player?.NetId == cardPlay.Player.NetId
                || !ToolkitDiagnosticsRuntime.TryGetOrdinal(
                    cardPlay.Player.NetId,
                    out byte ordinal))
            {
                return;
            }

            ulong eventId = (3UL << 60)
                | (unchecked((ulong)Interlocked.Increment(ref _event))
                    & 0x0FFF_FFFF_FFFF_FFFFUL);
            ToolkitDiagnosticsRuntime.RecordContribution(
                ordinal,
                eventId,
                defenseGiven: (ulong)amount);
        }
        catch
        {
            // Unattributable effects are intentionally ignored.
        }
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
internal static class ToolkitContributionHealPatch
{
    private sealed record State(
        Creature Receiver,
        int BeforeHp,
        byte OwnerOrdinal,
        ulong EventId);

    private static long _event;

    private static void Prefix(Creature creature, out State? __state)
    {
        __state = null;
        try
        {
            GameAction? action =
                RunManager.Instance.ActionExecutor?.CurrentlyRunningAction;
            if (action == null
                || creature.Player == null
                || action.OwnerId == creature.Player.NetId
                || !ToolkitDiagnosticsRuntime.TryGetOrdinal(
                    action.OwnerId,
                    out byte ordinal))
            {
                return;
            }

            ulong eventId = (4UL << 60)
                | (unchecked((ulong)Interlocked.Increment(ref _event))
                    & 0x0FFF_FFFF_FFFF_FFFFUL);
            __state = new(creature, creature.CurrentHp, ordinal, eventId);
        }
        catch
        {
            // Effects without a reliable native action owner are unassigned.
        }
    }

    private static void Postfix(State? __state, ref Task __result)
    {
        if (__state != null)
        {
            __result = Observe(__result, __state);
        }
    }

    private static async Task Observe(Task original, State state)
    {
        await original.ConfigureAwait(false);
        int actual = Math.Max(0, state.Receiver.CurrentHp - state.BeforeHp);
        if (actual > 0)
        {
            ToolkitDiagnosticsRuntime.RecordContribution(
                state.OwnerOrdinal,
                state.EventId,
                healingGiven: (ulong)actual);
        }
    }
}
