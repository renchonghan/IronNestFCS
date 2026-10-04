using System;
using System.IO;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// [DC] NukeAlarm — 核弹 (ATMC) 专属警报音 (自挂 AudioSource, 2D 全图可闻):
/// Load = 炮闩闭锁/膛内 ATMC 落位瞬间 → 单轮 (三声) 不循环;
/// Launch = 开火瞬间 (GC 击发确认回调) → 单次响, 落地停.
/// 音频 = UserData/IronNestFCS/siren_samples/{NukeLoad,NukeLunch}.wav (16-bit PCM 自解析).
/// 播放走 AudioClip.Create 的 PCMReaderCallback 流式重载 (本作 IL2CPP 裁掉了 SetData/DownloadHandlerAudioClip — CustomRecords 同款坑).
/// </summary>
public class NukeAlarm
{
    private const string LoadFile = "NukeLoad.wav";
    private const string LaunchFile = "NukeLunch.wav";

    private GameObject? _root;
    private AudioSource? _src;
    private ClipData? _load;
    private ClipData? _launch;

    public bool Ready { get; private set; }

    /// <summary>播放入口 (FcsModule 装配末段调用): 清 F9 残留旧实例 → 建 AudioSource → 解析两个 wav.</summary>
    public void Initialize()
    {
        var old = GameObject.Find("FCS2_NukeAlarm");
        if (old != null) UnityEngine.Object.Destroy(old); // F9 热重载残留旧实例 (3D 物件不随 ALC 卸载)
        _root = new GameObject("FCS2_NukeAlarm");
        _src = _root.AddComponent<AudioSource>();
        _src.spatialBlend = 0f;  // 2D: 无方位衰减, 玩家在哪都听见
        _src.playOnAwake = false;
        string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "IronNestFCS", "siren_samples");
        _load = LoadWav(Path.Combine(dir, LoadFile));
        _launch = LoadWav(Path.Combine(dir, LaunchFile));
        Ready = _load != null && _launch != null;
        MelonLogger.Msg(Ready ? $"[FCS] NukeAlarm ready ({LoadFile}/{LaunchFile})" : "[FCS] NukeAlarm: audio files missing, muted");
    }

    /// <summary>Load 警报 (炮闩闭锁/膛内落位瞬间): 单轮 (三声) 不循环. 已在响不重启.</summary>
    public void StartLoad()
    {
        if (!Ready || _src == null || _load == null) return;
        if (_src.clip == _load.Clip && _src.isPlaying) return;
        _src.clip = _load.Clip;
        _src.loop = false;
        _src.Play();
    }

    /// <summary>Launch 警报 (开火瞬间): 单次, 已在响不重启 (新一发开火 = 新触发).</summary>
    public void StartLaunch()
    {
        if (!Ready || _src == null || _launch == null) return;
        if (_src.clip == _launch.Clip && _src.isPlaying) return;
        _src.clip = _launch.Clip;
        _src.loop = false;
        _src.Play();
    }

    /// <summary>全停 + 销毁 (Shutdown/F9).</summary>
    public void Dispose()
    {
        try { _src?.Stop(); } catch { }
        if (_root != null) { try { UnityEngine.Object.Destroy(_root); } catch { } }
        _root = null;
        _src = null;
        _load = null;
        _launch = null;
        Ready = false;
    }

    // ===== 16-bit PCM WAV 自解析 (无需 CSCore — 素材已统一 16-bit) =====

    /// <summary>WAV 流式音轨 (PCMReaderCallback 回环供数 — 与 CustomRecords TrackPlayback 同款).</summary>
    private sealed class ClipData
    {
        public readonly AudioClip Clip;
        public readonly float[] Samples; // 交错 (立体声 L/R 交替)
        private readonly int _channels;
        private int _cursor;

        public ClipData(float[] samples, int channels, int sampleRate)
        {
            Samples = samples;
            _channels = channels;
            Clip = AudioClip.Create("NukeAlarm", samples.Length / channels, channels, sampleRate, true,
                (System.Action<Il2CppStructArray<float>>)Read, (System.Action<int>)SetPos);
        }

        private void Read(Il2CppStructArray<float> buf)
        {
            for (int i = 0; i < buf.Length; i++) {
                buf[i] = Samples[_cursor];
                _cursor = (_cursor + 1) % Samples.Length; // 回环 (loop 由 AudioSource 管理, 缓冲读完绕回)
            }
        }

        private void SetPos(int pos)
        {
            _cursor = pos * _channels % Samples.Length; // pos 单位 = 帧 (每帧含全部声道), 换算交错游标
        }
    }

    /// <summary>解析 16-bit PCM WAV (mono/stereo, 任意采样率); 失败返回 null (不炸).</summary>
    private static ClipData? LoadWav(string path)
    {
        try {
            var bytes = File.ReadAllBytes(path);
            int pos = 12; // 跳过 RIFF 头
            int channels = 1, sampleRate = 44100, bits = 16;
            int dataLen = 0, dataOff = 0;
            while (pos + 8 <= bytes.Length) {
                string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                int size = BitConverter.ToInt32(bytes, pos + 4);
                if (id == "fmt ") {
                    channels = BitConverter.ToInt16(bytes, pos + 10);
                    sampleRate = BitConverter.ToInt32(bytes, pos + 12);
                    bits = BitConverter.ToInt16(bytes, pos + 22);
                }
                else if (id == "data") {
                    dataOff = pos + 8;
                    dataLen = size;
                    break;
                }
                pos += 8 + size + (size & 1); // 块按偶数字节对齐
            }
            if (dataOff == 0 || dataLen <= 0 || bits != 16) return null;
            int frames = dataLen / (2 * channels);
            var samples = new float[frames * channels];
            for (int i = 0; i < frames * channels; i++) {
                short v = BitConverter.ToInt16(bytes, dataOff + i * 2);
                samples[i] = v / 32768f;
            }
            return new ClipData(samples, channels, sampleRate);
        }
        catch (Exception ex) {
            MelonLogger.Error($"[FCS] NukeAlarm LoadWav failed: {path} ({ex.Message})");
            return null;
        }
    }
}
