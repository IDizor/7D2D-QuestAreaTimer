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

    private static float Timout = PoiTimeout;
    private static float TimoutHot = PoiTimeoutHot;
    private static float? LeaveTime = null;
    private static Timer RefreshTimer = null;
    private static object UpdateUILock = new object();
    private static MethodInfo ObjectivePOIStayWithin_GetPosition = typeof(ObjectivePOIStayWithin).GetMethod("GetPosition", BindingFlags.NonPublic | BindingFlags.Instance);

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
        /// The new method to execute instead of the original method <see cref="ObjectivePOIStayWithin.UpdateState_Update"/>.
        /// The same code as in the original method, but creates a return-to-the-POI-timer instead of an instant quest failure.
        /// </summary>
        public static bool Prefix(ObjectivePOIStayWithin __instance, ref bool ___positionSet, ref Rect ___outerRect, ref Rect ___innerRect)
        {
            if (!___positionSet)
            {
                ObjectivePOIStayWithin_GetPosition.Invoke(__instance, null);
                ClearLeaveTime(__instance.OwnerQuest.OwnerJournal.OwnerPlayer);
                return false;
            }
            Vector3 vector = __instance.OwnerQuest.OwnerJournal.OwnerPlayer.position;
            vector.y = vector.z;
            if (!___outerRect.Contains(vector))
            {
                var isCorrectState = __instance.OwnerQuest.CurrentState == Quest.QuestState.InProgress
                    && !__instance.OwnerQuest.OwnerJournal.OwnerPlayer.IsDead();
                if (LeaveTime == null && isCorrectState)
                {
                    Timout = PoiTimeout;
                    TimoutHot = PoiTimeoutHot;
                    SetLeaveTime();
                    CreateRefreshTimer(90, () => {
                        if (LeaveTime.HasValue)
                        {
                            UpdateUI(__instance?.OwnerQuest?.OwnerJournal?.OwnerPlayer);
                        }
                        else
                        {
                            RefreshTimer?.Stop();
                        }
                    });
                }
                else
                {
                    if (!isCorrectState || Time.time - LeaveTime.Value > Timout)
                    {
                        __instance.Complete = false;
                        __instance.ObjectiveState = BaseObjective.ObjectiveStates.Failed;
                        ClearLeaveTime(__instance.OwnerQuest.OwnerJournal.OwnerPlayer);
                        __instance.OwnerQuest.MarkFailed();
                    }
                }
                return false;
            }
            if (___innerRect.Contains(vector))
            {
                __instance.ObjectiveState = BaseObjective.ObjectiveStates.Complete;
                ClearLeaveTime(__instance.OwnerQuest.OwnerJournal.OwnerPlayer);
                return false;
            }
            __instance.ObjectiveState = BaseObjective.ObjectiveStates.Warning;
            ClearLeaveTime(__instance.OwnerQuest.OwnerJournal.OwnerPlayer);
            return false;
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
    /// The Harmony patch for the method <see cref="ObjectiveStayWithin.Update"/>.
    /// </summary>
    [HarmonyPatch(typeof(ObjectiveStayWithin))]
    [HarmonyPatch("Update")]
    public class ObjectiveStayWithin_Update
    {
        /// <summary>
        /// The new method to execute instead of the original method <see cref="ObjectiveStayWithin.Update"/>.
        /// The same code as in the original method, but creates a return-to-the-area-timer instead of an instant quest failure.
        /// </summary>
        public static bool Prefix(ObjectiveStayWithin __instance, ref bool ___positionSetup, ref float ___maxDistance, ref float ___currentDistance)
        {
            Vector3 position = __instance.OwnerQuest.OwnerJournal.OwnerPlayer.position;
            Vector3 position2 = __instance.OwnerQuest.Position;
            if (!___positionSetup)
            {
                if (__instance.OwnerQuest.GetPositionData(out position2, Quest.PositionDataTypes.Location))
                {
                    __instance.OwnerQuest.Position = position2;
                    QuestEventManager.Current.QuestBounds = new Rect(position2.x, position2.z, ___maxDistance, ___maxDistance);
                    ___positionSetup = true;
                }
                else if (__instance.OwnerQuest.GetPositionData(out position2, Quest.PositionDataTypes.POIPosition))
                {
                    __instance.OwnerQuest.Position = position2;
                    QuestEventManager.Current.QuestBounds = new Rect(position2.x, position2.z, ___maxDistance, ___maxDistance);
                    ___positionSetup = true;
                }
            }
            position.y = 0f;
            position2.y = 0f;
            ___currentDistance = (position - position2).magnitude;
            float num = ___currentDistance / ___maxDistance;
            if (num > 1f)
            {
                var isCorrectState = __instance.OwnerQuest.CurrentState == Quest.QuestState.InProgress
                    && !__instance.OwnerQuest.OwnerJournal.OwnerPlayer.IsDead();
                if (LeaveTime == null && isCorrectState)
                {
                    Timout = NoPoiTimeout;
                    TimoutHot = NoPoiTimeoutHot;
                    SetLeaveTime();
                    CreateRefreshTimer(90, () => {
                        if (LeaveTime.HasValue)
                        {
                            UpdateUI(__instance?.OwnerQuest?.OwnerJournal?.OwnerPlayer);
                        }
                        else
                        {
                            RefreshTimer?.Stop();
                        }
                    });
                }
                else
                {
                    if (!isCorrectState || Time.time - LeaveTime.Value > Timout)
                    {
                        __instance.Complete = false;
                        __instance.ObjectiveState = BaseObjective.ObjectiveStates.Failed;
                        ClearLeaveTime(__instance.OwnerQuest.OwnerJournal.OwnerPlayer);
                        __instance.OwnerQuest.MarkFailed();
                    }
                }

                return false;
            }
            if (num > 0.75f)
            {
                __instance.ObjectiveState = BaseObjective.ObjectiveStates.Warning;
                ClearLeaveTime(__instance.OwnerQuest.OwnerJournal.OwnerPlayer);
                return false;
            }
            __instance.ObjectiveState = BaseObjective.ObjectiveStates.Complete;
            ClearLeaveTime(__instance.OwnerQuest.OwnerJournal.OwnerPlayer);
            return false;
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

        /// <summary>
        /// The additional code to execute before the original method <see cref="XUiC_QuestTrackerWindow.GetBindingValue"/>.
        /// Populates binding values for UI.
        /// </summary>
        public static bool Prefix(ref string value, string bindingName, ref bool __result, XUiC_QuestTrackerObjectiveList ___objectiveList)
        {
            if (bindingName != null)
            {
                if (bindingName == "staywithinwarning")
                {
                    value = LeaveTime.HasValue.ToString();
                    __result = true;
                    return false;
                }
                else if (bindingName == "staywithintimeleft")
                {
                    value = "";

                    if (LeaveTime.HasValue)
                    {
                        var timeLeft = Timout - (Time.time - LeaveTime.Value);
                        timeLeft = Math.Max(timeLeft, 0);
                        TimeColor = timeLeft > TimoutHot ? DefaultColor : HotColor;
                        value = timeLeft.ToString("0.0");
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
