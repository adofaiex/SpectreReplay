using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SpectreReplay.Features.Cloud;
using SpectreReplay.Features.EffectRemover;
using SpectreReplay.Features.Replay;
using SpectreReplay.Features.VideoRender;
using SpectreReplay.Ui;
using UnityFileDialog;
using UnityEngine;
using static SpectreReplay.Settings;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Pages
{
    /// <summary>
    /// 去特效页：总开关、允许覆盖关卡、各分组开关。
    /// 控件顺序按「风险从低到高」排，不要重排。
    /// </summary>
    internal static partial class Pages
    {
        /// <summary>
        /// 去特效页。
        ///
        /// **为什么单独一页而不是塞进录像页。** 这里有 27 个开关，全塞进任何一页都会
        /// 把原有的设置挤到几屏之外——而这一页本身**是给高级用途的**：想让谱面看起来
        /// 干净、或者机器跑不动渲染时减负。单独一页还有个附带好处：总开关关掉时它
        /// 就是个普通页签，玩家不会误以为"录制功能坏了"。
        ///
        /// **控件顺序按"风险从低到高"排。** 最前面是总开关与「允许存盘」（这两项
        /// 决定这次操作会不会改到磁盘），后面才是各个具体项——把破坏性选项（移除装饰物、
        /// 重置轨道颜色）摆在最上面，玩家顺手打开就会把谱面的美术全抹了。
        /// </summary>
        internal static void DrawEffects()
        {
            // ── 「全选本组」只给 visual / track / planet 三组 ──────────────
            // 口径是**这一组是不是一组同质的「要摘掉的事件类别」**，不是开关数量：
            //   · visual / track / planet：整组都是「摘掉某类事件」，一键同开同关有意义；
            //   · master：含总开关自身（自我指涉）与「允许覆盖关卡」授权，
            //     一键把破坏性授权也打开，正是这页刻意避免的；
            //   · decor：两项里「移除全部装饰」是清空级动作，不该被顺手打开；
            //   · audio：只有两个开关，多一次点击反而更慢；
            //   · gameplay：改的是运行逻辑不是画面，不该和视觉项共用一颗按钮。
            // 口径若改，同步改 tools\UiPreview\UiLogicShapeCheck.cs 的 ⑱f。
            Gui.Section("effects.master", Gui.Accent, () =>
            {
                OnGui.Row("effects.on", EffectRemoverOn, v =>
                {
                    EffectRemoverOn = v;
                    Commit();
                    if (!v)
                    {
                        // 先还原原始谱面，再刷新按钮：还原成功后 LevelDataStripped
                        // 归零，保存按钮才会真正恢复可用。
                        EffectRemover.RestoreOriginalLevelData();
                        EffectRemover.RestoreEditorSaveButtons();
                    }
                    else
                    {
                        EffectRemover.RefreshEditorSaveButtons();
                    }
                });
                OnGui.Note("effects.on.note");
                OnGui.Row("effects.enablesave", EffectRemoverEnableSave,
                    v =>
                    {
                        EffectRemoverEnableSave = v;
                        Commit();
                        EffectRemover.RefreshEditorSaveButtons();
                    }, EffectRemoverOn);
                OnGui.Note("effects.enablesave.note");
            });

            Gui.Section("effects.presets", Gui.Accent, () =>
            {
                OnGui.Note("effects.presets.note");
                RefreshPresets();
                if (_presetOptions.Length == 0)
                {
                    OnGui.Note("effects.preset.none");
                }
                else
                {
                    OnGui.RowDropdown("effects.preset.select", _presetOptions, _presetSel,
                        v => _presetSel = v);
                    OnGui.Button("effects.preset.apply", ApplySelectedPreset, true);
                    DrawPresetDeleteButton();
                }

                // 名称框照抄搜索行的画法：GUILayout 定位、自绘框画在组外，
                // 否则鼠标处理会和水平组打架（见 DrawSearchRow 注释）。
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("effects.preset.name"), Gui.LabelStyle,
                    GUILayout.Width(Gui.LabelW));
                Rect nameBox = GUILayoutUtility.GetRect(Gui.CtrlW, 24f,
                    GUILayout.Width(Gui.CtrlW), GUILayout.Height(24f));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                bool canceled;
                _presetName.Draw(nameBox, Gui.LabelStyle,
                    Loc.T("effects.preset.name.empty"), out canceled);
                if (canceled)
                {
                    _presetName.Text = "";
                    _presetName.Editing = false;
                }
                OnGui.Button("effects.preset.save", SaveCurrentPreset, true);
                OnGui.Button("effects.preset.rename", RenameSelectedPreset, true);
                OnGui.Button("effects.preset.upload", UploadSelectedPreset, true);
                OnGui.Button("effects.preset.pull", PullCloudPresets, true);
                OnGui.Button("effects.preset.openfolder", () => EffectPresets.OpenFolder(), false);
            });

            Gui.Section("effects.visual", Gui.Accent, () =>
            {
                // 一键全选本组（双向，见 DrawEffects 开头那段口径）。
                Gui.Dim(!EffectRemoverOn, () => OnGui.Button("effects.toggleall", () =>
                {
                    bool on = !(EffectRemoverFilters || EffectRemoverAdvancedFilters
                             || EffectRemoverParticles || EffectRemoverBackgrounds
                             || EffectRemoverCameras);
                    EffectRemoverFilters = on;
                    EffectRemoverAdvancedFilters = on;
                    EffectRemoverParticles = on;
                    EffectRemoverBackgrounds = on;
                    EffectRemoverCameras = on;
                    Commit();
                }, false, EffectRemoverOn));
                OnGui.Row("effects.filters", EffectRemoverFilters, v => { EffectRemoverFilters = v; Commit(); });
                OnGui.Row("effects.advfilters", EffectRemoverAdvancedFilters, v => { EffectRemoverAdvancedFilters = v; Commit(); });
                OnGui.Row("effects.particles", EffectRemoverParticles, v => { EffectRemoverParticles = v; Commit(); });
                OnGui.Row("effects.backgrounds", EffectRemoverBackgrounds, v => { EffectRemoverBackgrounds = v; Commit(); });
                OnGui.Row("effects.cameras", EffectRemoverCameras, v => { EffectRemoverCameras = v; Commit(); });
                OnGui.Row("effects.setcamerazoom", EffectRemoverSetCameraZoom,
                    v => { EffectRemoverSetCameraZoom = v; Commit(); },
                    EffectRemoverOn && EffectRemoverCameras);
                OnGui.RowSlider("effects.camerazoom", EffectRemoverCameraZoomScale, 100f, 1000f,
                    v => ((int)v) + " %", v => { EffectRemoverCameraZoomScale = v; Commit(); },
                    EffectRemoverOn && EffectRemoverCameras && EffectRemoverSetCameraZoom);
            }, EffectRemoverOn);

            Gui.Section("effects.decor", Gui.Accent, () =>
            {
                OnGui.Row("effects.decorations", EffectRemoverDecorations, v => { EffectRemoverDecorations = v; Commit(); });
                OnGui.Row("effects.removealldecor", EffectRemoverRemoveAllDecorations,
                    v => { EffectRemoverRemoveAllDecorations = v; Commit(); },
                    EffectRemoverOn && EffectRemoverDecorations);
                OnGui.Note("effects.decor.note");
            }, EffectRemoverOn);

            Gui.Section("effects.track", Gui.Accent, () =>
            {
                // 一键全选本组（上游 effectremover.toggleall 同形）。
                // 放在组首：这是「先决定这一组要不要」的动作，不是某一项的配套。
                // 受总开关管——总开关关着时按下去等于什么都没发生，会误导。
                // **双向**：本组已有一项开着，按下去是全部关掉；全关着才全部打开。
                // 单向「只开不关」的话，玩家批量打开后想撤销只能逐项去点。
                //
                // **只覆盖「摘掉某类事件」的四项**，不含三个 reset：
                // resettrackopacity/resettrackanim 是改写残余事件的参数，
                // 不是「多摘一类」，混进全选会变成「顺手改了参数」。
                Gui.Dim(!EffectRemoverOn, () => OnGui.Button("effects.toggleall", () =>
                {
                    bool on = !(EffectRemoverTrackAnimations || EffectRemoverTrackPositions
                             || EffectRemoverTrackMoves || EffectRemoverTrackColors);
                    EffectRemoverTrackAnimations = on;
                    EffectRemoverTrackPositions = on;
                    EffectRemoverTrackMoves = on;
                    EffectRemoverTrackColors = on;
                    Commit();
                }, false, EffectRemoverOn));
                OnGui.Row("effects.trackanim", EffectRemoverTrackAnimations, v => { EffectRemoverTrackAnimations = v; Commit(); });
                // 顺序照上游：move 在 pos 之前（move 是最常见的一类，排前面）。
                OnGui.Row("effects.trackmove", EffectRemoverTrackMoves, v => { EffectRemoverTrackMoves = v; Commit(); });
                OnGui.Row("effects.trackpos", EffectRemoverTrackPositions, v => { EffectRemoverTrackPositions = v; Commit(); });
                OnGui.Row("effects.trackcolor", EffectRemoverTrackColors, v => { EffectRemoverTrackColors = v; Commit(); });
                OnGui.Row("effects.resettrackopacity", EffectRemoverResetTrackOpacity,
                    v => { EffectRemoverResetTrackOpacity = v; Commit(); });
                // 两个「重置」各自从属同一个父项（上游同）：
                // 父项关着时对应事件不会被摘，重置也就没有对象可改。
                OnGui.Row("effects.resettrackanim", EffectRemoverResetTrackAnimation,
                    v => { EffectRemoverResetTrackAnimation = v; Commit(); },
                    EffectRemoverOn && EffectRemoverTrackAnimations);
                OnGui.Row("effects.resettrackcolor", EffectRemoverResetTrackColor,
                    v => { EffectRemoverResetTrackColor = v; Commit(); },
                    EffectRemoverOn && EffectRemoverTrackColors);
            }, EffectRemoverOn);

            Gui.Section("effects.planet", Gui.Accent, () =>
            {
                // 一键全选本组（同上）。
                // **双向**，同轨道组。四项都属于「摘掉一类行星相关事件」，
                // hideicons 摘的是 Hide 事件，同质，留在同一颗按钮里。
                Gui.Dim(!EffectRemoverOn, () => OnGui.Button("effects.toggleall", () =>
                {
                    bool on = !(EffectRemoverPlanetOrbit || EffectRemoverPlanetScale
                             || EffectRemoverPlanetRadius || EffectRemoverHideIcons);
                    EffectRemoverPlanetOrbit = on;
                    EffectRemoverPlanetScale = on;
                    EffectRemoverPlanetRadius = on;
                    EffectRemoverHideIcons = on;
                    Commit();
                }, false, EffectRemoverOn));
                OnGui.Row("effects.planetorbit", EffectRemoverPlanetOrbit, v => { EffectRemoverPlanetOrbit = v; Commit(); });
                OnGui.Row("effects.planetscale", EffectRemoverPlanetScale, v => { EffectRemoverPlanetScale = v; Commit(); });
                OnGui.Row("effects.planetradius", EffectRemoverPlanetRadius, v => { EffectRemoverPlanetRadius = v; Commit(); });
                OnGui.Row("effects.hideicons", EffectRemoverHideIcons, v => { EffectRemoverHideIcons = v; Commit(); });
            }, EffectRemoverOn);

            Gui.Section("effects.audio", Gui.Accent, () =>
            {
                OnGui.Row("effects.hitsounds", EffectRemoverHitSounds, v => { EffectRemoverHitSounds = v; Commit(); });
                OnGui.Row("effects.holdsounds", EffectRemoverHoldSounds, v => { EffectRemoverHoldSounds = v; Commit(); });
            }, EffectRemoverOn);

            Gui.Section("effects.gameplay", Gui.Accent, () =>
            {
                OnGui.Row("effects.checkpoints", EffectRemoverCheckpoints, v => { EffectRemoverCheckpoints = v; Commit(); });
                OnGui.Row("effects.inputevents", EffectRemoverInputEvents, v => { EffectRemoverInputEvents = v; Commit(); });
                OnGui.Row("effects.framerate", EffectRemoverFrameRate, v => { EffectRemoverFrameRate = v; Commit(); });
                OnGui.Row("effects.repeatevents", EffectRemoverRepeatEvents, v => { EffectRemoverRepeatEvents = v; Commit(); });
                OnGui.Note("effects.gameplay.note");
            }, EffectRemoverOn);
        }
    }
}
