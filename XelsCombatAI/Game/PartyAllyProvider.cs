using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Game;

internal sealed record PartyAllySnapshot(
    IReadOnlyList<IBattleChara> Members,
    int PartyListMembers,
    int DutySupportMembers);

internal static class PartyAllyProvider
{
    public static IEnumerable<IBattleChara> EnumerateVisiblePartyAllies(DalamudServices services, IBattleChara player)
    {
        return GetVisiblePartyAllies(services, player).Members;
    }

    public static PartyAllySnapshot GetVisiblePartyAllies(DalamudServices services, IBattleChara player)
    {
        var seen = new HashSet<ulong> { player.GameObjectId };
        var members = new List<IBattleChara>();
        var partyListMembers = 0;
        var dutySupportMembers = 0;

        foreach (var member in services.PartyList)
        {
            if (member.GameObject is IBattleChara actor &&
                IsUsableAlly(actor) &&
                seen.Add(actor.GameObjectId))
            {
                members.Add(actor);
                partyListMembers++;
                if (IsDutySupportPartyMember(actor))
                {
                    dutySupportMembers++;
                }
            }
        }

        foreach (var actor in services.ObjectTable.OfType<IBattleChara>())
        {
            if (seen.Contains(actor.GameObjectId) ||
                !IsUsableAlly(actor) ||
                !IsDutySupportPartyMember(actor))
            {
                continue;
            }

            seen.Add(actor.GameObjectId);
            members.Add(actor);
            dutySupportMembers++;
        }

        return new PartyAllySnapshot(members, partyListMembers, dutySupportMembers);
    }

    private static bool IsUsableAlly(IBattleChara actor)
    {
        return !actor.IsDead &&
               actor.CurrentHp > 0 &&
               actor.ObjectKind is ObjectKind.Pc or ObjectKind.BattleNpc;
    }

    private static bool IsDutySupportPartyMember(IBattleChara actor)
    {
        return actor.ObjectKind == ObjectKind.BattleNpc &&
               actor is IBattleNpc npc &&
               npc.BattleNpcKind == BattleNpcSubKind.NpcPartyMember;
    }
}
