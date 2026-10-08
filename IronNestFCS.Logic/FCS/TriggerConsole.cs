using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class TriggerConsole {
    private LookAtTarget? _taskCheck;
    private LookAtTarget? _bulletCheck;
    private LookAtTarget? _rotationCheck;
    private LookAtTarget? _elevationCheck;
    private LookAtTarget? _readyFire;
    private LookAtTarget? _armLeft;
    private LookAtTarget? _armRight;
    private SliderEnergyMomentumSpinner? _fire;

    public bool TryBind() {
        var console = GameObject.Find(".Review Console Parent").transform;
        var buttons = new List<LookAtTarget>();
        
        for (var i = 0; i < console.childCount; ++i) {
            var child = console.GetChild(i);
            if (child.name.StartsWith(".Check Switch")) {
                buttons.Add(child.GetComponentInChildren<LookAtTarget>());
            }
        }

        if (buttons.Count != 5) {
            MelonLogger.Error("Can't bind trigger console.");
        }
        _taskCheck = buttons[0];
        _bulletCheck = buttons[1];
        _rotationCheck = buttons[2];
        _elevationCheck = buttons[3];
        _readyFire = buttons[4];
        _armLeft = GameObject.Find(".ArmingLeverParent Left")?.GetComponentInChildren<LookAtTarget>();
        _armRight = GameObject.Find(".ArmingLeverParent Right")?.GetComponentInChildren<LookAtTarget>();
        _fire = GameObject.Find(".Trigger Core")?.transform.FindChild(".Generator Spinner")
            ?.GetComponentInChildren<SliderEnergyMomentumSpinner>();

        if (_fire == null)
        {
            MelonLogger.Error("[FCS] TriggerConsole: Can't find fire spinner");
            return false;
        }
        return true;
    }

    public void Fire() {
        _fire?.AddEnergy(255);
    }

    public IEnumerator Arm(LeftRight leftRight) {
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;
        arm?.OnClickDown();
        yield return new WaitForSeconds(0.2f);
        arm?.OnClickUp();
        yield return new WaitForSeconds(1f);
    }

    /// <summary>齐射: 两炮保险同时解除 (两个旋钮并行按, 不许一先一后).</summary>
    public IEnumerator ArmBoth() {
        _armLeft?.OnClickDown();
        _armRight?.OnClickDown();
        yield return new WaitForSeconds(0.2f);
        _armLeft?.OnClickUp();
        _armRight?.OnClickUp();
        yield return new WaitForSeconds(1f);
    }
    
    public IEnumerator ConfirmTask() {
        yield return UiClick.WaitAndClick(_taskCheck);
    }

    public IEnumerator ConfirmBullet() {
        yield return UiClick.WaitAndClick(_bulletCheck);
    }

    public IEnumerator ConfirmRotation() {
        yield return UiClick.WaitAndClick(_rotationCheck);
    }

    public IEnumerator ConfirmElevation() {
        yield return UiClick.WaitAndClick(_elevationCheck);
    }

    public IEnumerator ReadyToFire() {
        yield return UiClick.WaitAndClick(_readyFire);
    }
}