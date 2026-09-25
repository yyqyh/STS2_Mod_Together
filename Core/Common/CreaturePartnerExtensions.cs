using MegaCrit.Sts2.Core.Entities.Creatures;
using Together.Core.Alignment;
using Together.Core.Diagnostics;
using Together.Core.Foundation;
using Together.Core.Shared.Body;
using Together.Core.Shared.Gold;
using Together.Core.Shared.Orb;
using Together.Core.Shared.Pet;
using Together.Core.Shared.Power;
using Together.Core.Ui;

namespace Together.Core.Common;
/// <summary>共享身体相关的小工具。</summary>
internal static class CreaturePartnerExtensions
{
    /// <summary>
    /// 这个 creature 所在共生体里，除它以外的<b>其他成员</b>；不是共享局 / 不是玩家 creature 时为空。
    /// </summary>
    /// <remarks>镜像（血量/格挡/能力）就是往这些人身上推，所以共生体支持 2~4 人。</remarks>
    public static IEnumerable<Creature> OthersOrEmpty(this Creature creature)
    {
        return TogetherPair.OtherCreaturesOf(creature);
    }
}
