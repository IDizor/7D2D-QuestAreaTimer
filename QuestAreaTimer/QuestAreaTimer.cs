using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using System.Timers;
using UnityEngine;
using static Quest;

/// <summary>
/// When player leaves the quest area - creates a return-timer instead of an instant quest failure.
/// </summary>
public class QuestAreaTimer : IModApi
{
    private static float PoiTimeout = 10.05f;
    private static float PoiTimeoutHot = 5f;
    private static float BurriedSuppliesTimeout = 10.05f;
    private static float BurriedSuppliesTimeoutHot = 5f;
    private static float PoiOutZoneMultiplier = 0.33f;

    private static BaseObjective Objective = null;
    private static float Timout = PoiTimeout;
    private static float TimoutHot = PoiTimeoutHot;
    private static float? LeaveTime = null;
    private static Timer RefreshTimer = null;
    private static object UpdateUILock = new();
    
    /// <summary>
    /// Mod initialization.
    /// </summary>
    /// <param name="_modInstance"></param>
    public void InitMod(Mod _modInstance)
    {
        Debug.Log("Loading mod: " + GetType().ToString());
        LoadSettings();
        var harmony = new Harmony(GetType().ToString());
        harmony.PatchAll(Assembly.GetExecutingAssembly());
    }

    /// <summary>
    /// Loads the settings for the mod.
    /// </summary>
    private static void LoadSettings()
    {
        var settingsPath = Path.GetDirectoryName(Assembly.GetAssembly(typeof(QuestAreaTimer)).Location) + "\\settings.txt";
        if (File.Exists(settingsPath))
        {
            var settings = File.ReadAllLines(settingsPath);
            foreach (var line in settings)
            {
                var parts = line.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && float.TryParse(parts[1].Trim(), out float value))
                {
                    var name = parts[0].Trim();
                    switch (name)
                    {
                        case (nameof(PoiTimeout)):
                            PoiTimeout = value;
                            break;
                        case (nameof(PoiTimeoutHot)):
                            PoiTimeoutHot = value;
                            break;
                        case (nameof(BurriedSuppliesTimeout)):
                            BurriedSuppliesTimeout = value;
                            break;
                        case (nameof(BurriedSuppliesTimeoutHot)):
                            BurriedSuppliesTimeoutHot = value;
                            break;
                        case (nameof(PoiOutZoneMultiplier)):
                            PoiOutZoneMultiplier = value;
                            break;
                    }
                }
            }
        }
    }
    
    [HarmonyPatch(typeof(ObjectivePOIStayWithin))]
    [HarmonyPatch(nameof(ObjectivePOIStayWithin.UpdateState_Update))]
    public static class ObjectivePOIStayWithin_UpdateState_Update
    {
        /// <summary>
        /// Keeps current objective in a static variable. It might be used in the <see cref="Quest.MarkFailed"/> method prefix.
        /// </summary>
        public static bool Prefix(ObjectivePOIStayWithin __instance)
        {
            Objective = __instance;
            //Debug.LogWarning($"ObjectivePOIStayWithin.UpdateState_Update : {Objective.statusText} [{Objective.ObjectiveState}]");
            if (Objective.ObjectiveState != BaseObjective.ObjectiveStates.Failed)
            {
                ClearLeaveTime(__instance.OwnerQuest.OwnerJournal?.OwnerPlayer);
            }
            return true;
        }

        /// <summary>
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
    /// Keeps current objective in a static variable. It might be used in the <see cref="Quest.MarkFailed"/> method prefix.
    /// </summary>
    [HarmonyPatch(typeof(ObjectiveStayWithin))]
    [HarmonyPatch(nameof(ObjectiveStayWithin.Update))]
    public static class ObjectiveStayWithin_Update
    {
        public static bool Prefix(ObjectiveStayWithin __instance)
        {
            Objective = __instance;
            //Debug.LogWarning($"ObjectiveStayWithin.Update : {Objective.statusText} [{Objective.ObjectiveState}]");
            if (Objective.ObjectiveState != BaseObjective.ObjectiveStates.Failed)
            {
                ClearLeaveTime(__instance.OwnerQuest.OwnerJournal?.OwnerPlayer);
            }
            return true;
        }
    }

    /// <summary>
    /// Shrinks allowed area outside the POI.
    /// </summary>
    [HarmonyPatch(typeof(ObjectivePOIStayWithin))]
    [HarmonyPatch(nameof(ObjectivePOIStayWithin.ParseProperties))]
    public static class ObjectivePOIStayWithin_ParseProperties
    {
        public static void Postfix(DynamicProperties properties, ref float ___offset)
        {
            if (properties.Values.ContainsKey(ObjectivePOIStayWithin.PropRadius))
            {
                ___offset *= PoiOutZoneMultiplier;
            }
        }
    }

    /// <summary>
    /// Creates a return-to-the-quest-area-timer instead of an instant quest failure.
    /// </summary>
    [HarmonyPatch(typeof(Quest))]
    [HarmonyPatch(nameof(Quest.CloseQuest))]
    public static class Quest_CloseQuest
    {
        public static bool Prefix(Quest __instance, QuestState finalState)
        {
            if (finalState == QuestState.Failed)
            {
                var stackFrame = new System.Diagnostics.StackFrame(3);
                var calledClass = stackFrame.GetMethod().DeclaringType.Name;
                //Debug.LogWarning($"Quest.CloseQuest : {calledClass} [{Objective.ObjectiveState}]");
                if (calledClass == nameof(ObjectivePOIStayWithin) || calledClass == nameof(ObjectiveStayWithin))
                {
                    var isCorrectState = Objective != null
                        && __instance.CurrentState == QuestState.InProgress
                        && __instance.OwnerJournal?.OwnerPlayer?.IsDead() == false;

                    if (isCorrectState && LeaveTime == null && Objective.ObjectiveState == BaseObjective.ObjectiveStates.Failed)
                    {
                        var isPoiObjective = calledClass == nameof(ObjectivePOIStayWithin);
                        Timout = isPoiObjective ? PoiTimeout : BurriedSuppliesTimeout;
                        TimoutHot = isPoiObjective ? PoiTimeoutHot : BurriedSuppliesTimeoutHot;
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
            }

            return true;
        }
    }

    /// <summary>
    /// Binding values for UI.
    /// </summary>
    [HarmonyPatch(typeof(XUiC_QuestTrackerWindow))]
    [HarmonyPatch(nameof(XUiC_QuestTrackerWindow.GetBindingValue))]
    public static class XUiC_QuestTrackerWindow_GetBindingValue
    {
        private const string DefaultColor = "255,255,0";
        private const string HotColor = "255,30,30";
        private static string TimeColor = DefaultColor;
        private static float TimeLeft = 0;

        public static bool Prefix(ref string value, string bindingName, ref bool __result)
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

                        if (hadTime && TimeLeft == 0 && Objective != null && Objective is ObjectivePOIStayWithin objectiveStayWithin)
                        {
                            objectiveStayWithin.UpdateState_Update();
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
