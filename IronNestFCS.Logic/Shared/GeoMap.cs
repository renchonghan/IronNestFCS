using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 沙盘坐标系换算静态工具 (自 MapTable 抽出, 2.0 共享静态工具第 2 步):
/// 网格 A-T × 1-10 ↔ 棋子局部系 ↔ 公里. 供 DC 数据循环 / FC 诸元计算复用.
/// </summary>
public static class GeoMap {
    // 沙盘校准: 网格 A-T × 1-10 映射到棋子局部系
    // 格长用代码原有比例 1/3.8164 (1 棋盘单位 = 3.8164 km); 左下角用 1 药平射真实落点解出:
    // 目标 (-1.88,0.97) = 左下角 + 网格 (2.85,8.95) × 格长
    public static readonly Vector2 MapBottomLeft = new(-2.6238f, -1.3741f); // 网格原点 (A1) 在棋子空间的位置 (含目测修正: 右 0.1 小格 / 上 1/20 小格)
    public static readonly float MapCellSize = 1f / 3.8164f;                 // 每大格的棋子空间尺寸
    /// <summary>公里 → 棋子局部系偏移 (Fire Mission Root 局部 km 坐标基准, 自既有公式提取).</summary>
    public static readonly Vector3 KmOffset = new(10.016f, 5.235f, 0f);

    /// <summary>网格坐标 → 棋子局部坐标.</summary>
    public static Vector3 GridToLocal(Vector2 grid, float z = 0f) =>
        new(MapBottomLeft.x + grid.x * MapCellSize, MapBottomLeft.y + grid.y * MapCellSize, z);

    /// <summary>棋子局部坐标 → 公里坐标 (Fire Mission 系).</summary>
    public static Vector3 LocalToKm(Vector3 local) => local * 3.8164f + KmOffset;

    /// <summary>地图局部系两点差 → (距离 km, 方位角°, 0-360).</summary>
    public static (float dist, float angle) RelToTarget(Vector2 from, Vector2 to) {
        var t = to - from;
        float dist = t.magnitude * 3.8164f;
        float angle = Vector2.SignedAngle(t, Vector2.up);
        if (angle < 0) angle += 360;
        return (dist, angle);
    }
}
