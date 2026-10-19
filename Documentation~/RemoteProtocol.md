# HexR Remote protocol, version 1

How a phone (the HexR Tester's **Headsets** tab) finds a HexR app on a headset, connects and
disconnects its gloves, reads their state and watches the headset view, with the app's sound. The headset side is
`Runtime/HexR/Remote/` and `Runtime/Plugins/Android/HexRRemote/` in this package; the phone side
is `android/app/src/main/java/com/microtube/hexr/remote/` in HexR-Window-Tester (branch
`android`). `Tools~/hexr_remote_probe.py` speaks it from a computer.

Everything below is on the local network only. Nothing is encrypted or authenticated: anyone on
the same Wi-Fi with a HexR Remote client can control a headset that has HexR Remote, and watch it
once the wearer accepts the capture prompt. Bring a dedicated router to events.

The reference lines and bytes in this document are the ones both test suites use
(`Tests/Editor/RemoteTests.cs` here, `RemoteProtocolTest.kt` in the tester). Change the protocol
in both places.

## Ports

| Port | Transport | Direction | What |
|---|---|---|---|
| 47800 | UDP broadcast | headset → everyone | Announcement every 2 s |
| 47801 | TCP | phone ↔ headset | Control: JSON lines |
| 47802 | TCP | headset → phone | Video, while the view is being shared |
| 47803 | TCP | headset → phone | Sound, while the view is being shared with it (0.10.5 and later) |

The control, video and sound ports are defaults. If one is taken the headset uses the next free
one (up to +9, then any free port) and says so: the announcement carries the control port, and
the status carries the video and sound ports. A phone never assumes any of them.

## Discovery

The headset broadcasts this, as UTF-8 JSON, to `255.255.255.255:47800` every two seconds:

```json
{"hexr":1,"type":"announce","id":"27c6…","name":"Quest 3","model":"Oculus Quest 3","platform":"quest","app":"HexR Meta Developer Tutorial","appId":"com.Microtube.HexRMetaDeveloperTutorial","hexrVersion":"0.10.0","control":47801,"video":47802,"startedAt":1791966541289}
```

- The headset's address is the packet's source address; the announcement does not carry it.
- `id` is Unity's `SystemInfo.deviceUniqueIdentifier`. On Android it depends on the app's signing
  key, so the same headset can show a different id in apps signed on different machines.
- `platform` is `quest`, `pico`, `android`, `editor` or `desktop`.
- `startedAt` (Unix ms) tells apart two sessions on one headset: keep the newest.
- An Android phone has to hold a Wi-Fi multicast lock to receive broadcasts. Sending one needs
  nothing. iOS will need either Bonjour or Apple's multicast entitlement; neither exists yet.
- Venue Wi-Fi that isolates clients blocks this. The phone can still connect by IP.

## Control

TCP, UTF-8, one JSON object per line, `\n`-terminated (a trailing `\r` is ignored). A line over
16 KiB closes the connection. Requests from the phone are read into an object whose missing
fields mean "not given" (`index` defaults to -1).

### Phone → headset

| Request | Reference line | Effect |
|---|---|---|
| hello | `{"type":"hello","protocol":1,"client":"HexR Tester"}` | Headset replies with its welcome |
| ping | `{"type":"ping"}` | Nothing; keeps the connection alive. Send every ~5 s |
| glove | `{"type":"glove","action":"connect","hand":"left"}` | `HexRManager.Connect(Left)` |
| glove | `{"type":"glove","action":"disconnect","hand":"right"}` | `HexRManager.Disconnect(Right)` |
| release | `{"type":"release"}` | `ReleaseAllChannels()` on connected gloves; `"hand"` limits it to one |
| scene | `{"type":"scene","index":2}` | Load build scene 2 (only if the app exposes scenes) |
| command | `{"type":"command","id":"reset"}` | Run a command the app registered |
| stream | `{"type":"stream","action":"start"}` | Ask the wearer to share the view; `"stop"` ends sharing |

A request that does nothing (connect on a glove already connecting, disconnect on one already
off) is not an error. A request that cannot be done gets an error line. The headset drops a
phone it has not heard from in 15 s.

While the headset is paused (taken off, asleep) every request except hello and ping is refused
straight away: run later, a scene change or a capture prompt would land in front of whoever puts
the headset on next. A request that waited more than 5 s for the main thread is refused too.

### Headset → phone

Welcome, in reply to hello:

```json
{"type":"hello","protocol":1,"id":"q3-1","control":47801,"video":47802}
```

Error, in reply to a request it could not do:

```json
{"type":"error","request":"command","message":"This app has no command \"nope\"."}
```

Status, five times a second while the app runs and once a second while it is paused, so a phone
can tell a sleeping headset (`"paused":true`) from a vanished one (nothing for 4 s). Reference
line:

```json
{"type":"status","protocol":1,"id":"q3-1","name":"Quest 3","model":"Oculus Quest 3","platform":"quest","app":"HexR Demo","appVersion":"1.2","hexrVersion":"0.10.0","paused":false,"headsetBatteryPct":71,"scene":{"index":1,"name":"Hospital Tutorial"},"scenes":[{"index":0,"name":"Basic Tutorial"},{"index":1,"name":"Hospital Tutorial"}],"gloves":[{"hand":"left","state":"connected","message":"Connected","batteryPct":83,"pressureDeciKPa":[10,0,0,0,0,0,452]},{"hand":"right","state":"disconnected","message":"","batteryPct":-1,"pressureDeciKPa":[]}],"stream":{"state":"live","port":47802,"width":1024,"height":1024,"viewers":1,"message":"","audio":{"state":"live","port":47803,"sampleRate":48000,"channels":2,"message":""}},"commands":[{"id":"reset","label":"Reset demo"}],"allowGloveControl":true,"allowVideo":true,"exposeScenes":true,"controllers":2,"haptics":true,"clockUs":1791966541289123}
```

- `gloves` always has left then right. `state` is `connected`, `connecting`, `disconnected`, or
  `missing` (the rig has no handler for that hand). `message` is the handler's own status text,
  e.g. a Bluetooth permission prompt waiting in the headset.
- `batteryPct` is -1 until the glove has reported its battery.
- `pressureDeciKPa`: gauge pressure in tenths of a kPa, thumb, index, middle, ring, pinky, palm,
  then the pump's source tank (the firmware's channel order). Empty unless connected. The raw
  reading is converted the same way the HexR Tester converts it over Bluetooth
  (`Protocol.rawToKpa`): above 10000 it is absolute pascals, otherwise already gauge kPa.
- `scenes` is empty unless `exposeScenes`.
- `stream.state`: `off`, `requesting` (the prompt is up in the headset), `live`, `denied`,
  `unsupported` (Editor, desktop, or an app with view sharing off), `error`. `message` says why.
  `port`, `width` and `height` are meaningful while `live`.
- `stream.audio` is the app's sound beside the view (see Sound), with the same states: `live`
  while it is served on `port`, as AAC at `sampleRate` Hz in `channels` channels; `denied` when the
  wearer did not allow the microphone permission it needs; `unsupported` when the app does not
  share its sound or its build cannot (`message` says which). Headsets before 0.10.5 do not send
  it, and a phone reads it missing as no sound.
- `controllers` is how many phones have a control connection.
- `haptics` says the headset sends the haptics lines below. Headsets before it do not send the
  field, and a phone reads it missing as false.
- `clockUs` is the headset's clock when the status was made, in microseconds: the clock haptics
  lines and the video are stamped with (see Haptics).

Haptics, whenever a glove's haptics change, and once for each hand right after the welcome so a
phone starts in step. Only the channels that changed are listed, except in that first one, which
has all six. Reference line, the right index fingertip starting to press at 80%:

```json
{"type":"haptics","t":1791966541289123,"hand":"right","channels":[{"finger":1,"mode":"pressure","intensityPct":80,"frequencyDeciHz":0,"tap":false}]}
```

- These are the commands the app sent, not what the glove measured (that is the status's
  `pressureDeciKPa`). They come from `Haptics.GetChannelState`, read once a frame after every
  script's Update, so a command from any script is included.
- `finger`: 0 thumb, 1 index, 2 middle, 3 ring, 4 pinky, 5 palm, as `Haptics.Finger`.
- `mode`: `off`, `pressure` (press and hold), `vibration` or `pulse`.
- `intensityPct`: the command's intensity, 10-100 while on (0.1-1 in the API). `frequencyDeciHz`:
  vibration and pulse frequency in tenths of a hertz; 0 for pressure.
- `tap`: the channel was switched on and off within one frame. The line carries what it was
  switched on to, and a viewer shows it briefly rather than leaving it on.
- `t` is microseconds on the headset's `CLOCK_MONOTONIC` (Java's `System.nanoTime() / 1000`) on
  Android, the clock the captured frames are stamped with, so a viewer can draw each touch on the
  frame that shows it: the touch at `t` belongs on the frame whose presentation time is `t`. A viewer
  that finds the video's times far from `clockUs` (a capture stamped some other way) should line the
  two up by when they arrive instead. In the Editor and on desktop it is any steady clock.

## Video

Plain TCP to `stream.port`, while `stream.state` is `live`. All integers big-endian.

Once per connection, 9 bytes:

```
48 58 56 31   04 00   04 00   01
"HXV1"        width   height  codec (1 = H.264)
```

Then packets, each a 13-byte header and a payload:

```
00 00 00 04   02      00 00 00 00 00 00 03 e8   <4 payload bytes>
length        flags   presentation time (µs)
```

- `length` counts the payload only.
- `flags`: bit 0 codec config (SPS/PPS), bit 1 keyframe.
- The payload is the encoder's Annex-B output, unchanged. Keyframes also carry SPS/PPS.
- A new viewer gets the cached config packet, then nothing until the next keyframe (the headset
  asks the encoder for one when a viewer joins). A viewer that falls behind has its backlog
  dropped and waits for the next keyframe.
- Several phones can watch at once. Stopping the share closes every video connection.

### On the headset

The view comes from Android's MediaProjection: the wearer accepts a system prompt once per
session (a phone pressing "Share" again after a stop needs a new acceptance). The capture runs in
a foreground service of type `mediaProjection`. Defaults: 1024×1024, 4 Mbit/s, 30 fps, Baseline
profile, a keyframe every second. On Quest, Meta's compositor fills the capture surface itself.
Sharing ends if another app starts capturing, if the app is closed, or if it stays in the
background for `stopVideoAfterBackgroundSeconds` (20 s by default).

## Sound

Plain TCP to `stream.audio.port`, while `stream.audio.state` is `live`. All integers big-endian.

Once per connection, 10 bytes:

```
48 58 41 31   00 00 bb 80   02         01
"HXA1"        sample rate   channels   codec (1 = AAC-LC)
```

Then packets with the video's 13-byte header (length, flags, presentation time in µs) and payload:

- The first packet has flag bit 0 set and carries the AAC AudioSpecificConfig (2 bytes, 48 kHz
  stereo: `11 90`). A viewer that joins later gets it first too. Its presentation time is 0.
- Every other packet is one raw AAC frame of 1024 samples, no ADTS header, with flag bit 1 set:
  each one decodes on its own.
- The presentation time is when the frame played, on the clock the video and haptics lines carry,
  so a viewer plays or records sound and picture together by their stamps alone.
- A viewer that falls behind has its backlog dropped, as with the video.

### On the headset

The sound is Android's AudioPlaybackCapture on the same MediaProjection token as the view, so the
wearer's one capture prompt covers both. It captures only what the app itself plays as media or
game audio (`USAGE_MEDIA`, `USAGE_GAME`, the app's own UID): never the microphone, other apps or
the system's sounds. Android guards all audio capture with `RECORD_AUDIO` all the same, so the
first time, the wearer is also asked for the microphone permission, just before the capture
prompt. HexR declares that permission only in builds whose scenes have a HexR Remote with Share
Sound on. Muting the headset mutes what phones hear, as Meta's MediaProjection notes ask.
