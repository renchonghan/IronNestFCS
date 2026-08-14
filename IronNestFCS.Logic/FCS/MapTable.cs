using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class MapTable {
    private Transform? turret;
    private ImpactMarkerManager? impactManager; // 铁巢网格位置真源: 它把真实炮塔世界坐标投到地图网格
    private Dictionary<int, Transform> artilleries;

    // 沙盘校准: 网格 A-T × 1-10 映射到棋子局部系
    // 格长用代码原有比例 1/3.8164 (1 棋盘单位 = 3.8164 km); 左下角用 1 药平射真实落点解出:
    // 目标 (-1.88,0.97) = 左下角 + 网格 (2.85,8.95) × 格长
    private static readonly Vector2 MapBottomLeft = new(-2.6238f, -1.3741f); // 网格原点 (A1) 在棋子空间的位置 (含目测修正: 右 0.1 小格 / 上 1/20 小格)
    private static readonly float MapCellSize = 1f / 3.8164f;                 // 每大格的棋子空间尺寸
    private Transform? fireMissionRoot;
    private FireMission? fireMission;
    private Transform? mapSurface;
    
    /// <summary>炮塔 Transform(供扫荡/世界坐标打击换算使用)</summary>
    public Transform? Turret => turret;
    
    public bool TryBind() {
        artilleries = new Dictionary<int, Transform>();
        var turretObject = GameObject.Find("Player Turret Piece");
        if (turretObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Player Turret Piece, 当前场景尚未就绪");
            return false;
        }

        var mapObject = GameObject.Find("Draggable Surface");
        if (mapObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Draggable Surface, 当前场景尚未就绪");
            return false;
        }

        turret = turretObject.transform;
        mapSurface = mapObject.transform;
        impactManager = GameObject.Find("---ImpactMarkerManager")?.GetComponent<ImpactMarkerManager>();
        var map = mapObject.transform;
        for (var i = 0; i < map.childCount; ++i) {
            var t = map.GetChild(i);
            if (t.name != "MapToken_Artillery") continue;
            var tmp = t.GetComponentInChildren<Il2CppTMPro.TextMeshPro>();
            if (tmp == null) continue;
            if (!int.TryParse(tmp.text, out var id)) continue;
            artilleries.Add(id, t);
        }
        MelonLogger.Msg($"[FCS] 找到 Player Turret Piece: {turret}, Artilleries: {artilleries.Count}");
        var fireMissionObject = GameObject.Find("Fire Mission Root");
        if (fireMissionObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Fire Mission Root, 当前场景尚未就绪");
            return false;
        }

        fireMissionRoot = fireMissionObject.transform;
        fireMission = fireMissionRoot.GetComponent<FireMission>();
        return fireMission != null;
    }

    /// <summary>
    /// 把铁巢棋子吸附到游戏网格位置: 铁巢底座 RectTransform (turretBase) 就挂在
    /// MapRoot 网格空间里, 它的 localPosition 就是铁巢当前网格坐标, 游戏自己维护
    /// (归位/紧急转移都会更新). 再经四角校准常数映射到棋子局部系.
    /// </summary>
    public void SyncIronNestToken()
    {
        if (turret == null || impactManager == null || impactManager.turretController == null) return;
        var tb = impactManager.turretController.turretBase;
        if (tb == null) return;
        var grid = tb.localPosition;
        var local = new Vector3(MapBottomLeft.x + grid.x * MapCellSize,
                                MapBottomLeft.y + grid.y * MapCellSize,
                                turret.localPosition.z);
        turret.localPosition = local;
    }

    /// <summary>把第 index 号地图标记放到世界坐标对应位置(雷达自动标点用)</summary>
    public void SetMarkerWorldPos(int index, Vector3 worldPos)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        if (mapSurface == null) return;
        var local = mapSurface.InverseTransformPoint(worldPos);
        local.z = marker.localPosition.z;
        marker.localPosition = local;
    }

    /// <summary>把第 index 号地图标记复位到炮塔位置(目标不存在/已摧毁时)</summary>
    public void ResetMarker(int index)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        if (turret == null || mapSurface == null) return;
        marker.localPosition = mapSurface.InverseTransformPoint(turret.position);
    }

    /// <summary>按公里坐标设置第 index 号标记(KM 系 → 地图局部系, 比例 3.8164)</summary>
    public void SetMarkerByKmPos(int index, Vector2 kmPos)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        var local = new Vector3(kmPos.x / 3.8164f, kmPos.y / 3.8164f, marker.localPosition.z);
        marker.localPosition = local;
    }

    /// <summary>直接按地图局部坐标设置第 index 号标记.</summary>
    public void SetMarkerLocalPos(int index, Vector2 localPos)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        marker.localPosition = new Vector3(localPos.x, localPos.y, marker.localPosition.z);
    }

    public ArtilleryTask? GetMarkTarget(int index) {
        if (turret == null) {
            MelonLogger.Error("[FCS] GetMarkTarget: turret unbound");
            return null;
        }

        if (mapSurface == null) {
            MelonLogger.Error("[FCS] GetMarkTarget: map surface unbound");
            return null;
        }

        if (index > artilleries.Count) {
            MelonLogger.Error($"[FCS] GetMarkTarget: index {index} out of range, artillery count: {artilleries.Count}");
            return null;
        }

        // 炮塔世界坐标 → 地图局部坐标, 与标记处于同一坐标系后再相减
        // 铁巢紧急转移后炮塔移动也能得到正确的相对偏移(旧实现直接减 turret.localPosition, 坐标系不同, 转移后失准)
        var turretLocalOnMap = mapSurface.InverseTransformPoint(turret.position);
        var target = artilleries[index].localPosition - turretLocalOnMap;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        var task = new ArtilleryTask {
            angel = angle,
            distance = dist,
            position = artilleries[index].localPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f)
        };
        return task;
    }

    public List<EntityLocation> GetAllFireMissionEntities() {
        List<EntityLocation> res = new();
        if (fireMissionRoot == null) {
            return res;
        }

        for (var i = 0; i < fireMissionRoot.childCount; ++i) {
            var m = fireMissionRoot.GetChild(i).GetComponent<EntityLocation>();
            if (m != null) res.Add(m);
        }
        return res;
    }
    
}
