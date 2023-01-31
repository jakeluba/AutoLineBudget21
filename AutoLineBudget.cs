using ICities;
using ColossalFramework;
using ColossalFramework.Plugins;
using System;
using UnityEngine;
using System.Diagnostics;
using System.Collections.Generic;

namespace AutoLineBudget
{
    public class AutoLineBudget : IUserMod
    {
        public string Name
        {
            get { return "Auto Line Budget 21"; }
        }

        public string Description
        {
            get { return "Automatically adjusts the number of vehicles to the number of passengers. " +
                    "\n\n New 2021 version with added anti-jam mechanism and fixed bugs. "; }
        }
    }

    public class Loader : LoadingExtensionBase
    {
        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);

            GameObject go = new GameObject("AutoLineBudget");
            go.AddComponent<MyBehaviour>();
        }

    }

    public class MyBehaviour : MonoBehaviour
    {
        private readonly Stopwatch _throttle = Stopwatch.StartNew();

        private float inertia = 0.5f;

        private Dictionary<int, Dictionary<int, float>> lineHourFlow;
        private int prevHour = -1;

        void Start()
        {
            lineHourFlow = new Dictionary<int, Dictionary<int, float>>();
        }

        void Update()
        {
            int updateFrequency = 5000; //milliseconds
            if (_throttle.Elapsed.TotalMilliseconds <= updateFrequency)
                return;
            _throttle.Reset();
            _throttle.Start();

            if (!Singleton<TransportManager>.exists)
                return;

            long Ticks = Singleton<SimulationManager>.instance.m_currentGameTime.Ticks;
            long Frames = Ticks / Singleton<SimulationManager>.instance.m_timePerFrame.Ticks;

            //Calendar time
            //TicksPerMillisecond     10000
            //TicksPerHour            36000000000
            //TicksPerDay             864000000000

            //Unmodded game
            //1 frame ==              1476562500 ticks
            //1 tick ==               6.7724867724867724867724867724868e-10 frames
            //DAYTIME_FRAME_TO_HOUR   0.0003662109

            double FramesPerGameHour = TimeSpan.TicksPerHour / (double)Singleton<SimulationManager>.instance.m_timePerFrame.Ticks;
            double FramesPerSunHour = 1 / 0.0003662109;
            int hour;
            if (FramesPerGameHour > FramesPerSunHour)
                hour = Singleton<SimulationManager>.instance.m_currentGameTime.Hour;
            else
                hour = (int)((Frames % (24 * FramesPerSunHour)) / FramesPerSunHour);
            if (hour == prevHour)
                return;
            prevHour = hour;

            var changeLog = new Dictionary<ushort, float>();
            for (ushort lnId = 1; lnId < 256; ++lnId)
            {
                if ((Singleton<TransportManager>.instance.m_lines.m_buffer[lnId].m_flags & (TransportLine.Flags.Created | TransportLine.Flags.Temporary)) == TransportLine.Flags.Created)
                {
                    TransportLine line = Singleton<TransportManager>.instance.m_lines.m_buffer[lnId];
                    //bool debug = false;
                    //if (Singleton<TransportManager>.instance.GetLineName(lnId) == "Bus Line 15")
                        //debug = true;
                    int lnBudget = Singleton<EconomyManager>.instance.GetBudget(line.Info.m_class);
                    if (line.m_lineNumber != 0)
                    {
                        if (!lineHourFlow.ContainsKey(lnId))
                            lineHourFlow.Add(lnId, new Dictionary<int, float>());
                        VehicleManager vehInstance = Singleton<VehicleManager>.instance;
                        int fullVehCount = 0;
                        float notFullVehOccupancy = 0;
                        float notFullVehCapacity = 0;
                        int vehCount = 0;
                        int lineCapacity = 0;
                        ushort vehId = line.m_vehicles;
                        while (vehId != 0)
                        {
                            VehicleInfo info = Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehId].Info;
                            string localeKey;
                            int current2, max2;
                            info.m_vehicleAI.GetBufferStatus(vehId, ref Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehId], out localeKey, out current2, out max2);
                            if (current2 == max2)
                            {
                                fullVehCount++;
                            }
                            else
                            {
                                /*
                                //Occupancy enhancer - an alternative method to count in passengers left at stops
                                float occFactor = 1;
                                float occShare = current2 / (float)max2;
                                if (occShare > 0.5)
                                {
                                    occFactor = (float)(4 * Math.Pow(occShare, 2) - 4 * occShare + 2);
                                }
                                //notFullVeh means now allVeh
                                */
                                notFullVehOccupancy += current2;// * occFactor;
                                notFullVehCapacity += max2;
                            }
                            lineCapacity += max2;
                            vehId = vehInstance.m_vehicles.m_buffer[vehId].m_nextLineVehicle;
                            vehCount++;
                        }
                        if (lineCapacity == 0)
                        {
                            //if (debug) DebugOutputPanel.AddMessage(PluginManager.MessageType.Message, "[AutoLineBudget] " + Singleton<TransportManager>.instance.GetLineName(lnId)
                                //+ " lineCapacity " + lineCapacity);
                            continue;
                        }
                        if (vehCount != line.CalculateTargetVehicleCount())
                        {
                            //if (debug) DebugOutputPanel.AddMessage(PluginManager.MessageType.Message, "[AutoLineBudget] " + Singleton<TransportManager>.instance.GetLineName(lnId)
                                //+ " vehCount " + vehCount + " targetVehicleCount " + line.CalculateTargetVehicleCount());
                            continue;
                        }

                        float notFullVehAvgOccupancy = 0f;
                        if (notFullVehCapacity != 0)
                            notFullVehAvgOccupancy = notFullVehOccupancy / notFullVehCapacity;
                        float fullVehAvgOccupancy = 2f;
                        if (fullVehCount > 0 && vehCount != fullVehCount)
                        {
                            float occSpread = (1f - notFullVehAvgOccupancy) * 2f / (vehCount - fullVehCount);
                            fullVehAvgOccupancy = 1f + occSpread * fullVehCount / 2f;
                        }
                        float avgCurrOccupancy = (notFullVehAvgOccupancy * (vehCount - fullVehCount) + fullVehAvgOccupancy * fullVehCount) / vehCount;
                        //float avgCurrOccupancy = notFullVehAvgOccupancy;
                        float lineFlow = lineCapacity * avgCurrOccupancy;

                        //low frequency line flow adjustment
                        float maxWaitingTime = 120f;
                        lineFlow *= Math.Max(1f, line.m_averageInterval / maxWaitingTime);

                        //anti-jam mechanism
                        float vehSpacing = line.m_totalLength / vehCount;
                        float lineSpeed = line.m_totalLength / (line.m_averageInterval * vehCount);
                        lineFlow *= Math.Min(1f, 0.025f * (float)Math.Exp(0.0006f * vehSpacing) * lineSpeed);
                        lineHourFlow[lnId][hour] = lineFlow;

                        float hoursNum = lineHourFlow[lnId].Count;
                        if (hoursNum > 24f)
                        {
                            DebugOutputPanel.AddMessage(PluginManager.MessageType.Message, "[AutoLineBudget] Error: more than 24 time periods");
                            lineHourFlow.Clear();
                            return;
                        }
                        float avgFlow = 0f;
                        foreach (KeyValuePair<int, float> kvp in lineHourFlow[lnId])
                        {
                            avgFlow += kvp.Value;
                        }
                        avgFlow /= hoursNum;
                        if (avgFlow == 0)
                        {
                            //if (debug) DebugOutputPanel.AddMessage(PluginManager.MessageType.Message, "[AutoLineBudget] " + Singleton<TransportManager>.instance.GetLineName(lnId)
                                //+ " hoursNum " + hoursNum + " avgFlow " + avgFlow);
                            continue;
                        }
                        float avgOccupancy = avgFlow / lineCapacity;
                        float newOccupancy = Math.Min(Math.Max(0.1f, 43.75f / lnBudget), 1f);

                        //correction for incomplete data
                        newOccupancy = (newOccupancy - avgOccupancy) * hoursNum / 24f + avgOccupancy;

                        float vehChange = avgOccupancy * vehCount / newOccupancy - vehCount;
                        if (Math.Abs(vehChange + inertia) < inertia)
                        {
                            //if (debug) DebugOutputPanel.AddMessage(PluginManager.MessageType.Message, "[AutoLineBudget] " + Singleton<TransportManager>.instance.GetLineName(lnId)
                                //+ " vehChange " + vehChange + " inertia " + inertia);
                            continue;
                        }
                        ushort newBudget = (ushort)Mathf.CeilToInt(line.m_budget / newOccupancy * avgOccupancy);
                        //if (debug) DebugOutputPanel.AddMessage(PluginManager.MessageType.Message, "[AutoLineBudget] " + Singleton<TransportManager>.instance.GetLineName(lnId)
                                //+ " trnBudget " + lnBudget + " oldOccupancy " + avgOccupancy + " newOccupancy " + newOccupancy + " oldBudget " + line.m_budget + " newBudget " + newBudget);

                        Singleton<TransportManager>.instance.m_lines.m_buffer[lnId].m_budget = newBudget;
                        changeLog.Add(lnId, vehChange / vehCount);
                    }
                } 
            }
            if (changeLog.Count != 0)
            {
                foreach (KeyValuePair<ushort, float> kvp in changeLog)
                {
                    if (kvp.Value < -0.25)
                    {
                        MessageInfo messageInfo = new MessageInfo();
                        string lineName = Singleton<TransportManager>.instance.GetLineName(kvp.Key);
                        messageInfo.m_firstID1 = lineName + " is more frequent now. So cooool ";
                        Singleton<MessageManager>.instance.TryCreateMessage(messageInfo, Singleton<MessageManager>.instance.GetRandomResidentID());
                    }
                    else if (kvp.Value > 0.25)
                    {
                        MessageInfo messageInfo = new MessageInfo();
                        string lineName = Singleton<TransportManager>.instance.GetLineName(kvp.Key);
                        messageInfo.m_firstID1 = lineName + " is not so empty anymore. What happened? ";
                        Singleton<MessageManager>.instance.TryCreateMessage(messageInfo, Singleton<MessageManager>.instance.GetRandomResidentID());
                    }

                }
            }
        }
    }
}
