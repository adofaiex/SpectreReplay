using System;
using UnityEngine;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 两段式破坏性按钮（第一下切成确认态，第二下才算数）。
    ///
    /// <para><b>为什么抽成组件。</b>这份交互在本仓库出现了三处：回放列表的删除、
    /// 云端列表的删除、去特效预设的删除。三处的**语义**不同（删文件 / 删云端副本 /
    /// 删预设，各自的待确认状态、超时和后续动作都不一样），但**外观与点击协议**
    /// 完全一样：确认态换文案、换高亮样式、宽度按文案实测、悬停说明、按下才算数。
    /// 抄三份的代价不是行数——是「三颗按钮的确认态样式悄悄分叉」：玩家在某一页
    /// 按到一颗看着不像确认态的确认按钮，就会以为第一次点击没生效。</para>
    ///
    /// <para><b>这个组件不管状态。</b>待确认态归调用方所有（那是业务，
    /// 见 `Pages._pendingDelete` 的注释：存路径而不是 bool，因为玩家先点 A 再点 B
    /// 时 B 必须重新走第一段）。组件只收一个 `armed` 位、返回「这次到底有没有被点」，
    /// 两段式的判定留在各调用点——**形态留在看得见的地方**，闸门 ㉒b 也要看得见它。</para>
    /// </summary>
    internal static class ConfirmButton
    {
        /// <summary>
        /// 画一颗两段式按钮。
        ///
        /// <param name="armed">调用方的待确认状态是否指向本行。</param>
        /// <param name="labelKey">第一段文案键（通常是「删除」）。</param>
        /// <param name="confirmKey">确认态文案键（通常是「确认删除」）。</param>
        /// <param name="tipKey">悬停说明键；<c>null</c> 表示这一处原本就没有悬停说明。</param>
        /// <param name="minWidth">按文案实测宽度的下限（三处都是 70f）。</param>
        /// <param name="height">行高（三处都是 26f）。</param>
        /// <returns>本次这一帧是否被点了一下——**两种状态都返回 true**，
        /// 由调用方判断 `armed` 决定「进确认态」还是「真执行」。</returns>
        /// </summary>
        internal static bool Draw(bool armed, string labelKey, string confirmKey, string tipKey,
            float minWidth, float height)
        {
            string label = Loc.T(armed ? confirmKey : labelKey);
            GUIStyle style = armed ? Gui.AccentButtonStyle : Gui.QuietButtonStyle;
            // 宽度按文案实测：确认态的「确认删除」在德语/俄语下比 110px 宽。
            // 显式取矩形而不是让 GUILayout.Button 自己排：Gui.Tip 需要一个 Rect。
            Rect r = Gui.ButtonRect(label, style, minWidth, height);
            if (tipKey != null)
            {
                Gui.Tip(r, tipKey);
            }
            return GUI.Button(r, label, style);
        }
    }
}
