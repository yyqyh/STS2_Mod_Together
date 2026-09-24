using System;
using System.Collections.Generic;
using System.Reflection;

using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
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
    /// <summary><c>PowerModel</c> 上那个私有的 <c>object? _internalData</c>。</summary>
    private static readonly AccessTools.FieldRef<PowerModel, object?> InternalData =
        AccessTools.FieldRefAccess<PowerModel, object?>("_internalData");

    private static readonly Dictionary<Type, FieldInfo[]> FieldsCache = [];

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

            if (theirs is null || mine is null || ReferenceEquals(theirs, mine))
            {
                return;
            }

            Merge(theirs, mine);
        }
        catch (Exception)
        {
            // 同步失败不影响钩子继续跑（最坏是这一份数据落后一档）。
        }
    }

    /// <summary>把 <paramref name="source" /> 里"<paramref name="target" /> 还空着"的字段补上；已经有的值一律不动。</summary>
    /// <remarks>
    /// 逐字段：副本是空/默认（null、0、false、空集合）而原件不是 → 补上（集合另建一份）；
    /// 两边都是集合 → 把"原件有、副本没有"的元素并进去（幂等）；其它（副本已有值）→ <b>保持副本自己的值</b>。
    /// </remarks>
    private static void Merge(object source, object target)
    {
        foreach (var field in FieldsOf(source.GetType()))
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

        foreach (var field in FieldsOf(payload.GetType()))
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

        foreach (var field in FieldsOf(type))
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

    private static FieldInfo[] FieldsOf(Type type)
    {
        if (FieldsCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        FieldsCache[type] = fields;
        return fields;
    }
}
