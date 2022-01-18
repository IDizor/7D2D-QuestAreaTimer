using HarmonyLib;
using System;
using System.Reflection;
using System.Timers;
using UnityEngine;

/// <summary>
/// When player leaves the quest area - creates a return-timer instead of an instant quest failure.
/// </summary>
public class QuestAreaTimer : IModApi
{
    private const float PoiTimeout = 30.1f;
    private const float PoiTimeoutHot = 10f;
    private const float NoPoiTimeout = 10.1f;
    private const float NoPoiTimeoutHot = 5f;

    private static BaseObjective Objective = null;
    private static float Timout = PoiTimeout;
    private static float TimoutHot = PoiTimeoutHot;
    private static float? LeaveTime = null;
    private static Timer RefreshTimer = null;
    private static object UpdateUILock = new object();
    
    /// <summary>
    /// Mod initialization.
    /// </summary>
    /// <param name="_modInstance"></param>
    public void InitMod(Mod _modInstance)
    {
        Debug.Log("Loading mod: " + GetType().ToString());
        var harmony = new Harmony(GetType().ToString());
        harmony.PatchAll(Assembly.GetExecutingAssembly());
    }
    
    /// <summary>
    /// The Harmony patch for the method <see cref="ObjectivePOIStayWithin.UpdateState_Update"/>.
    /// </summary>
    [HarmonyPatch(typeof(ObjectivePOIStayWithin))]
    [HarmonyPatch("UpdateState_Update")]
    public class ObjectivePOIStayWithin_UpdateState_Update
    {
        /// <summary>
        /// The method to execute before the original method <see cref="ObjectivePOIStayWithin.UpdateState_Update"/>.
        /// Keeps current objective in a static variable. It might be used in the <see cref="Quest.MarkFailed"/> method prefix.
        /// </summary>
        public static bool Prefix(ObjectivePOIStayWithin __instance)
        {
            Objective = __instance;
            if (Objective.ObjectiveState != BaseObjective.ObjectiveStates.Failed)
            {
                ClearLeaveTime(__instance.OwnerQuest.OwnerJournal?.OwnerPlayer);
            }
            return true;
        }

        /// <summary>
        /// The new method to execute after the original method <see cref="ObjectivePOIStayWithin.UpdateState_Update"/>.
        /// Needed to hide timer faster, when player returned back to the POI.
        /// </summary>
        public static void Postfix(ObjectivePOIStayWithin __instance)
        {
            if (__instance.ObjectiveState != BaseObjective.ObjectiveStates.Failed)
            {
                ClearLeaveTime(__instance.OwnerQuest.OwnerJournal?.OwnerPlayer);
            }
        }
    }

    /// <summary>
    /// The Harmony patch for the method <see cref="ObjectiveStayWithin.Update"/>.
    /// </summary>
    [HarmonyPatch(typeof(ObjectiveStayWithin))]
    [HarmonyPatch("Update")]
    public class ObjectiveStayWithin_Update
    {
        /// <summary>
        /// The method to execute before the original method <see cref="ObjectiveStayWithin.Update"/>.
        /// Keeps current objective in a static variable. It might be used in the <see cref="Quest.MarkFailed"/> method prefix.
        /// </summary>
        public static bool Prefix(ObjectiveStayWithin __instance)
        {
            Objective = __instance;
            if (Objective.ObjectiveState != BaseObjective.ObjectiveStates.Failed)
            {
                ClearLeaveTime(__instance.OwnerQuest.OwnerJournal?.OwnerPlayer);
            }
            return true;
        }
    }

    /// <summary>
    /// The Harmony patch for the method <see cref="ObjectivePOIStayWithin.ParseProperties"/>.
    /// </summary>
    [HarmonyPatch(typeof(ObjectivePOIStayWithin))]
    [HarmonyPatch("ParseProperties")]
    public class ObjectivePOIStayWithin_ParseProperties
    {
        /// <summary>
        /// The additional code to execute after the original method <see cref="ObjectivePOIStayWithin.ParseProperties"/>.
        /// Shrinks allowed area outside the POI.
        /// </summary>
        public static void Postfix(DynamicProperties properties, ObjectivePOIStayWithin __instance, ref float ___offset)
        {
            if (properties.Values.ContainsKey(ObjectivePOIStayWithin.PropRadius))
            {
                ___offset /= 4;
            }
        }
    }

    /// <summary>
    /// The Harmony patch for the method <see cref="Quest.MarkFailed"/>.
    /// </summary>
    [HarmonyPatch(typeof(Quest))]
    [HarmonyPatch("MarkFailed")]
    public class Quest_MarkFailed
    {
        /// <summary>
        /// The method to execute before of the original method <see cref="Quest.MarkFailed"/>.
        /// Creates a return-to-the-quest-area-timer instead of an instant quest failure.
        /// </summary>
        public static bool Prefix(Quest __instance)
        {
            var stackFrame = new System.Diagnostics.StackFrame(2);
            var calledClass = stackFrame.GetMethod().DeclaringType.Name;
            if (calledClass == nameof(ObjectivePOIStayWithin) || calledClass == nameof(ObjectiveStayWithin))
            {
                var isCorrectState = Objective != null
                    && __instance.CurrentState == Quest.QuestState.InProgress
                    && __instance.OwnerJournal?.OwnerPlayer?.IsDead() == false;

                if (isCorrectState && LeaveTime == null && Objective.ObjectiveState == BaseObjective.ObjectiveStates.Failed)
                {
                    var isPoiObjective = calledClass == nameof(ObjectivePOIStayWithin);
                    Timout = isPoiObjective ? PoiTimeout : NoPoiTimeout;
                    TimoutHot = isPoiObjective ? PoiTimeoutHot : NoPoiTimeoutHot;
                    SetLeaveTime();
                    CreateRefreshTimer(90, () =>
                    {
                        if (LeaveTime == null)
                        {
                            RefreshTimer?.Stop();
                        }
                        UpdateUI(__instance.OwnerJournal?.OwnerPlayer);
                    });

                    return false;
                }
                if (LeaveTime.HasValue)
                {
                    if (!isCorrectState || Time.time - LeaveTime.Value > Timout)
                    {
                        ClearLeaveTime(__instance.OwnerJournal?.OwnerPlayer);
                        Objective = null;
                        return true;
                    }

                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The Harmony patch for the method <see cref="XUiC_QuestTrackerWindow.GetBindingValue"/>.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_QuestTrackerWindow))]
    [HarmonyPatch("GetBindingValue")]
    public class XUiC_QuestTrackerWindow_GetBindingValue
    {
        private const string DefaultColor = "255,255,0";
        private const string HotColor = "255,30,30";
        private static string TimeColor = DefaultColor;
        private static float TimeLeft = 0;

        /// <summary>
        /// The additional code to execute before the original method <see cref="XUiC_QuestTrackerWindow.GetBindingValue"/>.
        /// Populates binding values for UI.
        /// </summary>
        public static bool Prefix(ref string value, string bindingName, /*XUiC_QuestTrackerWindow __instance,*/ ref bool __result)
        {
            if (bindingName != null)
            {
                if (bindingName == "staywithinwarning")
                {
                    value = (LeaveTime.HasValue && TimeLeft > 0).ToString();
                    __result = true;
                    return false;
                }
                else if (bindingName == "staywithintimeleft")
                {
                    value = "";

                    if (LeaveTime.HasValue)
                    {
                        var hadTime = TimeLeft > 0;
                        TimeLeft = Math.Max(Timout - (Time.time - LeaveTime.Value), 0);
                        TimeColor = TimeLeft > TimoutHot ? DefaultColor : HotColor;
                        value = TimeLeft.ToString("0.0");

                        if (hadTime && TimeLeft == 0 && Objective != null && Objective is ObjectivePOIStayWithin)
                        {
                            typeof(ObjectivePOIStayWithin).GetMethod("UpdateState_Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Objective, null);
                        }
                    }

                    __result = true;
                    return false;
                }
                else if (bindingName == "staywithintimecolor")
                {
                    if (LeaveTime.HasValue)
                    {
                        value = TimeColor;
                    }
                    else
                    {
                        value = DefaultColor;
                    }

                    __result = true;
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Forces UI to update binded values.
    /// </summary>
    /// <param name="player"></param>
    private static void UpdateUI(EntityPlayerLocal player)
    {
        lock (UpdateUILock)
        {
            if (!GameManager.Instance.IsPaused() && player?.QuestJournal != null)
            {
                player.QuestJournal.TrackedQuest = player.QuestJournal.TrackedQuest;
            }
        }
    }

    /// <summary>
    /// Puts current game time to the static variable.
    /// </summary>
    private static void SetLeaveTime()
    {
        LeaveTime = Time.time;
    }

    /// <summary>
    /// Clears leave time static variable.
    /// </summary>
    /// <param name="player"></param>
    private static void ClearLeaveTime(EntityPlayerLocal player)
    {
        if (LeaveTime.HasValue)
        {
            LeaveTime = null;
            KillRefreshTimer();
            UpdateUI(player);
        }
        else
        {
            KillRefreshTimer();
        }
    }

    /// <summary>
    /// Creates a new timer to refresh UI.
    /// </summary>
    /// <param name="interval"></param>
    /// <param name="onTimer"></param>
    private static void CreateRefreshTimer(int interval, Action onTimer)
    {
        KillRefreshTimer();
        RefreshTimer = new Timer { Interval = interval, AutoReset = true };
        RefreshTimer.Elapsed += (sender, e) => { onTimer(); };
        RefreshTimer.Start();
    }

    /// <summary>
    /// Stops and disposes the refresh UI timer.
    /// </summary>
    private static void KillRefreshTimer()
    {
        if (RefreshTimer != null)
        {
            RefreshTimer.Stop();
            RefreshTimer.Dispose();
            RefreshTimer = null;
        }
    }
}
