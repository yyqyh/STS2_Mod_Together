using System;
using System.Collections.Generic;

using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Together.Core.Utils;
using Together;

namespace Together.Core.Combat;

/// <summary>把能力原件的"内部数据"（<c>PowerModel._internalData</c>）搬到镜像副本上。</summary>
/// <remarks>
/// 本体的克隆契约是"<b>内部数据在克隆时被重置</b>"（<c>DeepCloneFields()</c> 里
/// <c>_internalData = InitInternalData()</c>；<c>GetInternalData</c> 的注释也写明会 reset）。
/// 于是"带实例私有状态"的能力克隆出来是空壳 —— 夜魇的 <c>selectedCard</c> 变 null →
/// <c>BeforeHandDraw</c> 里 NRE 打死回合循环。本体那套是给"自己人"用的（克隆后由模型自己重填），
/// 而我们是替另一个宿主造副本、没有重演填充过程的机会 → 只能把原件那份搬过去。
/// <b>三级降级，永不抛</b>：① <b>反射浅拷贝</b>（新建同类型 <c>Data</c> 逐字段复制；集合/字典/数组各建一份，
/// 其它引用共享）；② 复制失败（无参构造不可用、字段只读等）→ <b>退回共享整份数据</b>；
/// ③ 连赋值都失败 → <b>放弃这次镜像</b>，调用方跳过，宁可"少一份"也不崩。
/// <b>残余风险</b>：数据里若存"指向原宿主"的引用，拷贝后仍指向原件宿主 → 副本可能作用到错的人。
/// 反射分不出"指向自己"和"指向别人"，只能靠实测发现。
/// </remarks>
internal static class PowerPayload
{
    /// <summary>每个能力类型"可搬运的自身字段"（缓存）。</summary>
    private static readonly Dictionary<Type, System.Reflection.FieldInfo[]> CarriableCache = [];

    /// <summary>已经因为"类型搬不动"而报过警的字段（每种类型+字段只报一次）。</summary>
    private static readonly HashSet<string> Skipped = [];

    /// <summary><c>PowerModel</c> 上那个私有的 <c>object? _internalData</c>。</summary>
    private static readonly AccessTools.FieldRef<PowerModel, object?> InternalData =
        AccessTools.FieldRefAccess<PowerModel, object?>("_internalData");

    /// <summary>把 <paramref name="source" /> 的内部数据搬到 <paramref name="clone" /> 上。</summary>
    /// <returns><c>false</c> = 搬不了，调用方应当跳过这一份镜像（不崩，只是不共享）。</returns>
    public static bool TryCopy(PowerModel source, PowerModel clone)
    {
        object? payload;
        try
        {
            payload = InternalData(source);
        }
        catch (Exception)
        {
            return false;
        }

        // 没有内部数据 → 副本本来就不缺东西，直接放行。
        if (payload is null)
        {
            return true;
        }

        var carried = TryDeepCopy(payload) ?? payload;

        try
        {
            InternalData(clone) = carried;

            if (!ReferenceEquals(carried, payload))
            {
                CappedLog.Info(
                    "power.payload",
                    $"能力内部数据随镜像搬运：{source.GetType().Name}"
                    + $"（{payload.GetType().Name} 浅拷贝一份）");
            }

            return true;
        }
        catch (Exception ex)
        {
            Main.Logger.Warn(
                $"[together] 能力内部数据搬运失败（跳过这一份镜像）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>派发钩子之前，把原件"有、副本还空着"的字段补上。</summary>
    /// <remarks>
    /// 不能只在克隆时拷一次：很多能力的数据是"施加<b>之后</b>"才填的（夜魇的 <c>SetSelectedCard</c>
    /// 在 <c>PowerCmd.Apply</c> 返回之后），而镜像发生在 Apply <b>内部</b> → 克隆那刻是空数据。
    /// <b>必须"只补空、不覆盖"</b>：余像/潘塔奇这类"<b>成对记录</b>"型能力（<c>BeforeCardPlayed</c> 写、
    /// <c>AfterCardPlayed</c> 取）会被整份覆盖冲掉自己刚写的状态 → 副本完全失效（实测：余像不给格挡）。
    /// </remarks>
    public static void Sync(PowerModel source, PowerModel clone)
    {
        try
        {
            var theirs = InternalData(source);
            var mine = InternalData(clone);

            if (theirs is not null && mine is not null && !ReferenceEquals(theirs, mine))
            {
                Merge(theirs, mine);
            }
        }
        catch (Exception)
        {
            // 同步失败不影响钩子继续跑（最坏是这一份数据落后一档）。
        }

        // 自身字段同样"只补空"：很多能力的状态是在 Apply **之后**才填的（模仿学习的 PlayerTarget 就是），
        // 克隆那一刻副本拿到的是 null，只能在这里补。
        SyncFields(source, clone);
    }

    /// <summary>把原件"有、副本还空着"的<b>自身字段</b>补上（克隆时一次、之后每次派发前一次）。</summary>
    /// <remarks>
    /// 为什么需要：本体的克隆契约只重置 <c>_internalData</c>，派生类自己的字段是浅拷贝过来的 ——
    /// 于是"Apply 之后才填"的字段（<c>ImitationLearningPower._playerTarget</c>）副本永远拿不到，
    /// 轻则能力失效，重则副本在钩子里抛异常把整次出牌打断（实测）。
    /// <b>只补"空/默认"值</b>，副本自己写进去的一律不动（否则会冲掉"成对记录"型能力的状态，见 <see cref="Sync" />）。
    /// <b>能搬的只有"值类型 + 身份对象"</b>（模型 / Creature / Player / 战斗状态）：这些在两端是同一个对象，语义正确；
    /// Godot 节点、委托、指令类、集合这些"运行时句柄"一律跳过 —— 复制会让副本去操作<b>原件</b>的节点/指令（形态类特效就是这一条）。
    /// </remarks>
    public static void SyncFields(PowerModel source, PowerModel clone)
    {
        foreach (var field in CarriableFields(source.GetType()))
        {
            object? mine;
            try
            {
                mine = field.GetValue(clone);
            }
            catch (Exception)
            {
                continue;
            }

            if (!IsEmpty(mine))
            {
                continue;
            }

            object? theirs;
            try
            {
                theirs = field.GetValue(source);
            }
            catch (Exception)
            {
                continue;
            }

            if (IsEmpty(theirs))
            {
                continue;
            }

            try
            {
                var value = Redirect(theirs, source, clone);
                if (value is not null)
                {
                    field.SetValue(clone, value);
                    CappedLog.Info(
                        "power.payload.field",
                        $"能力自身字段随镜像搬运：{source.GetType().Name}.{field.Name}"
                        + $"（{field.FieldType.Name}）");
                }
            }
            catch (Exception)
            {
                // readonly / 只写字段 → 保持原样。
            }
        }
    }

    /// <summary>可搬运的自身字段：<b>非 PowerModel 基类声明</b>（那些是每份各自的基础设施）且类型能安全共享。</summary>
    private static System.Reflection.FieldInfo[] CarriableFields(Type type)
    {
        if (CarriableCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var fields = new List<System.Reflection.FieldInfo>();
        foreach (var field in ModelAccess.FieldsOf(type))
        {
            // 基类那份基础设施（_owner/_amount/_applier/_target/_dynamicVars/_internalData/…）不搬：
            // 判据用 DeclaringType，所以新版本体加字段也不会被误搬。
            if (field.IsStatic || field.DeclaringType == typeof(PowerModel))
            {
                continue;
            }

            if (!CanCarry(field.FieldType))
            {
                if (Skipped.Add($"{type.Name}.{field.Name}"))
                {
                    CappedLog.Info(
                        "power.payload.skip",
                        $"{type.Name}.{field.Name}（{field.FieldType.Name}）不参与镜像搬运："
                        + "只搬值类型与身份对象，运行时句柄（节点/指令/委托/集合）交给副本自己重建");
                }

                continue;
            }

            fields.Add(field);
        }

        var result = fields.ToArray();
        CarriableCache[type] = result;
        return result;
    }

    /// <summary>这个类型能不能在两个宿主之间共享同一个值。</summary>
    private static bool CanCarry(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        return t.IsValueType
               || t == typeof(string)
               || typeof(AbstractModel).IsAssignableFrom(t)
               || typeof(Creature).IsAssignableFrom(t)
               || typeof(Player).IsAssignableFrom(t)
               || typeof(ICombatState).IsAssignableFrom(t)
               || typeof(IRunState).IsAssignableFrom(t);
    }

    /// <summary>把"指向原件宿主身上那份东西"的引用改到副本宿主身上；改不了就让这个字段留空。</summary>
    private static object? Redirect(object? value, PowerModel source, PowerModel clone)
    {
        if (value is PowerModel other
            && source.Owner is { } sourceOwner
            && ReferenceEquals(other.Owner, sourceOwner)
            && clone.Owner is { } cloneOwner)
        {
            // 原件把"自己身上的另一份能力"记在字段里（例如临时力量记的内部力量）→ 副本应该记副本自己那份。
            return PowerMirror.FindCounterpart(sourceOwner, cloneOwner, other);
        }

        return value;
    }

    /// <summary>把 <paramref name="source" /> 里"<paramref name="target" /> 还空着"的字段补上；已经有的值一律不动。</summary>
    /// <remarks>
    /// 逐字段：副本是空/默认（null、0、false、空集合）而原件不是 → 补上（集合另建一份）；
    /// 两边都是集合 → 把"原件有、副本没有"的元素并进去（幂等）；其它（副本已有值）→ <b>保持副本自己的值</b>。
    /// </remarks>
    private static void Merge(object source, object target)
    {
        foreach (var field in ModelAccess.FieldsOf(source.GetType()))
        {
            object? theirs;
            object? mine;
            try
            {
                theirs = field.GetValue(source);
                mine = field.GetValue(target);
            }
            catch (Exception)
            {
                continue;
            }

            if (IsEmpty(theirs))
            {
                continue;
            }

            if (IsEmpty(mine))
            {
                try
                {
                    field.SetValue(target, CopyValue(theirs));
                }
                catch (Exception)
                {
                    // readonly 等 → 保持原样。
                }

                continue;
            }

            // 两边都非空：集合按元素并（原件独有的并进去），标量/引用保持副本自己的值。
            try
            {
                switch (mine, theirs)
                {
                    case (System.Collections.IDictionary mineMap, System.Collections.IDictionary theirMap):
                        foreach (System.Collections.DictionaryEntry entry in theirMap)
                        {
                            if (!mineMap.Contains(entry.Key))
                            {
                                mineMap[entry.Key] = entry.Value;
                            }
                        }

                        break;

                    case (System.Collections.IList mineList, System.Collections.IList theirList):
                        foreach (var item in theirList)
                        {
                            if (!mineList.Contains(item))
                            {
                                mineList.Add(item);
                            }
                        }

                        break;
                }
            }
            catch (Exception)
            {
                // 集合类型不支持写入 → 忽略（保持副本自己的值）。
            }
        }
    }

    /// <summary>这份能力的内部数据里有没有直接引用 <paramref name="target" />（含集合元素）。</summary>
    /// <remarks>
    /// 给"镜像副本产出的牌该落谁手里"当判据用（见 <see cref="PowerMirror.MirrorOriginOfGeneratedCard" />）：
    /// 只看<b>直接字段</b>，不做深挖 —— 夜宴那种"载荷里存着选中的牌"就是一层字段。
    /// </remarks>
    internal static bool References(PowerModel power, object? target)
    {
        if (target is null)
        {
            return false;
        }

        object? payload;
        try
        {
            payload = InternalData(power);
        }
        catch (Exception)
        {
            return false;
        }

        if (payload is null)
        {
            return false;
        }

        foreach (var field in ModelAccess.FieldsOf(payload.GetType()))
        {
            object? value;
            try
            {
                value = field.GetValue(payload);
            }
            catch (Exception)
            {
                continue;
            }

            if (ReferenceEquals(value, target))
            {
                return true;
            }

            if (value is System.Collections.IEnumerable items and not string)
            {
                foreach (var item in items)
                {
                    if (ReferenceEquals(item, target))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>这个字段值算不算"空/默认"。</summary>
    private static bool IsEmpty(object? value)
    {
        switch (value)
        {
            case null:
            case string { Length: 0 }:
                return true;
            case bool flag:
                return !flag;
            case int number:
                return number == 0;
            case long number:
                return number == 0L;
            case decimal number:
                return number == 0m;
            case double number:
                return number == 0d;
            case float number:
                return number == 0f;
            case System.Collections.ICollection collection:
                return collection.Count == 0;
            default:
                return false;
        }
    }

    /// <summary>尽量新建一份等价的内部数据；不行返回 <c>null</c>（调用方退回共享引用）。</summary>
    private static object? TryDeepCopy(object payload)
    {
        var type = payload.GetType();

        object? fresh;
        try
        {
            fresh = Activator.CreateInstance(type, nonPublic: true);
        }
        catch (Exception)
        {
            return null;
        }

        if (fresh is null)
        {
            return null;
        }

        foreach (var field in ModelAccess.FieldsOf(type))
        {
            object? value;
            try
            {
                value = field.GetValue(payload);
            }
            catch (Exception)
            {
                return null;
            }

            try
            {
                field.SetValue(fresh, CopyValue(value));
            }
            catch (Exception)
            {
                // readonly / 只写字段等 → 整份退回"共享引用"。
                return null;
            }
        }

        return fresh;
    }

    /// <summary>值类型与字符串原样；集合类各建一份；其它引用共享同一个对象。</summary>
    private static object? CopyValue(object? value)
    {
        if (value is null || value is string || value.GetType().IsValueType)
        {
            return value;
        }

        var type = value.GetType();

        try
        {
            return type.IsArray
                ? ((Array)value).Clone()
                : Activator.CreateInstance(type, value);
        }
        catch (Exception)
        {
            // 牌 / 角色 / 玩家 / 元组…：没有"从同类拷贝"的构造 → 共享同一个对象。
            return value;
        }
    }

}
