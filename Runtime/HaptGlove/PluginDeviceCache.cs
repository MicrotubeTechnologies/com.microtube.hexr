using System;
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
    }
}
