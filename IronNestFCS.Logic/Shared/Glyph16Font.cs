using System.Collections.Generic;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// TM1629A 十六段米字数码字形工具 (自 MapTable 抽出, 2.0 共享静态工具第 1 步):
/// 段坐标/笔画位/ASCII 字形表 + 单字符画法. 供 DC 渲染线程画编号 (00T/米字数码) 复用.
/// </summary>
public static class Glyph16Font {
    /// <summary>字符宽 (板面单位), 与实体标签基准一致.</summary>
    public const float LabelSegW = 0.045f;

/// <summary>
    /// TM1629A 十六段米字数码管: 段坐标 (单位格: 宽 1, 高 1.6, 中心 0.5,0.8) 与笔画位映射.
    /// 微调字形直接改下方 Glyph16: ['X'] = 段名按位或, 例如 ['L'] = F|E|D1|D2.
    /// 笔画位与布局 (bit0=a1 ... bit15=m):
    ///      a1|a2
    ///    f   h   b
    ///      g1|g2
    ///    e   i   c
    ///      d1|d2
    /// 对角: j 左上->中, k 右上->中, l 左下->中, m 右下->中
    /// </summary>
    private static readonly Dictionary<string, (Vector2, Vector2)> Seg16 = new() {
        ["a1"] = (new Vector2(0.05f, 1.6f), new Vector2(0.5f, 1.6f)),
        ["a2"] = (new Vector2(0.5f, 1.6f), new Vector2(0.95f, 1.6f)),
        ["b"] = (new Vector2(0.95f, 1.6f), new Vector2(0.95f, 0.8f)),
        ["c"] = (new Vector2(0.95f, 0.8f), new Vector2(0.95f, 0f)),
        ["d1"] = (new Vector2(0.05f, 0f), new Vector2(0.5f, 0f)),
        ["d2"] = (new Vector2(0.5f, 0f), new Vector2(0.95f, 0f)),
        ["e"] = (new Vector2(0.05f, 0.8f), new Vector2(0.05f, 0f)),
        ["f"] = (new Vector2(0.05f, 1.6f), new Vector2(0.05f, 0.8f)),
        ["g1"] = (new Vector2(0.05f, 0.8f), new Vector2(0.5f, 0.8f)),
        ["g2"] = (new Vector2(0.5f, 0.8f), new Vector2(0.95f, 0.8f)),
        ["h"] = (new Vector2(0.5f, 0.8f), new Vector2(0.5f, 1.6f)),
        ["i"] = (new Vector2(0.5f, 0.8f), new Vector2(0.5f, 0f)),
        ["j"] = (new Vector2(0.05f, 1.6f), new Vector2(0.5f, 0.8f)),
        ["k"] = (new Vector2(0.95f, 1.6f), new Vector2(0.5f, 0.8f)),
        ["l"] = (new Vector2(0.05f, 0f), new Vector2(0.5f, 0.8f)),
        ["m"] = (new Vector2(0.95f, 0f), new Vector2(0.5f, 0.8f)),
    };

    // 笔画位定义: ushort 掩码, bit N = 第 N 段, 与 SegBit16 顺序一致 (0-7 外框, 8-11 中间十字, 12-15 对角)
    private const ushort A1 = 1 << 0, A2 = 1 << 1, B = 1 << 2, C = 1 << 3,
                        D1 = 1 << 4, D2 = 1 << 5, E = 1 << 6, F = 1 << 7,
                        G1 = 1 << 8, G2 = 1 << 9, H = 1 << 10, I = 1 << 11,
                        J = 1 << 12, K = 1 << 13, L = 1 << 14, M = 1 << 15;

    private static readonly string[] SegBit16 = { "a1", "a2", "b", "c", "d1", "d2", "e", "f", "g1", "g2", "h", "i", "j", "k", "l", "m" };

    /// <summary>
    /// 可打印 ASCII (0x20-0x7E) 字形表, ushort 位掩码按 SegBit16 点亮.
    /// 无法表达的字符 (如 ~) 置 0 显示空白.
    /// </summary>
    private static readonly Dictionary<char, ushort> Glyph16 = new() {
        // ---- 数字 ----
        ['0'] = A1|A2|B|C|D1|D2|E|F,
        ['1'] = B|C,
        ['2'] = A1|A2|B|G1|G2|E|D1|D2,
        ['3'] = A1|A2|B|G1|G2|C|D1|D2,
        ['4'] = F|G1|G2|B|C,
        ['5'] = A1|A2|F|G1|G2|C|D1|D2,
        ['6'] = A1|A2|F|E|G1|G2|C|D1|D2,
        ['7'] = A1|A2|B|C,
        ['8'] = A1|A2|B|C|D1|D2|E|F|G1|G2,
        ['9'] = A1|A2|F|G1|G2|B|C|D1|D2,
        // ---- 大写字母 ----
        ['A'] = A1|A2|B|C|E|F|G1|G2,
        ['B'] = A1|A2|F|E|G1|D1|D2|K|M,   // 左竖 + 三横 + 右侧斜边 k/m
        ['C'] = A1|A2|D1|D2|E|F,
        ['D'] = F|E|J|L,   // 左竖 + 左侧尖角 j/l (上 \ 下 /)
        ['E'] = A1|A2|F|G1|G2|E|D1|D2,
        ['F'] = A1|A2|F|G1|G2|E,
        ['G'] = A1|A2|F|E|G2|C|D1|D2,
        ['H'] = F|E|G1|G2|B|C,
        ['I'] = A1|A2|H|I|D1|D2,
        ['J'] = A1|A2|H|I|D1,   // 上横 + 中竖 + 左下钩
        ['K'] = F|E|G1|K|M,   // 中横只留左半
        ['L'] = F|E|D1|D2,
        ['M'] = F|E|J|K|B|C,
        ['N'] = F|E|K|L|B|C,
        ['O'] = A1|A2|B|C|D1|D2|E|F,
        ['P'] = A1|A2|F|E|G1|G2|B,
        ['Q'] = A1|A2|B|C|D1|D2|E|F|M,
        ['R'] = A1|A2|F|E|G1|G2|B|M,   // 上横 + 左竖全 f/e + 中横 g1g2 + 右上竖 b + 斜腿 m
        ['S'] = A1|A2|J|M|D1|D2,   // Z 镜像形, 与 5 区分 (齐射 S 标记用)
        ['T'] = A1|A2|H|I,
        ['U'] = F|E|B|C|D1|D2,
        ['V'] = F|E|K|L,   // 左竖 f/e + 整条斜线 k/l (右上到左下)
        ['W'] = E|F|B|C|J|K|L|M,
        ['X'] = J|K|L|M,
        ['Y'] = J|K|I,
        ['Z'] = A1|A2|K|L|D1|D2,
        // ---- 小写字母 (暂时用不上, 未调试, a 已知有误待定) ----
        ['a'] = E|F|G1|G2|C|D1|D2,
        ['b'] = F|E|G1|G2|C|D1|D2,
        ['c'] = E|G1|G2|D1|D2,
        ['d'] = B|C|G1|G2|E|D1|D2,
        ['e'] = A1|F|E|G1|G2|D1|D2,
        ['f'] = A1|F|G1|G2|E,
        ['g'] = A1|A2|F|G1|G2|C|D1|D2,
        ['h'] = F|E|G1|G2|C,
        ['i'] = I,
        ['j'] = C|I|D1|D2,
        ['k'] = F|E|G1|G2|K|M,
        ['l'] = F|E,
        ['m'] = E|F|G1|G2|H|B|C,
        ['n'] = E|G1|G2|C,
        ['o'] = E|C|G1|G2|D1|D2,
        ['p'] = A1|F|E|G1|G2|B,
        ['q'] = A1|A2|B|C|G1|G2|D1|D2,
        ['r'] = E|G1|G2,
        ['s'] = A1|A2|F|G1|G2|C|D1|D2,
        ['t'] = F|E|G1|G2|I,
        ['u'] = E|C|D1|D2,
        ['v'] = J|K,
        ['w'] = E|C|J|K|L|M,
        ['x'] = K|L,
        ['y'] = K|L|I,
        ['z'] = A1|A2|G1|G2|D1|D2,
        // ---- 符号 ----
        [' '] = 0,
        ['!'] = H|D2,   // 中上竖 + 右半底横
        ['"'] = F|B,
        ['#'] = F|E|B|C|G1|G2,
        ['$'] = A1|A2|F|G1|G2|C|D1|D2|H|I,
        ['%'] = F|C|K|L,
        ['&'] = A1|F|E|G1|G2|C|D1|D2,
        ['\''] = H,   // 中上竖
        ['('] = A1|F|E,   // 左上横 + 左竖两段
        [')'] = B|C|D2,   // 右竖两段 + 右下横
        ['*'] = J|K|L|M|H|I,
        ['+'] = G1|G2|H|I,
        [','] = L,
        ['-'] = G1|G2,
        ['.'] = G1|D1|E|I,   // 左下角画圈: g1 上 / d1 下 / e 左 / i 右
        ['/'] = K|L,
        [':'] = G1|D1,   // 左中横 + 左下横
        [';'] = A1|L,   // 逗号 + 左上横
        ['<'] = J|L,
        ['='] = G1|G2|D1|D2,
        ['>'] = K|M,
        ['?'] = A1|A2|F|K|I,   // 上横 + 左竖上半 + 半斜线(k 右上到中) + 中下竖
        ['@'] = A1|A2|B|C|E|F|G2|D2|I,   // 无底横的 0 + 右下角圈 (g2 d2 c i)
        ['['] = A2|D2|H|I,   // 右半横 (钩朝右) + 中竖
        ['\\'] = J|M,
        [']'] = A1|D1|H|I,   // 左半横 (钩朝左) + 中竖
        ['^'] = L|M,   // 下面两条斜杠 l/m 组成尖朝上的 ^
        ['_'] = D1|D2,
        ['`'] = J,
        ['{'] = A2|G1|D2|H|I,
        ['|'] = H|I,
        ['}'] = A1|G2|D1|H|I,
        ['~'] = A1|G1|F|H,  // 左上角圈: 当 ° 用 (a1 上 / g1 下 / f 左 / h 右)
    };

    /// <summary>画一个字符: scale = 字符宽 (板面单位), 字形高 1.6 x scale, 笔画粗随字号同比缩放 (实体标签基准 0.008).</summary>
    public static void DrawCharSegments(Transform parent, char ch, Color color, float x0, float scale)
    {
        if (!Glyph16.TryGetValue(ch, out var mask)) mask = A1|A2|B|C|D1|D2|E|F|G1|G2; // 未知字符全亮 (8 形)
        for (int bit = 0; bit < SegBit16.Length; bit++) {
            if ((mask & (1 << bit)) == 0) continue;
            var (a, b) = Seg16[SegBit16[bit]];
            var go = new GameObject("FCS_LabelSeg");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<Il2CppShapes.Line>();
            line.Thickness = 0.008f * (scale / LabelSegW);
            line.Start = new Vector3(x0 + a.x * scale, a.y * scale, 0f);
            line.End = new Vector3(x0 + b.x * scale, b.y * scale, 0f);
            line.Color = color;
            line.ColorStart = color;
            line.ColorEnd = color;
            var r = go.GetComponent<Renderer>(); // 强制渲染队列到顶: 与 SandboxRenderer 线同款, 防照片穿插 (DC 所有元素统一 5000)
            if (r != null) r.material.renderQueue = 5000;
        }
    }
}
