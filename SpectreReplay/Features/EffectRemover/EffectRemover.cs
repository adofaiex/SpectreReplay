using System;
using System.Collections.Generic;
using ADOFAI;
using UnityEngine.SceneManagement;

namespace SpectreReplay.Features.EffectRemover;

/// <summary>
/// 去特效：在关卡数据被解码之后、事件真正执行之前，把指定的特效事件从
/// <see cref="LevelData.levelEvents"/> 里摘掉。
///
/// **为什么在 Decode 之后动手，而不是拦截执行。** 关卡事件在运行期是一条条
/// 按时间轴触发的，要拦就得给每一种事件都挂一个补丁——少挂一个就是"这项特效
/// 没去干净"，而且新增事件类型时必然漏。改数据只有一处：解码完成、还没开始
/// 跑之前。一次改完，之后无论谁读这份 levelEvents 都看不到被去掉的事件。
///
/// **这是破坏性操作。** 它改的是玩家磁盘上那关的数据在内存里的副本，所以
/// 默认只在「播放预览」这条路上生效；要让它把改动写回关卡文件必须显式打开 EnableSave，
/// 否则玩家只是想干净地看一遍谱面，结果保存时把原谱弄没了。
/// </summary>
internal static class EffectRemover
{
    internal static readonly EffectDataSafety Safety = new EffectDataSafety();
    private static bool Restoring;
    private static bool ModDisabled;

    internal static void OnLevelDataDecode(LevelData levelData)
    {
        if (Restoring) return;
        if (!Enabled || levelData == null) return;

        // **先留原始快照，再动数据。** 快照失败时宁可不改写：没有原始副本的
        // 去特效就是不可回滚的破坏，玩家一关一关试过去时根本发现不了。
        try
        {
            string json = levelData.Encode();
            if (string.IsNullOrEmpty(json)) throw new InvalidOperationException("Encode returned empty");
            Safety.Track(levelData, json);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("[SpectreReplay] 去特效原始快照失败，本次不改写谱面：" + ex.Message);
            return;
        }

        Remove(levelData);
        // 还原前必须证明玩家没有继续编辑这份对象。只比较对象引用挡不住原地编辑；
        // 比较去特效后的完整序列化快照，任何后续差异都宁可保留，不用旧 JSON 覆盖。
        try { Safety.SetStrippedSnapshot(levelData, levelData.Encode()); }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("[SpectreReplay] 去特效后快照失败，将禁止整份回滚：" + ex.Message);
        }
    }

    internal static void OnLevelLoaded(LevelData data, string path, bool successful)
    {
        if (!successful) return;
        if (!ModDisabled && Enabled) Safety.BindSource(data, path);
        else Safety.MarkClean(data);
        RefreshEditorSaveButtons();
    }

    internal static bool OnSaveLevelPrefix(scnEditor editor)
    {
        bool allowed = CanSave(editor);
        if (!allowed)
            UnityEngine.Debug.LogWarning("[SpectreReplay] 拒绝覆盖去特效保护的原谱；请另存到新路径，或重新载入原谱。");
        return allowed;
    }

    // ExportLevel / PublishToSteam 先写 ADOFAI.levelPath，再复制或打包；仅拦 RDFile
    // 写入会让调用方继续拿旧文件打包。必须在导出入口整体短路。
    internal static bool OnExportLevelPrefix(scnEditor editor)
    {
        bool allowed = CanSave(editor);
        if (!allowed)
            UnityEngine.Debug.LogWarning("[SpectreReplay] 拒绝从去特效保护的原谱导出或发布；请另存为新文件，或重新载入原谱。");
        return allowed;
    }

    internal static bool OnFileWritePrefix(string path)
    {
        bool allowed = Safety.CanWrite(CurrentLevelData, path,
            !ModDisabled && Enabled && Settings.EffectRemoverEnableSave);
        if (!allowed)
            UnityEngine.Debug.LogWarning("[SpectreReplay] 拒绝直接写入去特效保护的原谱文件；请另存为新文件或重新载入原谱。");
        return allowed;
    }

    private static bool CanSave(scnEditor editor)
        => Safety.CanWrite(editor == null ? CurrentLevelData : editor.levelData,
            ADOBase.customLevel == null ? null : ADOBase.levelPath,
            !ModDisabled && Enabled && Settings.EffectRemoverEnableSave);

    internal static void OnEditorLoadGameScenePostfix(scnEditor instance) => RefreshEditorSaveButtons(instance);

    /// <summary>条件事件里引用的标签。带这些标签的装饰物会被保留——
    /// 否则去完装饰物，谱面会变成"永远不触发的条件分支"，看着像关卡做坏了。</summary>
    private static readonly string[] ConditionalTagKeys =
    {
        "perfectTag", "hitTag", "earlyPerfectTag", "latePerfectTag",
        "barelyTag", "veryEarlyTag", "veryLateTag",
        "missTag", "tooEarlyTag", "tooLateTag", "lossTag"
    };

    private static bool Enabled => Settings.EffectRemoverOn;

    // Undo/Redo 会换对象；保存与还原始终查询当前对象的记录，不能查询最近一次 Decode。
    private static LevelData CurrentLevelData => scnEditor.instance != null
        ? scnEditor.instance.levelData : ADOBase.customLevel != null ? ADOBase.customLevel.levelData : null;

    internal static bool HasProtectedEditorData()
    {
        if (Safety.IsStripped(CurrentLevelData)) return true;
        var editor = scnEditor.instance;
        if (editor == null) return false;
        // 即使当前对象已还原，玩家仍可以撤销到去特效的副本，临时补丁须继续保留。
        foreach (var state in editor.undoStates)
            if (Safety.IsStripped(state.data)) return true;
        foreach (var state in editor.redoStates)
            if (Safety.IsStripped(state.data)) return true;
        return false;
    }

    // 必须先交接保护再卸载普通补丁。交接失败就拒绝禁用，保持原保存入口受保护。
    internal static bool TryPrepareForDisable()
    {
        try
        {
            RestoreOriginalLevelData();
            if (HasProtectedEditorData()) DisabledEffectSaveGuard.Install();
            ModDisabled = true;
            return true;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("[SpectreReplay] 原谱保存保护未能交接，已拒绝关闭模组：" + ex.Message);
            return false;
        }
    }

    internal static void ResumeAfterEnable()
    {
        // 保存入口与历史复制都交接完成才拆除临时保护，否则新历史副本会丢掉标记。
        if (!SpectreReplay.PatchShared.SpectrePatchHub.IsApplied(typeof(Patch_EffectDataSafety))
            || !SpectreReplay.PatchShared.SpectrePatchHub.IsApplied(typeof(Patch_LevelDataCopySafety)))
            throw new InvalidOperationException("editor level data safety or copy patches unavailable");
        DisabledEffectSaveGuard.Release();
        ModDisabled = false;
        RefreshEditorSaveButtons();
    }

    /// <summary>
    /// 把当前谱面还原成原始数据。
    /// 关闭模组或关闭功能时调用；没有快照时是 no-op。
    /// 只清除成功还原的当前对象的保护；其它历史副本保留各自记录。
    /// </summary>
    internal static void RestoreOriginalLevelData()
    {
        LevelData data = CurrentLevelData;
        if (!Safety.IsStripped(data)) return;
        Restoring = true;
        try
        {
            bool restored = Safety.Restore(data, data.Encode(), json =>
            {
                if (!(GDMiniJSON.Json.Deserialize(json) is Dictionary<string, object> dict))
                    throw new InvalidOperationException("original level data JSON parse failed");
                data.Decode(dict, out LoadResult status);
                return status == LoadResult.Successful;
            });
            if (!restored)
                UnityEngine.Debug.LogWarning("[SpectreReplay] 当前谱面在去特效后已被编辑或还原失败，保留当前修改及原文件保护；可另存到新路径。");
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("[SpectreReplay] 去特效原始谱面还原失败：" + ex.Message);
        }
        finally
        {
            Restoring = false;
            RefreshEditorSaveButtons();
        }
    }

    /// <summary>编辑器里还能不能保存。
    /// 去特效开着时：只有显式允许覆盖（EnableSave）才放行——玩家在编辑器里试听后
    /// 存盘，会把去干净的数据写回原文件。
    /// 去特效关着时仍按当前对象和原路径保护，另存新路径不受影响，
    /// 否则「先开去特效看一眼、关掉、再 Ctrl+S」照样写坏原谱。</summary>
    internal static bool EditorSaveEnabled
        => CanSave(scnEditor.instance);

    internal static void RefreshEditorSaveButtons()
    {
        RefreshEditorSaveButtons(scnEditor.instance);
    }

    internal static void RefreshEditorSaveButtons(scnEditor editor)
    {
        SetEditorSaveButtons(editor, EditorSaveEnabled);
    }

    /// <summary>离开编辑器或关掉功能时刷新保存按钮。**不能无条件恢复成可用**：
    /// 关掉去特效时内存里可能仍是剥过的那份，
    /// 那时按钮必须保持禁用，直到玩家重新进关——否则按钮亮着、Prefix 在拦，
    /// 点了没反应且不报错（正是 UiLogicShapeCheck ⑳ 那组判据要防的「两边脱节」）。</summary>
    internal static void RestoreEditorSaveButtons()
    {
        SetEditorSaveButtons(scnEditor.instance, EditorSaveEnabled);
    }

    private static void SetEditorSaveButtons(scnEditor editor, bool enabled)
    {
        if (editor == null) return;
        if (SceneManager.GetActiveScene().name != "scnEditor") return;

        if (editor.popupUnsavedChangesSave != null)
            editor.popupUnsavedChangesSave.interactable = enabled;
        if (editor.buttonSave != null)
            editor.buttonSave.interactable = enabled;
    }

    internal static void Remove(LevelData levelData)
    {
        if (!Enabled || levelData == null) return;
        // OnLevelDataDecode 在调用前已登记保护，Remove 抛异常也不能丢掉保护记录。

        List<LevelEventType> events = new List<LevelEventType>();

        if (Settings.EffectRemoverDecorations)
            RemoveDecorations(events, levelData);
        if (Settings.EffectRemoverFilters)
            AddFilterEvents(events);
        if (Settings.EffectRemoverAdvancedFilters)
            events.Add(LevelEventType.SetFilterAdvanced);
        if (Settings.EffectRemoverParticles)
        {
            events.Add(LevelEventType.AddParticle);
            events.Add(LevelEventType.EmitParticle);
            events.Add(LevelEventType.SetParticle);
        }
        if (Settings.EffectRemoverBackgrounds)
            RemoveBackgrounds(events, levelData);
        if (Settings.EffectRemoverCameras)
            RemoveCameras(events, levelData);
        if (Settings.EffectRemoverPlanetOrbit)
            events.Add(LevelEventType.SetPlanetRotation);
        if (Settings.EffectRemoverPlanetScale)
            events.Add(LevelEventType.ScalePlanets);
        if (Settings.EffectRemoverPlanetRadius)
            events.Add(LevelEventType.ScaleRadius);
        if (Settings.EffectRemoverRepeatEvents)
            events.Add(LevelEventType.RepeatEvents);
        if (Settings.EffectRemoverFrameRate)
            events.Add(LevelEventType.SetFrameRate);
        if (Settings.EffectRemoverHitSounds)
        {
            events.Add(LevelEventType.PlaySound);
            events.Add(LevelEventType.SetHitsound);
        }
        if (Settings.EffectRemoverCheckpoints)
            events.Add(LevelEventType.Checkpoint);
        if (Settings.EffectRemoverInputEvents)
            events.Add(LevelEventType.SetInputEvent);
        if (Settings.EffectRemoverHoldSounds)
            events.Add(LevelEventType.SetHoldSound);
        if (Settings.EffectRemoverTrackAnimations)
            RemoveTrackAnimations(events, levelData);
        if (Settings.EffectRemoverTrackPositions)
            events.Add(LevelEventType.PositionTrack);
        if (Settings.EffectRemoverTrackMoves)
            events.Add(LevelEventType.MoveTrack);
        if (Settings.EffectRemoverTrackColors)
            RemoveTrackColors(events, levelData);
        if (Settings.EffectRemoverHideIcons)
            events.Add(LevelEventType.Hide);
        if (Settings.EffectRemoverResetTrackOpacity)
            ResetTrackOpacity(levelData);

        if (events.Count == 0) return;

        HashSet<LevelEventType> eventSet = new HashSet<LevelEventType>(events);
        if (levelData.levelEvents != null)
            levelData.levelEvents.RemoveAll(data => data != null && eventSet.Contains(data.eventType));
    }

    private static void AddFilterEvents(List<LevelEventType> events)
    {
        events.Add(LevelEventType.Flash);
        events.Add(LevelEventType.SetFilter);
        events.Add(LevelEventType.HallOfMirrors);
        events.Add(LevelEventType.ShakeScreen);
        events.Add(LevelEventType.Bloom);
        events.Add(LevelEventType.ScreenTile);
        events.Add(LevelEventType.ScreenScroll);
    }

    private static void RemoveBackgrounds(List<LevelEventType> events, LevelData levelData)
    {
        events.Add(LevelEventType.CustomBackground);

        if (GCS.settingsInfo.TryGetValue("BackgroundSettings", out var bgSettings))
            levelData.backgroundSettings = new LevelEvent(0, LevelEventType.BackgroundSettings, bgSettings);
        if (levelData.miscSettings != null)
            levelData.miscSettings["bgVideo"] = "";
    }

    private static void RemoveCameras(List<LevelEventType> events, LevelData levelData)
    {
        events.Add(LevelEventType.MoveCamera);

        if (!Settings.EffectRemoverSetCameraZoom) return;

        float zoom = Settings.EffectRemoverCameraZoomScale;
        if (zoom < 100f) zoom = 100f;
        if (zoom > 1000f) zoom = 1000f;

        if (GCS.settingsInfo.TryGetValue("CameraSettings", out var camSettings))
        {
            levelData.cameraSettings = new LevelEvent(0, LevelEventType.CameraSettings, camSettings);
            levelData.cameraSettings["zoom"] = zoom;
        }
    }

    private static void RemoveDecorations(List<LevelEventType> events, LevelData levelData)
    {
        if (Settings.EffectRemoverRemoveAllDecorations)
        {
            levelData.decorations?.Clear();
            if (GCS.settingsInfo.TryGetValue("DecorationSettings", out var decSettings))
                levelData.decorationSettings = new LevelEvent(0, LevelEventType.DecorationSettings, decSettings);

            events.Add(LevelEventType.DecorationSettings);
            events.Add(LevelEventType.AddDecoration);
            events.Add(LevelEventType.AddText);
            events.Add(LevelEventType.SetText);
            events.Add(LevelEventType.SetDefaultText);
            events.Add(LevelEventType.MoveDecorations);
            events.Add(LevelEventType.AddObject);
            events.Add(LevelEventType.SetObject);
            return;
        }

        HashSet<string> conditionalEventTags = GetConditionalEventTags(levelData);
        HashSet<string> preservedDecorationTags = GetPreservedDecorationTags(levelData, conditionalEventTags);

        levelData.decorations.RemoveAll(data =>
            IsDecorationData(data) && !ShouldPreserve(data, conditionalEventTags, preservedDecorationTags));
        levelData.levelEvents.RemoveAll(data =>
            IsDecorationData(data) && !ShouldPreserve(data, conditionalEventTags, preservedDecorationTags));
    }

    private static HashSet<string> GetConditionalEventTags(LevelData levelData)
    {
        HashSet<string> tags = new HashSet<string>();

        foreach (LevelEvent eventData in levelData.levelEvents)
        {
            if (eventData == null || eventData.eventType != LevelEventType.SetConditionalEvents) continue;

            foreach (string key in ConditionalTagKeys)
            {
                if (!eventData.ContainsKey(key)) continue;

                string tag = eventData.GetString(key);
                if (!string.IsNullOrWhiteSpace(tag) && tag != "None" && tag != "없음")
                    tags.Add(tag);
            }
        }

        return tags;
    }

    private static HashSet<string> GetPreservedDecorationTags(LevelData levelData, HashSet<string> conditionalEventTags)
    {
        HashSet<string> tags = new HashSet<string>();

        foreach (LevelEvent eventData in levelData.levelEvents)
        {
            if (!IsDecorationData(eventData) || !HasAnyEventTag(eventData, conditionalEventTags)) continue;

            foreach (string tag in GetTags(eventData, "tag"))
                tags.Add(tag);
        }

        return tags;
    }

    private static bool ShouldPreserve(LevelEvent eventData, HashSet<string> conditionalEventTags, HashSet<string> preservedDecorationTags)
    {
        return HasAnyEventTag(eventData, conditionalEventTags) || HasAnyTag(eventData, preservedDecorationTags);
    }

    private static bool IsDecorationData(LevelEvent eventData)
    {
        if (eventData == null) return false;

        LevelEventType type = eventData.eventType;
        return type == LevelEventType.DecorationSettings
            || type == LevelEventType.AddDecoration
            || type == LevelEventType.AddText
            || type == LevelEventType.SetText
            || type == LevelEventType.SetDefaultText
            || type == LevelEventType.MoveDecorations
            || type == LevelEventType.AddObject
            || type == LevelEventType.SetObject;
    }

    private static bool HasAnyEventTag(LevelEvent eventData, HashSet<string> tags)
    {
        foreach (string eventTag in GetTags(eventData, "eventTag"))
        {
            if (tags.Contains(eventTag)) return true;
        }
        return false;
    }

    private static bool HasAnyTag(LevelEvent eventData, HashSet<string> tags)
    {
        foreach (string tag in GetTags(eventData, "tag"))
        {
            if (tags.Contains(tag)) return true;
        }
        return false;
    }

    private static IEnumerable<string> GetTags(LevelEvent eventData, string key)
    {
        if (eventData == null || !eventData.ContainsKey(key)) yield break;

        string tags = eventData.GetString(key);
        if (string.IsNullOrWhiteSpace(tags)) yield break;

        string[] split = tags.Split(' ');
        for (int i = 0; i < split.Length; i++)
        {
            string tag = split[i];
            if (!string.IsNullOrWhiteSpace(tag)) yield return tag;
        }
    }

    private static void RemoveTrackAnimations(List<LevelEventType> events, LevelData levelData)
    {
        events.Add(LevelEventType.AnimateTrack);

        if (Settings.EffectRemoverResetTrackAnimation && levelData.trackSettings != null)
        {
            levelData.trackSettings["trackAppearAnimation"] = TrackAnimationType.Fade;
            levelData.trackSettings["trackDisappearAnimation"] = TrackAnimationType.Fade;
            levelData.trackSettings["beatsAhead"] = 8.0f;
            levelData.trackSettings["beatsBehind"] = 0.0f;
        }
    }

    private static void RemoveTrackColors(List<LevelEventType> events, LevelData levelData)
    {
        events.Add(LevelEventType.ColorTrack);
        events.Add(LevelEventType.RecolorTrack);

        if (Settings.EffectRemoverResetTrackColor && levelData.trackSettings != null)
        {
            levelData.trackSettings["trackStyle"] = TrackStyle.Standard;
            levelData.trackSettings["trackColor"] = "debb7bff";
            levelData.trackSettings["trackColorType"] = TrackColorType.Single;
        }
    }

    private static void ResetTrackOpacity(LevelData levelData)
    {
        foreach (LevelEvent eventData in levelData.levelEvents)
        {
            if (eventData == null) continue;
            if (eventData.eventType != LevelEventType.MoveTrack && eventData.eventType != LevelEventType.PositionTrack) continue;
            if (eventData.ContainsKey("opacity"))
                eventData["opacity"] = 100.0f;
        }
    }
}
