using System;
using System.Collections.Generic;
using UnityEngine;

namespace HaptGlove
{
    /// <summary>
    /// Keeps the bundled Bluetooth plugin's device cache safe to search by name.
    /// </summary>
    /// <remarks>
    /// com.tony.bluetoothunityapi.BLEBluetoothHelper remembers the devices of the last scan in
    /// a static list, <c>sharedBleDevices</c>, and finds the glove in it with
    ///
    ///   scanned.getName().equals(deviceName)
    ///
    /// where <c>getName()</c> is Android's live name for that device: null for anything that
    /// advertises none, and able to turn null after the scan that listed it. One such device
    /// ahead of the glove in the list makes every name lookup throw NullPointerException:
    /// <c>setDeviceName()</c> (which searches on the way out), <c>isDevicePaired()</c>, and so
    /// the match at the end of a scan. On a Quest 3 that left
    /// the second connect of a session on "Searching..." for good, since the throw came out of
    /// <c>setDeviceName()</c> before the attempt watchdog was armed. Nameless devices are
    /// everywhere at an event, so which connect hits it is down to the order of the list.
    ///
    /// The jar is third-party and shipped prebuilt, so the fix is on our side: drop nameless
    /// entries before searching. They can never be the glove, which is matched by name, or by
    /// an address that is only used with the name cleared. JNI reads the private static fields
    /// whatever their Java access.
    ///
    /// The plugin writes the list only when a scan ends, replacing it with that scan's results
    /// on the Android UI thread, so this leaves it alone while a scan is running.
    /// </remarks>
    public static class PluginDeviceCache
    {
        private const string HelperClass = "com.tony.bluetoothunityapi.BLEBluetoothHelper";

        /// <summary>Removes cached devices that have no name. Never throws.</summary>
        public static void DropNamelessDevices()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaClass helper = new AndroidJavaClass(HelperClass))
                {
                    if (helper.GetStatic<bool>("isScanning"))
                    {
                        return;
                    }

                    using (AndroidJavaObject devices = helper.GetStatic<AndroidJavaObject>("sharedBleDevices"))
                    using (AndroidJavaObject it = devices.Call<AndroidJavaObject>("iterator"))
                    {
                        int dropped = 0;
                        while (it.Call<bool>("hasNext"))
                        {
                            using (AndroidJavaObject device = it.Call<AndroidJavaObject>("next"))
                            {
                                if (string.IsNullOrEmpty(device.Call<string>("getName")))
                                {
                                    it.Call("remove");
                                    dropped++;
                                }
                            }
                        }

                        if (dropped > 0)
                        {
                            Debug.Log("[HexR-BLE] dropped " + dropped + " nameless device(s) from the plugin's cache");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HaptGlove] Could not tidy the Bluetooth plugin's device cache (" + e.Message
                    + "). A nameless device in it can make the next name lookup throw.");
            }
#endif
        }

        /// <summary>
        /// Whether the plugin's <c>scanNearbyDevices()</c> would show Android's "choose a device to
        /// be managed by ..." chooser instead of scanning, on Android 12 or newer, where a real
        /// scan works anyway. Never throws.
        /// </summary>
        /// <remarks>
        /// The plugin only scans when location permission is granted and the GPS or network
        /// location provider is on. Otherwise it opens the Companion Device Manager chooser, which
        /// on every scan asks the wearer to let the app manage some nearby device (often not a
        /// glove). That gate dates from Android 6-11, when a BLE scan needed location. From
        /// Android 12 a scan needs BLUETOOTH_SCAN, which HexRManager asks for, and not location,
        /// which nothing asks for there. So on PICO 4 Ultra (Android 14) the gate always failed and
        /// every connect, retry and nearby search put the chooser up. Below Android 12 the chooser
        /// stays: with location off, a scan there would find nothing.
        /// </remarks>
        public static bool ScanWouldOpenChooserNeedlessly()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                int sdk;
                using (AndroidJavaClass version = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    sdk = version.GetStatic<int>("SDK_INT");
                }
                if (sdk < 31)
                {
                    return false;
                }

                using (AndroidJavaClass player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    // Without BLUETOOTH_SCAN a scan finds nothing either way; leave the plugin be.
                    if (activity.Call<int>("checkSelfPermission", "android.permission.BLUETOOTH_SCAN") != 0)
                    {
                        return false;
                    }

                    // The plugin's own test, in its order.
                    bool permitted = activity.Call<int>("checkSelfPermission", "android.permission.ACCESS_FINE_LOCATION") == 0
                        && activity.Call<int>("checkSelfPermission", "android.permission.ACCESS_COARSE_LOCATION") == 0;
                    if (!permitted)
                    {
                        return true;
                    }

                    using (AndroidJavaObject location = activity.Call<AndroidJavaObject>("getSystemService", "location"))
                    {
                        return !ProviderOn(location, "gps") && !ProviderOn(location, "network");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HexR-BLE] Could not tell whether the Bluetooth plugin would open the device chooser ("
                    + e.Message + "). Leaving the scan to the plugin.");
                return false;
            }
#else
            return false;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool ProviderOn(AndroidJavaObject location, string provider)
        {
            try
            {
                return location != null && location.Call<bool>("isProviderEnabled", provider);
            }
            catch (Exception)
            {
                return false;
            }
        }
#endif

        /// <summary>
        /// Runs the plugin's own 8 s BLE scan for the helper with this id, skipping the location
        /// gate in front of it (see <see cref="ScanWouldOpenChooserNeedlessly"/>): what
        /// <c>scanNearbyDevices()</c> does once that gate passes, with the plugin's own scan callback
        /// and end-of-scan runnable, so the results, the device cache and OnScanEnded come back
        /// exactly as from a plugin scan. Call on the Android UI thread, like the plugin's scan.
        /// Returns true when the scan started, false when a scan is already running (as the plugin
        /// does), and null when the helper could not be driven, so the caller can fall back to the
        /// plugin. Throws when Bluetooth is off, as the plugin does.
        /// </summary>
        public static bool? StartScanWithoutChooser(int helperId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (AndroidJavaClass helperClass = new AndroidJavaClass(HelperClass))
            {
                if (helperClass.GetStatic<bool>("isScanning"))
                {
                    return false;
                }

                AndroidJavaObject helper;
                try
                {
                    helper = helperClass.CallStatic<AndroidJavaObject>("GetInstanceById", helperId);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[HexR-BLE] Could not find the plugin's helper " + helperId + ": " + e.Message);
                    return null;
                }
                if (helper == null)
                {
                    return null;
                }

                using (helper)
                using (AndroidJavaClass adapterClass = new AndroidJavaClass("android.bluetooth.BluetoothAdapter"))
                using (AndroidJavaObject adapter = adapterClass.CallStatic<AndroidJavaObject>("getDefaultAdapter"))
                {
                    if (adapter == null || !adapter.Call<bool>("isEnabled"))
                    {
                        throw new InvalidOperationException("Bluetooth is off on this headset.");
                    }

                    AndroidJavaObject callback, endOfScan, found;
                    try
                    {
                        callback = helper.Get<AndroidJavaObject>("scanCallback");
                        endOfScan = helper.Get<AndroidJavaObject>("scanningRunnable");
                        found = helper.Get<AndroidJavaObject>("bleDevices");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[HexR-BLE] Could not reach the plugin's scan internals: " + e.Message);
                        return null;
                    }

                    using (callback)
                    using (endOfScan)
                    using (found)
                    {
                        if (callback == null || endOfScan == null || found == null)
                        {
                            return null;
                        }

                        helperClass.SetStatic("isScanning", true);
                        try
                        {
                            found.Call("clear");
                            using (AndroidJavaObject scanner = adapter.Call<AndroidJavaObject>("getBluetoothLeScanner"))
                            {
                                scanner.Call("startScan", callback);
                            }
                            using (AndroidJavaClass looperClass = new AndroidJavaClass("android.os.Looper"))
                            using (AndroidJavaObject mainLooper = looperClass.CallStatic<AndroidJavaObject>("getMainLooper"))
                            using (AndroidJavaObject handler = new AndroidJavaObject("android.os.Handler", mainLooper))
                            {
                                handler.Call<bool>("postDelayed", endOfScan, 8000L);
                            }
                        }
                        catch (Exception)
                        {
                            helperClass.SetStatic("isScanning", false);
                            throw;
                        }
                    }
                }
            }
            Debug.Log("[HexR-BLE] scanning without the plugin's device chooser (Android 12+, location not needed)");
            return true;
#else
            return null;
#endif
        }

        /// <summary>
        /// Signal strength (dBm) by address for the devices of the last scan, read from the
        /// plugin's cache, whose entries carry the RSSI the scan heard them at. Empty off Android,
        /// or if the plugin's cache holds no strengths. Never throws.
        /// </summary>
        public static Dictionary<string, int> ReadSignalStrengths()
        {
            var strengths = new Dictionary<string, int>();
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaClass helper = new AndroidJavaClass(HelperClass))
                using (AndroidJavaObject devices = helper.GetStatic<AndroidJavaObject>("sharedBleDevices"))
                using (AndroidJavaObject it = devices.Call<AndroidJavaObject>("iterator"))
                {
                    while (it.Call<bool>("hasNext"))
                    {
                        using (AndroidJavaObject device = it.Call<AndroidJavaObject>("next"))
                        {
                            try
                            {
                                string address = device.Call<string>("getAddress");
                                if (!string.IsNullOrEmpty(address))
                                {
                                    strengths[address] = device.Call<int>("getRssi");
                                }
                            }
                            catch (Exception)
                            {
                                // An entry without a strength: left out, read as unknown.
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log("[HexR-BLE] Could not read signal strengths from the plugin's cache: " + e.Message);
            }
#endif
            return strengths;
        }
    }
}
