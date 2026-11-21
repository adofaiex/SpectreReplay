using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SpectreReplay;
using SpectreReplay.Features.EffectRemover;
using SpectreReplay.Features.Replay;
using SpectreReplay.Features.VideoRender;
using SpectreReplay.Ui;

namespace UiPreview;

// 真实文件 I/O、真实参数构造和纯决策；需要 Unity 的接线另外验证状态转移顺序。
// 不调用 Begin/WarmUp/Probe，不创建 ffmpeg 或硬件会话。
internal static class ReviewRegressionCheck
{
    internal static int Run()
    {
        try { return RunCore(); }
        catch (Exception ex)
        {
            Console.WriteLine("    ✗ 审查回归执行失败：" + ex.Message);
            return 1;
        }
    }

    private static int RunCore()
    {
        int failed = 0;
        string temp = Path.Combine(Path.GetTempPath(), "srm-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string config = Path.Combine(temp, "Configs.json");
            File.WriteAllText(config, "before-cloud");
            File.Copy(config, config + ".bak");
            AtomicFile.WriteAllText(config, "from-cloud");
            AtomicFile.WriteAllText(config, "ordinary-save");
            Check(File.ReadAllText(config + ".bak") == "before-cloud"
                && File.ReadAllText(config + ".rolling.bak") == "from-cloud",
                "R01 云恢复保全副本经常规保存仍保留");

            string blocked = Path.Combine(temp, "blocked.json");
            File.WriteAllText(blocked, "old");
            Directory.CreateDirectory(blocked + ".rolling.bak");
            bool rejected = false;
            try { AtomicFile.WriteAllText(blocked, "new"); }
            catch (IOException) { rejected = true; }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected && File.ReadAllText(blocked) == "old"
                && File.ReadAllText(blocked + ".tmp") == "new", "R02 备份失败不删除旧目标");
        }
        finally { Directory.Delete(temp, true); }

        string fx = Read("Features/EffectRemover/EffectRemover.cs");
        string restore = Body(fx, "RestoreOriginalLevelData");
        Check(SnapshotRestoreGuard.CanRestore("stripped", "stripped")
            && !SnapshotRestoreGuard.CanRestore("stripped", "edited")
            && !SnapshotRestoreGuard.CanRestore(null, "edited")
            && Before(restore, "Safety.Restore(data, data.Encode()", "data.Decode(")
            && fx.Contains("Safety.SetStrippedSnapshot(levelData, levelData.Encode());", StringComparison.Ordinal),
            "R03 只有去特效后未继续编辑的谱面可整份回滚");
        string cloud = Read("Features/Cloud/CloudReplay.cs");
        string cloudRestore = Body(cloud, "RestoreConfigManual");
        string backupBlock = BlockAfter(cloudRestore, "if (File.Exists(localPath))");
        string backupCatch = BlockAfter(backupBlock, "catch (Exception ex)");
        Check(BlockAfter(backupBlock, "try").Contains("File.Copy(localPath, localPath + \".bak\", true);", StringComparison.Ordinal)
            && Before(cloudRestore, "File.Copy(localPath, localPath + \".bak\", true);", "AtomicFile.WriteAllBytes(localPath, data);")
            && backupCatch.Contains("error = \"backup_failed\";", StringComparison.Ordinal)
            && backupCatch.Contains("return false;", StringComparison.Ordinal),
            "R04 云恢复先创建保全副本，备份失败在本分支明确中止");

        bool hdrMatrix = true;
        for (int surface = 0; surface < 2; surface++)
        for (int codec = 0; codec < 3; codec++)
        for (int container = 0; container < 4; container++)
            hdrMatrix &= RenderOutputPolicy.Hdr10Enabled(true, surface, codec, container)
                == (surface == 0 && codec == 1 && container != 2);
        hdrMatrix &= !RenderOutputPolicy.Hdr10Enabled(false, 0, 1, 0);
        Check(hdrMatrix, "R05 HDR10 只在纯净相机/H265/非WebM生效");
        Check(RenderOutputPolicy.DrainComplete(0, 100, 100)
            && !RenderOutputPolicy.DrainComplete(1, 99, 100)
            && !RenderOutputPolicy.DrainComplete(0, 99, 100), "R06 排空必须同时满足在途为零与帧数守恒");
        Check(!RenderOutputPolicy.UseTimelineRange(true, 100, 0, 100)
            && !RenderOutputPolicy.UseTimelineRange(false, 100, 10, 80)
            && RenderOutputPolicy.UseTimelineRange(true, 100, 10, 80), "R07 全长默认时间轴不覆盖选砖起点");
        Check(HitAdvanceGuard.ProvesAdvance(9, 10, null)
            && HitAdvanceGuard.ProvesAdvance(9, null, 10)
            && !HitAdvanceGuard.ProvesAdvance(9, 9, 10)
            && !HitAdvanceGuard.ProvesAdvance(9, null, 9)
            && !HitAdvanceGuard.ProvesAdvance(9, null, null), "R08 最后一击由EndTile证明推进，同砖多按不误收");

        const BindingFlags sf = BindingFlags.NonPublic | BindingFlags.Static;
        const BindingFlags inf = BindingFlags.NonPublic | BindingFlags.Instance;
        var software = typeof(VideoEncoder).GetMethod("SoftwareCodec", sf);
        string catalogKey = "review-memory-only";
        bool coldRejected = false;
        try { software.Invoke(null, new object[] { VideoEncoder.VideoCodec.Av1, catalogKey }); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { coldRejected = true; }
        var cache = (Dictionary<string, HashSet<string>>)typeof(EncoderCatalog).GetField("Cache", sf).GetValue(null);
        cache[catalogKey] = new HashSet<string> { "libsvtav1" };
        string warm = software.Invoke(null, new object[] { VideoEncoder.VideoCodec.Av1, catalogKey }).ToString();
        cache.Remove(catalogKey);
        string enc = Read("Features/VideoRender/Pipeline/VideoEncoder.cs");
        Check(coldRejected && warm == "SvtAv1"
            && Before(Body(enc, "Begin"), "EncoderCatalog.WaitUntilLoaded", "selectedCodec = WaitForCodec"),
            "R09 冷目录不把AV1定为x265，后台先等目录再选择");

        var qsvCrf = Encoder(VideoEncoder.RateControlMode.Crf);
        var qsvCqp = Encoder(VideoEncoder.RateControlMode.Cqp);
        Check(Invoke(qsvCrf, "BuildQsvRateControl") == "-global_quality 17"
            && Invoke(qsvCqp, "BuildQsvRateControl") == "-flags +qscale -q:v 31",
            "R10 QSV CQP显式启用qscale，与ICQ分开");
        Check(Invoke(qsvCrf, "BuildSvtRateControl") == "-crf 17"
            && Invoke(qsvCqp, "BuildSvtRateControl") == "-qp 31"
            && Invoke(new VideoEncoder("unused", "unused", "unused", 64, 64, 1,
                VideoEncoder.Quality.Balanced, 1, false, rateControl: VideoEncoder.RateControlMode.Cqp,
                cqp: 0), "BuildSvtRateControl").Contains("aq-mode=0:qp=0", StringComparison.Ordinal),
            "R11 SVT CQP使用qp而非crf，零值不退回默认量化");
        Check(Invoke(qsvCrf, "BuildAmfRateControl").Contains("-qp_i 17 -qp_p 17", StringComparison.Ordinal)
            && Invoke(qsvCqp, "BuildAmfRateControl").Contains("-qp_i 31 -qp_p 31", StringComparison.Ordinal),
            "R12 AMF质量值进入各帧QP参数");
        var codecField = typeof(VideoEncoder).GetField("selectedCodec", inf);
        Type codecType = Nullable.GetUnderlyingType(codecField.FieldType);
        codecField.SetValue(qsvCrf, Enum.Parse(codecType, "NvencH265"));
        bool hardware = qsvCrf.UsingHardware;
        codecField.SetValue(qsvCrf, Enum.Parse(codecType, "X265"));
        Check(hardware && !qsvCrf.UsingHardware, "R13 重试依据本次实际编码器区分软硬件");

        Settings.RenderWidth = 1920;
        Settings.RenderVideoCodec = 1;
        Settings.RenderCaptureMode = 0;
        Settings.RenderOutputFormat = 0;
        Settings.RenderHdr10 = true;
        var snapshot = new RenderEncodingSettings();
        Settings.RenderWidth = 640;
        Settings.RenderVideoCodec = 0;
        Settings.RenderHdr10 = false;
        Check(snapshot.Width == 1920 && snapshot.Codec == 1 && snapshot.Hdr10 && snapshot.TenBit,
            "R14 渲染配置快照不随全局设置变化");

        string job = Read("Features/VideoRender/Pipeline/VideoRenderJob.cs");
        string phases = Body(job, "Phases");
        Check(Before(phases, "StartFinalizeOnWorker();", "consumeReplay = true;")
            && Before(phases, "if (finalizeError != null)", "ConsumeLoadedReplay();")
            && phases.Contains("finalizingReplay = true;", StringComparison.Ordinal)
            && job.Contains("else if (finalizingReplay)", StringComparison.Ordinal),
            "R15 合成成功后才消费回放，等待时暂停两种模式");
        Check(phases.Contains("scrController.instance.Restart();", StringComparison.Ordinal),
            "R16 普通关卡启动和CPU重试主动重开");
        Check(Body(job, "NotifyFailed").Contains("encoder.UsingHardware", StringComparison.Ordinal)
            && !Body(job, "NotifyFailed").Contains("Settings.RenderHardware", StringComparison.Ordinal)
            && job.Contains("message, options);", StringComparison.Ordinal)
            && phases.Contains("options.StartFloor ?? ResolveRequestedStartFloor()", StringComparison.Ordinal)
            && Before(phases, "options.StartFloor = requestedStartFloor;", "ADOBase.editor.Play();"),
            "R17 CPU重试保留配置与起点且不读可变后端");
        string drainFailure = BlockAfter(phases, "if (!RenderOutputPolicy.DrainComplete(frames.InFlight, delivered, frameIndex))");
        Check(Before(drainFailure, "NotifyFailed(", "yield break;")
            && Before(phases, "RenderOutputPolicy.DrainComplete", "StartFinalizeOnWorker();"),
            "R18 缺尾帧在本失败分支退出，不进入合成");
        string vr = Read("Features/VideoRender/Pipeline/VideoRender.cs");
        Check(Before(Body(vr, "TryStartRender"), "if (Job != null) return false;", "CpuRetryUsed = false;")
            && Read("State/SpectreAPI.cs").Contains("return Features.VideoRender.VideoRender.TryStartRender(", StringComparison.Ordinal),
            "R19 忙碌请求返回失败并保留正在使用的重试预算");
        string gui = Read("Ui/OnGui.cs");
        Check(Body(gui, "CommitText").Contains("_fieldCommitters", StringComparison.Ordinal)
            && Body(gui, "RowTextField").Contains("_fieldCommitters[labelKey] = onCommit;", StringComparison.Ordinal),
            "R20 通用文本框草稿接入关窗提交");
        string state = Read("State/SpectreState.cs");
        Check(Regex.IsMatch(state, @"public static bool ReplayHit\(scrPlayer player, HitSnapshot hitdata\)\s*=> ReplayHit\(player, hitdata, null\);"),
            "R21 保留公开双参数ReplayHit二进制入口");
        Check(Read("Features/Replay/Patches/Patch_Playback.cs").Contains("HitAdvanceGuard.ProvesAdvance(hc.CurrentFloorID,", StringComparison.Ordinal)
            && Read("Features/Replay/Patches/Patch_Playback.cs").Contains("data.EndTile);", StringComparison.Ordinal)
            && vr.Contains("RenderOutputPolicy.UseTimelineRange", StringComparison.Ordinal),
            "R22 最后一击与默认时间轴规则接到实际调用点");
        string pages = Read("Pages/Pages.cs");
        Check(pages.Contains("RenderOutputPolicy.Hdr10Enabled", StringComparison.Ordinal)
            && pages.Contains("Render10Bit || hdr10, hdr10", StringComparison.Ordinal)
            && pages.Contains("VideoRender.Job?.ActualEncoder", StringComparison.Ordinal)
            && pages.Contains("CRF → CQP", StringComparison.Ordinal), "R23 界面显示生效HDR/实际编码器/AMF模式映射");
        Check(Read("State/ConfigManager.cs").Contains("if (rawHardware == null && rawEncoder == 3 && Settings.RenderPreset == 0)", StringComparison.Ordinal)
            && Read("State/ConfigManager.cs").Contains("Settings.RenderPreset = 3;", StringComparison.Ordinal),
            "R24 旧CpuOnly升级保留veryfast预设");

        string main = Read("Main.cs");
        string disabledGuard = Read("Features/EffectRemover/DisabledEffectSaveGuard.cs");
        string prepare = Body(fx, "TryPrepareForDisable");
        Check(Before(prepare, "RestoreOriginalLevelData();", "if (HasProtectedEditorData()) DisabledEffectSaveGuard.Install();")
            && BlockAfter(prepare, "catch (Exception ex)").Contains("return false;", StringComparison.Ordinal)
            && Before(Body(main, "Teardown"), "if (!Features.EffectRemover.EffectRemover.TryPrepareForDisable()) return false;", "SpectrePatchHub.UnpatchAll();")
            && main.Contains("return Teardown();", StringComparison.Ordinal)
            && disabledGuard.Contains("typeof(scnEditor), \"SaveLevel\"", StringComparison.Ordinal)
            && disabledGuard.Contains("private const string Owner = \"com.spectre.replay.disabled-effect-save-guard\";", StringComparison.Ordinal),
            "R25 不能还原时先交接独立保存保护，交接失败拒绝禁用");
        bool guardOwner = disabledGuard.Contains("nameof(SavePrefix)", StringComparison.Ordinal)
            && disabledGuard.Contains("nameof(FileWritePrefix)", StringComparison.Ordinal)
            && disabledGuard.Contains("nameof(LoadedPostfix)", StringComparison.Ordinal)
            && disabledGuard.Contains("nameof(CopyPostfix)", StringComparison.Ordinal);
        bool guardWrites = disabledGuard.Contains("EffectRemover.OnSaveLevelPrefix(__instance)", StringComparison.Ordinal)
            && disabledGuard.Contains("EffectRemover.OnFileWritePrefix(path)", StringComparison.Ordinal);
        bool guardState = Before(Body(disabledGuard, "LoadedPostfix"), "if (!__result) return;", "Safety.MarkClean(__instance.levelData)")
            && disabledGuard.Contains("Safety.Copy(__instance, __result)", StringComparison.Ordinal)
            && disabledGuard.Contains("AccessTools.Method(typeof(LevelData), \"Copy\")", StringComparison.Ordinal)
            && disabledGuard.Contains("HasProtectedEditorData()", StringComparison.Ordinal)
            && disabledGuard.Contains("ReleaseFrom(harmonyType, hooks);", StringComparison.Ordinal);
        Check(guardOwner && guardWrites && guardState,
            "R26 禁用后按对象/原路径拦保存，干净载入与历史状态解除保护");
        string resume = Body(fx, "ResumeAfterEnable");
        const string bothHooksRequired = @"if\s*\(\s*!SpectreReplay\.PatchShared\.SpectrePatchHub\.IsApplied\(typeof\(Patch_EffectDataSafety\)\)\s*\|\|\s*!SpectreReplay\.PatchShared\.SpectrePatchHub\.IsApplied\(typeof\(Patch_LevelDataCopySafety\)\)\)\s*throw\b";
        Match handoff = Regex.Match(resume, bothHooksRequired);
        Check(Before(Body(main, "OnToggle"), "SpectrePatchHub.Refresh();", "EffectRemover.ResumeAfterEnable();")
            && handoff.Success && Before(resume, handoff.Value, "DisabledEffectSaveGuard.Release();")
            && fx.Contains("!ModDisabled && Enabled && Settings.EffectRemoverEnableSave", StringComparison.Ordinal),
            "R27 重开模组时普通保存或复制钩子缺失都拒绝释放临时保护");

        string effectPatch = Read("Features/EffectRemover/Patch_EffectRemover.cs");
        Check(effectPatch.Contains("LoadResult status", StringComparison.Ordinal)
            && effectPatch.Contains("if (status != LoadResult.Successful) return;", StringComparison.Ordinal)
            && Before(effectPatch, "status != LoadResult.Successful", "EffectRemover.OnLevelDataDecode(__instance);")
            && effectPatch.Contains("[SpectrePatch(typeof(scnEditor), \"ExportLevel\", PatchType.Prefix)]\n    internal static bool ExportPrefix(scnEditor __instance) => EffectRemover.OnExportLevelPrefix(__instance);", StringComparison.Ordinal)
            && effectPatch.Contains("[SpectrePatch(typeof(scnEditor), \"PublishToSteam\", PatchType.Prefix)]\n    internal static bool PublishPrefix(scnEditor __instance) => EffectRemover.OnExportLevelPrefix(__instance);", StringComparison.Ordinal)
            && Body(fx, "OnExportLevelPrefix").Contains("bool allowed = CanSave(editor);", StringComparison.Ordinal)
            && Body(fx, "OnExportLevelPrefix").Contains("return allowed;", StringComparison.Ordinal),
            "R39 Decode失败不去特效，导出与Steam发布入口整体短路");

        string cloudState = Read("Features/Cloud/CloudReplay.cs");
        string uploadGate = Body(cloudState, "ConfigUploadAllowedNow");
        string restoreConfig = Body(cloudState, "RestoreConfigIfMissing");
        string localAppeared = BlockAfter(restoreConfig, "if (File.Exists(localPath))");
        string configConflict = BlockAfter(localAppeared, "if (SteamCloud.Exists(RemoteConfigName))");
        string restoreTimeout = BlockAfter(Body(cloudState, "RestoreWhenReady"), "if (!SteamCloud.Ready)");
        Check(Body(cloudState, "RestoreOnStartup").Contains("BeginConfigRestoreWindow();", StringComparison.Ordinal)
            && Body(cloudState, "BeginConfigRestoreWindow").Contains("_configLocalMissingAtStartup = string.IsNullOrEmpty(localPath) || !File.Exists(localPath);", StringComparison.Ordinal)
            && Before(restoreConfig, "if (!_configLocalMissingAtStartup)", "_configRestoreSettled = true;")
            && configConflict.Contains("return false;", StringComparison.Ordinal)
            && !configConflict.Contains("_configRestoreSettled = true;", StringComparison.Ordinal)
            && !restoreTimeout.Contains("_configRestoreSettled = true;", StringComparison.Ordinal)
            && !uploadGate.Contains("Time.realtimeSinceStartup > 90f", StringComparison.Ordinal)
            && cloudState.Contains("需手动上传确认本地配置", StringComparison.Ordinal),
            "R40 启动期间本地配置与云端冲突时不因超时自动覆盖备份");

        string player = Read("Features/Replay/Runtime/ReplayPlayer.cs");
        Check(player.Contains("private static bool _mirrorUsedThisPlayback;", StringComparison.Ordinal)
            && player.Contains("bool releaseMirrorKeys = _mirrorUsedThisPlayback;", StringComparison.Ordinal)
            && player.Contains("_mirrorUsedThisPlayback = true;", StringComparison.Ordinal)
            && player.Contains("_mirrorUsedThisPlayback = false;", StringComparison.Ordinal),
            "R41 镜像中途关闭设置仍按本轮实际注入状态补发按键释放");

        string encoderSource = Read("Features/VideoRender/Pipeline/VideoEncoder.cs");
        string jobSource = Read("Features/VideoRender/Pipeline/VideoRenderJob.cs");
        Check(encoderSource.Contains("internal bool TrySubmit(", StringComparison.Ordinal)
            && encoderSource.Contains("return queue.TryAdd(frame);", StringComparison.Ordinal)
            && !Body(encoderSource, "TrySubmit").Contains("while (true)", StringComparison.Ordinal)
            && jobSource.Contains("private bool pendingEncode;", StringComparison.Ordinal)
            && jobSource.Contains("encoder.TrySubmit(pendingEncodeData", StringComparison.Ordinal)
            && jobSource.Contains("while ((frames.InFlight > 0 || pendingEncode)", StringComparison.Ordinal)
            && jobSource.Contains("if (frames.InFlight > 0 || pendingEncode)\n                yield return null;", StringComparison.Ordinal)
            && jobSource.Contains("while ((pendingEncode || frames.InFlight >= frames.MaxInFlight)", StringComparison.Ordinal)
            && Body(jobSource, "TryFlushPendingEncode").Contains("encodeStallClock.ElapsedMilliseconds >= 120_000", StringComparison.Ordinal),
            "R42 编码队列满时主线程只保留待提交帧并让出协程，不同步等待ffmpeg");

        string verify = File.ReadAllText(Path.Combine(Driver.ProjectDir, "..", "tools", "verify.ps1"));
        Check(Before(verify, "$buildExit = $LASTEXITCODE", "if ($buildExit -ne 0)")
            && Before(verify, "$previewBuildExit = $LASTEXITCODE", "if ($previewBuildExit -ne 0)")
            && Before(verify, "$previewRunExit = $LASTEXITCODE", "if ($previewRunExit -ne 0)"),
            "R43 主项目构建、预览器构建与运行均按原生退出码判红");

        var resolve = typeof(VideoEncoder).GetMethod("ResolveCodec", sf, null, new[] {
            typeof(VideoEncoder.OutContainer), typeof(VideoEncoder.VideoCodec), typeof(VideoEncoder.HardwareBackend),
            typeof(bool), typeof(bool), typeof(string) }, null);
        var probes = (Dictionary<string, int>)typeof(VideoEncoder).GetField("encoderProbeCache", sf).GetValue(null);
        bool hardwareOnly = true, pendingAndFallback = true;
        var backendNames = new[] { "av1_nvenc", "av1_qsv", "av1_amf" };
        var codecNames = new[] { "NvencAv1", "QsvAv1", "AmfAv1" };
        try
        {
            for (int b = 0; b < backendNames.Length; b++)
            {
                cache[catalogKey] = new HashSet<string> { backendNames[b] };
                probes[catalogKey + "|" + backendNames[b]] = 1;
                for (int c = 0; c < 2; c++)
                {
                    hardwareOnly &= Select((VideoEncoder.OutContainer)c, (VideoEncoder.HardwareBackend)(b + 1)) == codecNames[b];
                    hardwareOnly &= Select((VideoEncoder.OutContainer)c, VideoEncoder.HardwareBackend.Auto) == codecNames[b];
                }
                probes[catalogKey + "|" + backendNames[b]] = -1;
                pendingAndFallback &= Select(VideoEncoder.OutContainer.Mp4, (VideoEncoder.HardwareBackend)(b + 1)) == "EncoderPendingException";
                cache[catalogKey].Add("libaom-av1");
                pendingAndFallback &= Select(VideoEncoder.OutContainer.Mp4, (VideoEncoder.HardwareBackend)(b + 1)) == "AomAv1";
                probes[catalogKey + "|" + backendNames[b]] = 0;
                cache[catalogKey].Remove("libaom-av1");
                pendingAndFallback &= Select(VideoEncoder.OutContainer.Mp4, (VideoEncoder.HardwareBackend)(b + 1)) == nameof(VideoEncoder.PreStartFailureReason.NoAv1Encoder);
                pendingAndFallback &= Select(VideoEncoder.OutContainer.Mkv, VideoEncoder.HardwareBackend.Cpu)
                    == nameof(VideoEncoder.PreStartFailureReason.NoAv1Encoder);
            }
            cache[catalogKey] = new HashSet<string> { "av1_nvenc", "av1_qsv" };
            probes[catalogKey + "|av1_nvenc"] = -1;
            probes[catalogKey + "|av1_qsv"] = 1;
            hardwareOnly &= Select(VideoEncoder.OutContainer.Mp4, VideoEncoder.HardwareBackend.Auto) == "QsvAv1";
        }
        finally
        {
            cache.Remove(catalogKey);
            foreach (string name in backendNames) probes.Remove(catalogKey + "|" + name);
        }
        Check(hardwareOnly, "R28 只有硬件AV1也可选NVENC/QSV/AMF，pending不遮住已就绪后端");
        Check(pendingAndFallback
            && Body(enc, "WaitForCodec").Contains("catch (EncoderPendingException)", StringComparison.Ordinal)
            && Body(enc, "WaitForCodec").Contains("Thread.Sleep(10);", StringComparison.Ordinal)
            && Body(enc, "Begin").Contains("selectedCodec = WaitForCodec();", StringComparison.Ordinal)
            && VideoEncoder.PreStartWaitSeconds >= 32.0
            && phases.Contains("prestartWait.Elapsed.TotalSeconds < VideoEncoder.PreStartWaitSeconds", StringComparison.Ordinal),
            "R29 硬件AV1待探测可等待，无硬件时才要求软件回退");
        var catalogFailure = new VideoEncoder.PreStartException(
            VideoEncoder.PreStartFailureReason.EncoderCatalogUnavailable, "raw catalog error");
        var probeFailure = new VideoEncoder.PreStartException(
            VideoEncoder.PreStartFailureReason.HardwarePreflightTimeout, "raw probe error");
        var noAv1Failure = new VideoEncoder.PreStartException(
            VideoEncoder.PreStartFailureReason.NoAv1Encoder, "raw availability error");
        bool reasonMapping = VideoEncoder.ClassifyPreStartFailure(catalogFailure)
                == VideoEncoder.PreStartFailureReason.EncoderCatalogUnavailable
            && VideoEncoder.ClassifyPreStartFailure(probeFailure)
                == VideoEncoder.PreStartFailureReason.HardwarePreflightTimeout
            && VideoEncoder.ClassifyPreStartFailure(noAv1Failure)
                == VideoEncoder.PreStartFailureReason.NoAv1Encoder
            && VideoEncoder.ClassifyPreStartFailure(new InvalidOperationException("unclassified"))
                == VideoEncoder.PreStartFailureReason.Generic
            && VideoEncoder.PreStartFailureLocalizationKey(VideoEncoder.PreStartFailureReason.EncoderCatalogUnavailable)
                == "rendervideo.encoder_catalog_unavailable"
            && VideoEncoder.PreStartFailureLocalizationKey(VideoEncoder.PreStartFailureReason.HardwarePreflightTimeout)
                == "rendervideo.av1_preflight_timeout"
            && VideoEncoder.PreStartFailureLocalizationKey(VideoEncoder.PreStartFailureReason.NoAv1Encoder)
                == "rendervideo.av1_unavailable"
            && VideoEncoder.PreStartFailureLocalizationKey(VideoEncoder.PreStartFailureReason.PreStartWaitTimeout)
                == "rendervideo.encoder_prestart_timeout"
            && VideoEncoder.PreStartFailureLocalizationKey(VideoEncoder.PreStartFailureReason.Generic)
                == "rendervideo.encoder_prestart_failed";
        bool localizedReasons = true;
        var savedLanguage = RDString.language;
        try
        {
            foreach (string lang in LocCheck.Languages)
            {
                RDString.language = LocCheck.CodeToSystemLanguage(lang);
                var messages = new HashSet<string>(StringComparer.Ordinal);
                foreach (var reason in Enum.GetValues<VideoEncoder.PreStartFailureReason>())
                {
                    string key = VideoEncoder.PreStartFailureLocalizationKey(reason);
                    string text = Loc.T(key);
                    localizedReasons &= Loc.CurrentLanguage == lang
                        && LocData.Languages[lang].TryGetValue(key, out string expected)
                        && text == expected && text != key && !string.IsNullOrWhiteSpace(text)
                        && messages.Add(text);
                }
            }
        }
        finally
        {
            RDString.language = savedLanguage;
            Loc.RefreshLanguage();
        }
        string preStartErrorBlock = BlockAfter(phases, "if (_encoderPreStartError != null)");
        string preStartTimeoutBlock = BlockAfter(phases, "if (encoder == null)");
        string drive = Body(job, "Drive");
        string resizeBranch = BlockAfter(phases, "if (frames.Width != preW || frames.Height != preH || frames.NeedsRowFlip != preFlip)");
        // 只检查真实重建分支内包着 Begin 的处理，不把独立构造的异常当作调用点证据。
        bool rebuildKeepsOriginal = Regex.IsMatch(resizeBranch,
            @"\btry\s*\{\s*encoder\.Begin\(\);\s*\}\s*catch \(Exception ex\)\s*"
            + @"when \(ex is not VideoEncoder\.PreStartException && ex is not OperationCanceledException\)\s*\{\s*"
            + @"throw new VideoEncoder\.PreStartException\(\s*VideoEncoder\.PreStartFailureReason\.Generic,\s*""[^""]*"",\s*ex\);\s*\}");
        Check(reasonMapping && localizedReasons
            && enc.Contains("throw new PreStartException(PreStartFailureReason.EncoderCatalogUnavailable", StringComparison.Ordinal)
            && enc.Contains("throw new PreStartException(PreStartFailureReason.HardwarePreflightTimeout", StringComparison.Ordinal)
            && enc.Contains("throw new PreStartException(PreStartFailureReason.NoAv1Encoder", StringComparison.Ordinal)
            && preStartErrorBlock.Contains("var reason = VideoEncoder.ClassifyPreStartFailure(_encoderPreStartError);", StringComparison.Ordinal)
            && Before(preStartErrorBlock, "Debug.LogException(_encoderPreStartError);",
                "NotifyFailed(SpectreReplay.Ui.Loc.T(VideoEncoder.PreStartFailureLocalizationKey(reason)));")
            && !preStartErrorBlock.Contains("_encoderPreStartError.Message", StringComparison.Ordinal)
            && preStartTimeoutBlock.Contains("PreStartFailureReason.PreStartWaitTimeout", StringComparison.Ordinal)
            && preStartTimeoutBlock.Contains("NotifyFailed(SpectreReplay.Ui.Loc.T(key));", StringComparison.Ordinal)
            && rebuildKeepsOriginal
            && drive.Contains("if (failure is VideoEncoder.PreStartException)", StringComparison.Ordinal)
            && drive.Contains("NotifyFailed(SpectreReplay.Ui.Loc.T(", StringComparison.Ordinal)
            && drive.Contains("VideoEncoder.PreStartFailureLocalizationKey(reason)", StringComparison.Ordinal)
            && Before(drive, "Debug.LogException(failure);",
                "VideoEncoder.PreStartFailureLocalizationKey(reason)")
            && drive.Contains("NotifyFailed(failure.Message);", StringComparison.Ordinal),
            "R30 预启动失败按类型本地化；重建原异常只完整写入日志");
        var safetyModel = new EffectDataSafety();
        object currentData = new object(), undoCopy = new object();
        safetyModel.Track(currentData, "original-level-json");
        safetyModel.BindSource(currentData, Path.Combine(temp, "original.adofai"));
        safetyModel.SetStrippedSnapshot(currentData, "stripped-level-json");
        safetyModel.Copy(currentData, undoCopy);
        bool sourceBlocked = !safetyModel.CanWrite(currentData, Path.Combine(temp, "original.adofai"), false);
        bool copyBlocked = !safetyModel.CanWrite(undoCopy, Path.Combine(temp, "original.adofai"), false);
        bool newPathAllowed = safetyModel.CanWrite(undoCopy, Path.Combine(temp, "copy.adofai"), false);
        bool editRefused = !safetyModel.Restore(undoCopy, "edited", _ => true);
        bool historicalSurvivesCurrentRestore = safetyModel.Restore(currentData, "stripped-level-json", _ => true)
            && !safetyModel.IsStripped(currentData)
            && safetyModel.IsStripped(undoCopy);
        Check(sourceBlocked && copyBlocked && newPathAllowed && editRefused && historicalSurvivesCurrentRestore,
            "R33 对象标记随撤销副本传递，原路径受保护、新路径放行、仅当前未编辑对象可还原");
        CheckLevelFileIdentity(Check);

        string guard = Read("Features/EffectRemover/DisabledEffectSaveGuard.cs");
        string dataPatch = Read("Features/EffectRemover/Patch_EffectRemover.cs");
        Check(Regex.IsMatch(dataPatch, @"\[SpectrePatch\(typeof\(scnEditor\), ""SaveLevel"", PatchType\.Prefix")
            && Regex.IsMatch(dataPatch, @"\[SpectrePatch\(typeof\(scnEditor\), ""SaveLevel"", PatchType\.Postfix")
            && Body(fx, "OnSaveLevelPrefix").Contains("CanSave(editor)", StringComparison.Ordinal)
            && fx.Contains("Safety.CanWrite(CurrentLevelData,", StringComparison.Ordinal)
            && fx.Contains("!ModDisabled && Enabled && Settings.EffectRemoverEnableSave", StringComparison.Ordinal)
            && dataPatch.Contains("typeof(RDFile), \"WriteAllText\"", StringComparison.Ordinal)
            && dataPatch.Contains("EffectRemover.OnFileWritePrefix(path)", StringComparison.Ordinal)
            && dataPatch.Contains("OnLevelLoaded(__instance.levelData, levelPath", StringComparison.Ordinal)
            && dataPatch.Contains("[SpectrePatch(typeof(ADOFAI.LevelData), \"Copy\", PatchType.Postfix)]", StringComparison.Ordinal)
            && Body(Read("Features/EffectRemover/EffectDataSafety.cs"), "BindSource").Contains("snapshot.SourcePath = path;", StringComparison.Ordinal)
            && Body(guard, "SavePrefix").Contains("return EffectRemover.OnSaveLevelPrefix(__instance);", StringComparison.Ordinal)
            && Body(Read("Features/EffectRemover/EffectDataSafety.cs"), "CanWrite").Contains("Path.GetFullPath", StringComparison.Ordinal),
            "R31 最终SaveLevel入口保护原路径，允许新路径并保留显式覆盖开关");
        string safety = Read("Features/EffectRemover/EffectDataSafety.cs");
        Check(Body(fx, "RestoreOriginalLevelData").Contains("LevelData data = CurrentLevelData;", StringComparison.Ordinal)
            && Before(Body(fx, "RestoreOriginalLevelData"), "Safety.Restore(data, data.Encode()", "data.Decode(dict, out LoadResult status)")
            && Body(safety, "Copy").Contains("snapshots.Add(copy, snapshot)", StringComparison.Ordinal)
            && Body(fx, "HasProtectedEditorData").Contains("Safety.IsStripped(CurrentLevelData)", StringComparison.Ordinal)
            && Regex.IsMatch(Body(fx, "HasProtectedEditorData"), @"foreach\s*\(var state in editor\.undoStates\)\s*if \(Safety\.IsStripped\(state\.data\)\) return true;")
            && Regex.IsMatch(Body(fx, "HasProtectedEditorData"), @"foreach\s*\(var state in editor\.redoStates\)\s*if \(Safety\.IsStripped\(state\.data\)\) return true;")
            && dataPatch.Contains("EffectRemover.Safety.Copy(__instance, __result)", StringComparison.Ordinal),
            "R32 去特效状态随LevelData.Copy传播，撤销/重做当前副本保持保护并单独还原");
        string loaded = Body(guard, "LoadedPostfix");
        string editorState = Body(guard, "EditorStatePostfix");
        Check(Before(loaded, "EffectRemover.Safety.MarkClean(__instance.levelData);", "EditorStatePostfix(editor);")
            && Before(editorState, "EffectRemover.RefreshEditorSaveButtons(__instance);", "if (!EffectRemover.HasProtectedEditorData()) Release();"),
            "R37 同编辑器成功加载直接刷新保存按钮，仅当前及撤销/重做均干净时释放临时owner");
        const string currentEditorLoad = "if (editor == null || !ReferenceEquals(__instance, ADOBase.customLevel)) return;";
        Check(Before(loaded, "if (!__result) return;", "EffectRemover.Safety.MarkClean(__instance.levelData);")
            && Before(loaded, "var editor = scnEditor.instance;", currentEditorLoad)
            && Before(loaded, currentEditorLoad, "EffectRemover.Safety.MarkClean(__instance.levelData);"),
            "R38 失败加载及非当前编辑器关卡不得清理当前原谱保护或触发释放");
        return failed;

        string Select(VideoEncoder.OutContainer container, VideoEncoder.HardwareBackend backend)
        {
            try { return resolve.Invoke(null, new object[] { container, VideoEncoder.VideoCodec.Av1, backend, false, false, catalogKey }).ToString(); }
            catch (TargetInvocationException ex) when (ex.InnerException is VideoEncoder.PreStartException typed)
            {
                return VideoEncoder.ClassifyPreStartFailure(typed).ToString();
            }
            catch (TargetInvocationException ex) { return ex.InnerException.GetType().Name; }
        }

        void Check(bool ok, string label)
        {
            Console.WriteLine("    " + (ok ? "✓ " : "✗ ") + label);
            if (!ok) failed++;
        }
    }

    private static void CheckLevelFileIdentity(Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "srm-level-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string original = Path.Combine(dir, "original.adofai");
            string hardLink = Path.Combine(dir, "alias.adofai");
            string different = Path.Combine(dir, "different.adofai");
            File.WriteAllText(original, "original");
            File.Copy(original, different); // 即使内容完全相同，也不应误认成同一文件。
            if (!CreateHardLinkW(hardLink, original, IntPtr.Zero))
                throw new IOException("CreateHardLinkW failed: " + Marshal.GetLastWin32Error());

            var model = new EffectDataSafety();
            object level = new object(), history = new object();
            model.Track(level, "original");
            model.BindSource(level, original);
            model.Copy(level, history);
            check(!model.CanWrite(history, hardLink, false)
                && !model.CanWrite(history, @"\\?\" + original, false)
                && model.CanWrite(history, hardLink, true)
                && model.CanWrite(history, different, false)
                && model.CanWrite(history, Path.Combine(dir, "new.adofai"), false),
                "R34 真实硬链接和扩展路径不可覆盖；独立文件、全新路径与显式覆盖按策略放行");

            File.Delete(original); // 硬链接仍指向载入时的文件。
            bool oldIdentitySurvives = !model.CanWrite(history, hardLink, false)
                && model.CanWrite(history, different, false);
            if (!CreateHardLinkW(original, different, IntPtr.Zero))
                throw new IOException("CreateHardLinkW failed: " + Marshal.GetLastWin32Error());
            check(oldIdentitySurvives && !model.CanWrite(history, hardLink, false)
                && !model.CanWrite(history, different, false),
                "R35 原路径被移除或换成另一文件后仍保护载入时和当前原谱两种身份");

            check(!model.CanWrite(history, dir, false), "R36 目标探测失败不能按路径不存在放行");
        }
        finally { Directory.Delete(dir, true); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newPath, string existingPath, IntPtr security);

    private static VideoEncoder Encoder(VideoEncoder.RateControlMode rate)
        => new VideoEncoder("unused", "unused.mkv", "unused.mp4", 1920, 1080, 60,
            VideoEncoder.Quality.Balanced, 1, false, videoCodec: VideoEncoder.VideoCodec.H264,
            rateControl: rate, crf: 17, cqp: 31, hardware: VideoEncoder.HardwareBackend.Cpu);

    private static string Invoke(VideoEncoder encoder, string method)
        => (string)typeof(VideoEncoder).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(encoder, null);

    private static string Read(string path) => SrcUtil.Source(Driver.ProjectDir, path);
    private static bool Before(string code, string first, string second)
        => code.IndexOf(first, StringComparison.Ordinal) >= 0
            && code.IndexOf(second, StringComparison.Ordinal) > code.IndexOf(first, StringComparison.Ordinal);

    private static string Body(string code, string method)
    {
        Match m = Regex.Match(code, @"(?m)^\s*(?:private|internal|public)\s+(?:static\s+)?[^\r\n=;]+\b" + method + @"\([^;]*?\)\s*\{");
        if (!m.Success) return "";
        int start = m.Index + m.Length, depth = 1;
        for (int i = start; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            if (code[i] == '}') depth--;
            if (depth == 0) return code.Substring(start, i - start);
        }
        return "";
    }

    private static string BlockAfter(string code, string anchor)
    {
        int at = code.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0) return "";
        int open = code.IndexOf('{', at + anchor.Length);
        if (open < 0) return "";
        int depth = 1;
        for (int i = open + 1; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            if (code[i] == '}') depth--;
            if (depth == 0) return code.Substring(open + 1, i - open - 1);
        }
        return "";
    }

}
