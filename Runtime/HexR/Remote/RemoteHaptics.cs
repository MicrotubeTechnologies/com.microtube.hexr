using System;
using System.Collections.Generic;
using HaptGlove;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// Tells phones what each glove's haptics are doing as it happens, so a viewer can draw the
    /// glove over the headset view and light up where the wearer is being touched.
    ///
    /// It reads the last command per channel that HaptGlove.Haptics already keeps
    /// (GetChannelState, GetActivationCount) once a frame, so it sees every command from every
    /// script with no hook in the send path. These are commands, not measurements: the status's
    /// pressureDeciKPa is what the glove measures. A channel switched on and off inside one frame
    /// is sent as a tap, which polling the state alone would never see.
    /// </summary>
    internal sealed class RemoteHaptics
    {
        private const int Fingers = 6;

        private readonly Func<long> clock;
        private readonly Haptics.ChannelState[,] last = new Haptics.ChannelState[2, Fingers];
        private readonly int[,] count = new int[2, Fingers];
        private readonly HaptGloveHandler[] watched = new HaptGloveHandler[2];
        private readonly List<RemoteHapticChannel> changed = new List<RemoteHapticChannel>(Fingers * 2);

        public RemoteHaptics(Func<long> clock = null)
        {
            this.clock = clock ?? RemoteClock.NowUs;
        }

        /// <summary>
        /// Main thread, once a frame: a line for each hand whose channels changed since the last call.
        /// </summary>
        public void Poll(HexRManager rig, List<string> lines)
        {
            long now = -1;
            for (int h = 0; h < 2; h++)
            {
                HaptGloveHandler handler = rig != null ? (h == 0 ? rig.leftHand : rig.rightHand) : null;
                Haptics haptics = handler != null ? handler.haptics : null;
                changed.Clear();

                if (!ReferenceEquals(handler, watched[h]))
                {
                    // A new rig after a scene load, or the glove handler gone: whatever the old one
                    // was doing has stopped, and its activation counts mean nothing for the new one.
                    for (int f = 0; f < Fingers; f++)
                    {
                        if (last[h, f].Mode != Haptics.HapticMode.Off)
                        {
                            changed.Add(Channel(f, default, false));
                        }

                        last[h, f] = default;
                        count[h, f] = haptics != null ? haptics.GetActivationCount((Haptics.Finger)f) : 0;
                    }

                    watched[h] = handler;
                }

                if (haptics != null)
                {
                    for (int f = 0; f < Fingers; f++)
                    {
                        Haptics.Finger finger = (Haptics.Finger)f;
                        Haptics.ChannelState state = haptics.GetChannelState(finger);
                        int n = haptics.GetActivationCount(finger);
                        if (n != count[h, f])
                        {
                            // Switched on, or a new value while on. Off again already means it was a
                            // tap that came and went within this frame: send what it was.
                            changed.Add(state.Mode != Haptics.HapticMode.Off
                                ? Channel(f, state, false)
                                : Channel(f, haptics.GetLastActiveState(finger), true));
                        }
                        else if (!Same(state, last[h, f]))
                        {
                            changed.Add(Channel(f, state, false));
                        }

                        last[h, f] = state;
                        count[h, f] = n;
                    }
                }

                if (changed.Count > 0)
                {
                    if (now < 0)
                    {
                        now = clock();
                    }

                    lines.Add(Line(h, now, changed));
                }
            }
        }

        /// <summary>Every channel of both hands as last polled: what a phone that connects now is sent first.</summary>
        public string[] Snapshot()
        {
            long now = clock();
            string[] lines = new string[2];
            for (int h = 0; h < 2; h++)
            {
                changed.Clear();
                for (int f = 0; f < Fingers; f++)
                {
                    changed.Add(Channel(f, last[h, f], false));
                }

                lines[h] = Line(h, now, changed);
            }

            return lines;
        }

        private static string Line(int hand, long t, List<RemoteHapticChannel> channels)
        {
            return RemoteProtocol.ToLine(new RemoteHapticsMessage
            {
                t = t,
                hand = hand == 0 ? RemoteProtocol.Left : RemoteProtocol.Right,
                channels = channels.ToArray(),
            });
        }

        private static RemoteHapticChannel Channel(int finger, Haptics.ChannelState state, bool tap)
        {
            if (state.Mode == Haptics.HapticMode.Off)
            {
                return new RemoteHapticChannel { finger = finger, mode = RemoteProtocol.HapticOff };
            }

            // Intensity is 0.1-1 from every current command; the deprecated raw-pressure command
            // stores kPa, which maps onto the same 15-50 kPa range the others drive.
            float intensity = state.Intensity > 1f ? 0.1f + 0.9f * Mathf.InverseLerp(15f, 50f, state.Intensity) : state.Intensity;
            return new RemoteHapticChannel
            {
                finger = finger,
                mode = state.Mode == Haptics.HapticMode.Pressure ? RemoteProtocol.HapticPressure
                    : state.Mode == Haptics.HapticMode.Pulse ? RemoteProtocol.HapticPulse
                    : RemoteProtocol.HapticVibration,
                intensityPct = Mathf.Clamp(Mathf.RoundToInt(intensity * 100f), 0, 100),
                frequencyDeciHz = Mathf.Max(0, Mathf.RoundToInt(state.Frequency * 10f)),
                tap = tap,
            };
        }

        private static bool Same(Haptics.ChannelState a, Haptics.ChannelState b)
        {
            return a.Mode == b.Mode && Mathf.Approximately(a.Intensity, b.Intensity) && Mathf.Approximately(a.Frequency, b.Frequency);
        }
    }

    /// <summary>
    /// The clock haptics and the status are stamped with: the same one as the video's presentation
    /// times, so a viewer can put each touch on the frame that shows it. On Android the captured
    /// frames are stamped with CLOCK_MONOTONIC, which Java reads as System.nanoTime. Elsewhere there
    /// is no video, and any steady clock will do.
    /// </summary>
    internal static class RemoteClock
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaClass javaSystem;
#endif

        /// <summary>Microseconds. Main thread only on Android, where it goes through JNI.</summary>
        public static long NowUs()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (javaSystem == null)
            {
                javaSystem = new AndroidJavaClass("java.lang.System");
            }

            return javaSystem.CallStatic<long>("nanoTime") / 1000L;
#else
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000000.0 / System.Diagnostics.Stopwatch.Frequency));
#endif
        }
    }
}
