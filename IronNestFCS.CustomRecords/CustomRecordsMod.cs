using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using IronNestFCS.CustomRecords;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(IronNestFCS.CustomRecords.CustomRecordsMod),
    "IronNestFCS.CustomRecords", "2.0.0", "svr2kos2, Lancelot_Holland & DeepSeek(TM) V4")]
[assembly: MelonGame("Iron Nest", "Iron Nest Heavy Turret Simulator")]

namespace IronNestFCS.CustomRecords;

/// <summary>
/// 独立的 MelonMod：扫描 UserData/CustomRecords 下所有“带内嵌封面”的音频文件
/// （.mp3 / .wav / .flac），为每个文件克隆一张场景里的 RecordDisk，
/// 用该文件的封面合成唱片贴图、用其解码后的 PCM 作为音轨。
///
/// 用户只需把音频文件丢进 CustomRecords 即可，无需再手工准备 a.wav / diskTexture.png，
/// 也不再从 StreamingAssets 读取素材。
///
/// 解码与标签读取走纯托管库（NAudio / NAudio.Flac / TagLib#），不受本作 IL2CPP 裁剪影响；
/// 只有"把结果塞回 Unity"这一步受 IL2CPP 约束，沿用已验证可用的 API（见各处注释）。
/// 进场景后轮询唱片对象（兼容 Demo 版的精确名 "RecordDisk" 与正式版的
/// "RecordDisk 1"~"RecordDisk 13" 命名），出现时执行一次；切换场景自动重置。
/// </summary>
public class CustomRecordsMod : MelonMod
{
    private readonly List<GameObject> diskClones = new();
    // 每张克隆盘各自持有的流式音轨状态：PCMReaderCallback 按需供数，必须保活托管侧
    // 样本缓冲与读游标，否则被 GC 回收后回调里访问就是野指针。模块存活期间一直持有。
    private readonly List<TrackPlayback> playbacks = new();
    private bool done;
    private bool reportedMissing;
    private bool candidatesLogged;
    // 场景加载时刻（Time.realtimeSinceStartup）：相机渲染一两帧后 Renderer.isVisible 才可靠，
    // 宽限期内不把盘放到"不可见模板"的位置。
    private float sceneLoadedAt;
    private const float VisibleWaitSeconds = 5f;

    public override void OnUpdate()
    {
        if (done)
            return;

        // 轮询模板盘出现即执行一次。
        var src = FindRecordDiskTemplate();
        if (src == null)
        {
            // 找不到时只提示一次，避免每帧刷屏；场景切换后重置再提示。
            if (!reportedMissing)
            {
                reportedMissing = true;
                MelonLogger.Msg("[CustomRecords] Waiting for a record disk in the scene...");
            }
            return;
        }

        done = true;
        try
        {
            CreateCustomRecordss(src);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[CustomRecords] Failed to create custom disks: {ex}");
        }
    }

    /// <summary>
    /// 场景切换：销毁旧克隆盘（游戏不销毁它们，会累积），清空托管侧状态并重置标志，
    /// 使新场景中再次轮询创建。避免"done 一次置位永不重试"导致换场景后不再生成。
    /// </summary>
    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        foreach (var clone in diskClones)
        {
            if (clone != null)
                Object.Destroy(clone);
        }
        diskClones.Clear();
        playbacks.Clear();
        done = false;
        reportedMissing = false;
        candidatesLogged = false;
        sceneLoadedAt = Time.realtimeSinceStartup;
    }

    /// <summary>
    /// 定位可作模板的唱片对象，按优先级：
    /// 1) 精确名 "RecordDisk"（Demo 版场景）；
    /// 2) 可见的桌面（Drag Surface）唱片（排除 "(Clone)"）；
    /// 3) 可见的任意命名盘；
    /// 4) 场景加载宽限期（VisibleWaitSeconds）内继续等待——**必须等场景稳定再创建**：
    ///    过早创建会被 Drag Surface 系统初始化时收编（inactive + 位置重置，导致盘"消失"）；
    /// 5) 宽限期后兜底：桌面盘优先（避免选到 Barbet/Delivery Guy 等位置的装饰竖盘）→ 任意命名盘。
    /// </summary>
    private GameObject? FindRecordDiskTemplate()
    {
        var exact = GameObject.Find("RecordDisk");
        if (exact != null)
            return exact;

        var items = Object.FindObjectsOfType<RecordItem>(true);

        // 诊断：场景首次查找时打印全部候选（名称/坐标/父对象/可见性），
        // 便于确认真实可见盘的判别特征。
        if (!candidatesLogged && items.Length > 0)
        {
            candidatesLogged = true;
            foreach (var item in items)
            {
                var go = item.gameObject;
                var r = go.GetComponentInChildren<MeshRenderer>(true);
                string parent = go.transform.parent != null ? go.transform.parent.name : "(null)";
                MelonLogger.Msg($"[CustomRecords] cand: {go.name} @ {go.transform.position} " +
                                $"parent={parent} active={go.activeInHierarchy} " +
                                $"visible={(r != null && r.enabled && r.isVisible)}");
            }
        }

        GameObject? surface = null, surfaceVisible = null, named = null, namedVisible = null, any = null;
        foreach (var item in items)
        {
            var go = item.gameObject;
            if (go.name.Contains("(Clone)"))
                continue; // 排除此前创建的克隆盘
            string parentName = go.transform.parent != null ? go.transform.parent.name : "";
            if (parentName == "Drag Surface")
            {
                surface ??= go;
                if (IsRenderedVisible(go))
                    surfaceVisible ??= go;
            }
            if (go.name.StartsWith("RecordDisk"))
            {
                named ??= go;
                if (IsRenderedVisible(go))
                    namedVisible ??= go;
            }
            else
            {
                any ??= go;
            }
        }

        // 可见桌面盘 → 可见命名盘 → 宽限期等待 → 桌面盘兜底 → 命名盘兜底 → 任意 RecordItem。
        if (surfaceVisible != null)
            return surfaceVisible;
        if (namedVisible != null)
            return namedVisible;
        if (Time.realtimeSinceStartup - sceneLoadedAt < VisibleWaitSeconds)
            return null; // 等场景稳定（相机渲染出可见性、Drag Surface 初始化完成），再创建
        return surface ?? named ?? any;
    }

    /// <summary>对象自身或子物体存在"启用且可见"的 MeshRenderer。</summary>
    private static bool IsRenderedVisible(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
        foreach (var r in renderers)
        {
            if (r.enabled && r.isVisible)
                return true;
        }
        return false;
    }

    public override void OnDeinitializeMelon()
    {
        foreach (var clone in diskClones)
        {
            if (clone != null)
                Object.Destroy(clone);
        }
        diskClones.Clear();
        playbacks.Clear();
    }

    /// <summary>
    /// 列出 CustomRecords 里所有受支持且带封面的文件，逐个克隆 RecordDisk 并装配。
    /// 多张盘沿原盘朝同一方向依次排开，避免叠在一起。
    /// </summary>
    private void CreateCustomRecordss(GameObject src)
    {
        var dir = System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "CustomRecords");
        if (!System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
            MelonLogger.Msg($"[CustomRecords] Created empty folder, drop audio files here: {dir}");
            return;
        }

        // 枚举受支持扩展名的文件，按文件名排序得到稳定顺序。
        var files = new List<string>();
        foreach (var f in System.IO.Directory.GetFiles(dir))
        {
            if (AudioImport.IsSupported(f))
                files.Add(f);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);

        if (files.Count == 0)
        {
            MelonLogger.Msg($"[CustomRecords] No supported audio (.mp3/.wav/.flac) in {dir}");
            return;
        }

        int placed = 0;
        foreach (var file in files)
        {
            try
            {
                // 封面来源：优先同名图片文件（song.mp3 配 song.png/.jpg/.jpeg，音频与图片分开存放，
                // 无需给音频嵌入标签），回退到音频内嵌封面（TagLib 读取）。
                var cover = ReadSidecarCover(file) ?? TagReader.ReadCover(file);
                if (cover == null)
                {
                    MelonLogger.Warning($"[CustomRecords] Skip (no cover): {System.IO.Path.GetFileName(file)}" +
                                        " (no sidecar image, no embedded cover)");
                    continue;
                }

                if (CreateOneDisk(src, file, cover, placed))
                    placed++;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomRecords] Failed on {System.IO.Path.GetFileName(file)}: {ex}");
            }
        }

        MelonLogger.Msg($"[CustomRecords] Done: {placed} disk(s) created from {files.Count} file(s).");
    }

    /// <summary>
    /// 查找与音频文件同名的封面图片（song.mp3 → song.png / song.jpg / song.jpeg），
    /// 存在则读回原始字节；找不到返回 null，由调用方回退到音频内嵌封面。
    /// </summary>
    private static byte[]? ReadSidecarCover(string audioPath)
    {
        var dir = System.IO.Path.GetDirectoryName(audioPath);
        var baseName = System.IO.Path.GetFileNameWithoutExtension(audioPath);
        if (dir == null)
            return null;
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
        {
            var p = System.IO.Path.Combine(dir, baseName + ext);
            if (System.IO.File.Exists(p))
                return System.IO.File.ReadAllBytes(p);
        }
        return null;
    }

    /// <summary>
    /// 为单个音频文件克隆一张盘，装配合成封面贴图与流式音轨。
    /// <paramref name="index"/> 用于沿原盘前方依次错开摆放。
    /// </summary>
    private bool CreateOneDisk(GameObject src, string file, byte[] cover, int index)
    {
        // 先解码音频，失败就别建盘了。
        float[] samples;
        int channels, sampleRate;
        try
        {
            samples = AudioImport.Decode(file, out channels, out sampleRate);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[CustomRecords] Decode failed for {System.IO.Path.GetFileName(file)}: {ex}");
            return false;
        }
        if (samples.Length == 0 || channels <= 0 || sampleRate <= 0)
        {
            MelonLogger.Warning($"[CustomRecords] Empty/invalid audio: {System.IO.Path.GetFileName(file)}");
            return false;
        }

        // 合成封面贴图。
        var tex = CoverImage.Build(cover);
        if (tex == null)
        {
            MelonLogger.Warning($"[CustomRecords] Cover decode failed: {System.IO.Path.GetFileName(file)}");
            return false;
        }

        // 克隆盘并脱离原父级：模板可能在 inactive 容器或唱片机内部，继承父级会导致
        // 克隆盘整体不可见（SetActive(true) 只激活自身，救不了 inactive 的父链）。
        var disk = Object.Instantiate(src);
        diskClones.Add(disk);
        disk.transform.SetParent(null, true);
        if (!disk.activeSelf)
            disk.SetActive(true);

        // 位置：沿模板 right 的水平分量依次排开（间距 0.5 米），高度抬高 0.02 防 Z-fighting。
        // 不沿用原来的 -forward 偏移：正式版的盘多为平放（forward 朝上），-fwd 会把新盘
        // 埋进桌面下方；right 方向与盘面垂直，始终是水平方向，放出来的盘必在桌上可见。
        var dir = src.transform.right;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f)
            dir = Vector3.right; // 模板几乎完全竖直时兜底
        dir.Normalize();
        var pos = src.transform.position + dir * (0.5f * ((index > 4 ? 4 : index) + 1));
        pos.y += 0.02f;
        disk.transform.position = pos;
        disk.transform.rotation = src.transform.rotation;

        var recordItem = disk.GetComponent<RecordItem>();

        // 装配流式音轨。
        var playback = new TrackPlayback(samples, channels);
        playbacks.Add(playback);
        AssignTrack(recordItem, playback, sampleRate);

        // 替换唱片贴图：游戏盘面为两层结构（Record Disk Blend 的 4 材质槽位）：
        //   mat[0] VinylRecord*Albedo = 整体黑胶（保持原样，不替换）
        //   mat[1] CoverArt_*         = 中心封面（替换为自定义封面）
        //   mat[2]/mat[3]             = Outline Mask/Fill（不动）
        // 注意：绝不写 MaterialPropertyBlock——MPB 是 renderer 级，会覆盖全部材质槽位，
        // 导致合成贴图同时出现在中心与整体（此前"双黑圈"的根因）。

        // 诊断：打印每个 MeshRenderer 的材质槽位贴图名，确认替换目标。
        foreach (var mr in recordItem.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = mr.materials;
            MelonLogger.Msg($"[CustomRecords] {mr.gameObject.name}: {mats.Length} material(s)");
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                string main = m.mainTexture != null ? m.mainTexture.name : "";
                string bas = m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null
                    ? m.GetTexture("_BaseMap").name : "";
                MelonLogger.Msg($"  mat[{i}] shader={m.shader?.name} main={main} base={bas}");
            }
        }

        foreach (var mr in recordItem.GetComponentsInChildren<MeshRenderer>(true))
            SwapCoverArtTextures(mr, tex);
        foreach (var sr in recordItem.GetComponentsInChildren<SpriteRenderer>(true))
        {
            MelonLogger.Msg($"[CustomRecords] renderer: {sr.gameObject.name} type=SpriteRenderer " +
                            $"sprite={sr.sprite?.name} tex={sr.sprite?.texture?.name}");
            var oldSprite = sr.sprite;
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), oldSprite != null ? oldSprite.pixelsPerUnit : 100f);
            sr.sprite = sprite;
        }

        // 尽力设置显示名（字段在不同版本可能不同，存在才设）。
        TrySetDisplayName(recordItem, TagReader.ReadTitle(file));

        MelonLogger.Msg($"[CustomRecords] + {System.IO.Path.GetFileName(file)}  " +
                        $"({(float)(samples.Length / channels) / sampleRate:F1}s, {channels}ch@{sampleRate}Hz) " +
                        $"<- {src.name} @ {pos}");
        return true;
    }

    /// <summary>
    /// 只替换 <see cref="MeshRenderer"/> 材质数组中贴图名以 "CoverArt" 开头的槽位
    /// （中心封面层），整体黑胶（VinylRecord*）与描边（Outline*）槽位保持原样。
    /// 替换方式：new Material(原材质) 生成实例副本再改贴图（不影响共享材质/原版盘），
    /// 最后把数组赋回 renderer.materials。不写 MaterialPropertyBlock（renderer 级，
    /// 会覆盖全部槽位）。
    /// </summary>
    private static void SwapCoverArtTextures(MeshRenderer r, Texture2D tex)
    {
        var mats = r.materials;
        bool changed = false;
        for (int i = 0; i < mats.Length; i++)
        {
            var m = mats[i];
            string main = m.mainTexture != null ? m.mainTexture.name : "";
            string bas = m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null
                ? m.GetTexture("_BaseMap").name : "";
            if (!main.StartsWith("CoverArt") && !bas.StartsWith("CoverArt"))
                continue;

            var instance = new Material(m); // 实例副本，不影响共享材质/原版盘
            instance.mainTexture = tex;
            if (instance.HasProperty("_BaseMap"))
                instance.SetTexture("_BaseMap", tex);
            mats[i] = instance;
            changed = true;
            MelonLogger.Msg($"[CustomRecords] swapped cover mat[{i}] ({m.name}) of {r.gameObject.name}");
        }
        if (changed)
            r.materials = mats;
    }

    /// <summary>
    /// 用带 PCMReaderCallback 的 AudioClip.Create 构造流式音轨并写入 RecordItem.tracks。
    /// 不用 DownloadHandlerAudioClip / AudioClip.SetData：本作 IL2CPP 把它们的 ctor / Span
    /// 依赖裁掉了，运行时会 MissingMethodException。PCMReaderCallback 重载是纯
    /// il2cpp_runtime_invoke，无 Span 依赖，Unity 播放时回调拉取 PCM。
    /// </summary>
    private static void AssignTrack(RecordItem recordItem, TrackPlayback playback, int sampleRate)
    {
        int lengthSamples = playback.LengthPerChannel; // 每声道采样数

        // stream=true：Unity 不一次性缓冲，反复回调取数据，适合循环。
        // 回调里禁止访问 IL2CPP 复杂对象，只读写传入的 float 数组，最安全。
        AudioClip.PCMReaderCallback reader = (System.Action<Il2CppStructArray<float>>)playback.PcmRead;
        AudioClip.PCMSetPositionCallback setPos = (System.Action<int>)playback.PcmSetPosition;
        var clip = AudioClip.Create("CustomRecord", lengthSamples, playback.Channels, sampleRate, true, reader, setPos);

        recordItem.tracks = new Il2CppReferenceArray<AudioClip>(new[] { clip });
        recordItem.loop = true;
    }

    /// <summary>
    /// 反射设置常见的显示名字段（displayName / nameText 等）。字段缺失或类型不符就静默跳过——
    /// 这只是锦上添花，失败不影响音轨与贴图。
    /// </summary>
    private static void TrySetDisplayName(RecordItem recordItem, string title)
    {
        try
        {
            recordItem.gameObject.name = title;
        }
        catch
        {
            // 显示名是可选项，忽略任何失败。
        }
    }
}

/// <summary>
/// 单张盘的流式播放状态：持有交错 float 样本与读游标，提供 PCM 回调。
/// 每张盘一个实例，互不干扰。被 CustomRecordsMod 长期持有以防 GC。
/// </summary>
internal sealed class TrackPlayback
{
    private readonly float[] _samples; // 交错样本，范围 [-1,1]
    private int _readPos;

    internal int Channels { get; }
    internal int LengthPerChannel => _samples.Length / Channels;

    internal TrackPlayback(float[] samples, int channels)
    {
        _samples = samples;
        Channels = channels;
    }

    /// <summary>
    /// PCMReaderCallback：Unity 每次要播一段时调用，把接下来的样本写满 data。
    /// 缓冲读完即环回（配合 RecordItem.loop / AudioSource 循环）。游标自增。
    /// </summary>
    internal void PcmRead(Il2CppStructArray<float> data)
    {
        var src = _samples;
        if (src.Length == 0)
        {
            for (int i = 0; i < data.Length; i++) data[i] = 0f;
            return;
        }
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = src[_readPos];
            if (++_readPos >= src.Length) _readPos = 0;
        }
    }

    /// <summary>
    /// PCMSetPositionCallback：Unity seek 或循环回起点时调用，参数是“每声道”采样位置。
    /// 换算成交错缓冲的绝对索引。循环回零时 position 多为 0。
    /// </summary>
    internal void PcmSetPosition(int positionSamples)
    {
        var src = _samples;
        if (src.Length == 0) { _readPos = 0; return; }
        if (positionSamples <= 0) { _readPos = 0; return; }
        // 每声道位置 → 交错绝对索引，并钳制到缓冲范围内。
        long abs = (long)positionSamples * Channels;
        _readPos = (int)Math.Min(abs, src.Length - 1);
    }
}
