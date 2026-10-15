# Trainer connectivity

Wodra connects Bluetooth LE smart trainers using FTMS and supports ANT+ FE-C through an Elite ANTUSB-m receiver. The connection screen owns one selected trainer for the application session, starts scanning when opened, automatically connects one compatible trainer, and asks the rider to choose when multiple devices are found. Each protocol's trainer identity is stored separately. Heart rate discovery is not part of trainer readiness.

## Lifecycle and presentation

Continue is enabled only after the Bluetooth link has been initiated by uncached FTMS GATT discovery, the control-point indication is subscribed, FTMS control is acknowledged, and Indoor Bike Data notifications are subscribed. The device panel retains that client across route selection and ride transitions. An unexpected link loss gets at most three reconnect attempts; an explicit disconnect invalidates the attempt and suppresses recovery. Reconnection performs FTMS setup again and reapplies the current ride grade and rider/bike weights. Telemetry readouts expose awaiting, unsupported, stale, measured-zero and current values separately. Stale power/cadence are replaced with zero only for ride physics and retain their original timestamp for UI freshness checks.

## Protocol preference (#4)

The connection screen persists **Auto**, **Bluetooth FTMS**, or **ANT+ FE-C** under `Trainer.ProtocolPreference` in the per-user settings file. New settings use Auto. Existing settings without a protocol value migrate to Bluetooth FTMS, preserving the behavior of prior installations. Auto prefers Bluetooth FTMS; explicit ANT+ selection starts FE-C discovery and never silently switches protocols. Each protocol keeps its own remembered trainer identity.

`LastTrainerBluetoothAddress` remains the remembered BLE identity. ANT device number, device type, and transmission type have separate settings fields; they are never inferred from the BLE address.

## ANTUSB-m support and acceptance status (#3)

The Elite ANTUSB-m is an end-of-life ANT receiver ([official product and driver page](https://www.thisisant.com/developer/components/antusb-m)). Windows currently reports `USB\\VID_0FCF&PID_1009` as **ANT USB-m**, status **Started**, using `oem51.inf`. `pnputil` identifies the installed package as Dynastream `ant_libusb.inf`, driver version 1.2.40.201, signed by Microsoft Windows Hardware Compatibility Publisher. No driver changes were made during this work.

The owner supplied the official ANT PC SDK 3.5 package and Software Tools driver archive. Both were extracted under the system temporary directory, outside the repository. A temporary x86 .NET Framework smoke program using the SDK's `ANT_NET.dll` / `ANT_WrappedLib.dll` enumerated one stick, initialized USB device 0 at 57600 baud, and called the SDK shutdown API successfully. The smoke process exited normally. This verifies the installed driver and SDK can open and release the dongle on this x64 Windows host. It does not exercise FE-C or the Flux 2.

The SDK's shipped DLLs are 32-bit. Its own readme says those binaries require x86 applications, even on 64-bit Windows. The SDK source was rebuilt as x64 with Visual Studio 2022 and the v143 toolset, and the generated native wrapper plus managed library were loaded by an x64 .NET 8 smoke program. That program again enumerated one stick, initialized it, and released it successfully. The repeatable source-based build is `tools/ant-sdk/build-x64.ps1`; its output and required ANT SDK license notice are in `src/IndoorCycling.Trainer.Windows/AntSdk`. The x64 smoke also succeeded using the user-mode `libusb0.dll` installed in Windows by the official driver package, so Wodra does not need to bundle or install another USB driver. The SDK `License.txt` permits binary redistribution with its copyright, conditions, and disclaimer reproduced in accompanying materials. Keep the profile PDF and other restricted adopter documents out of this repository.

The official PC SDK is an older vendor SDK. The [AntPlus .NET library](https://github.com/StephenHidem/AntPlus) is a maintained alternative for application-level device profiles, but its USB stick project still depends on the ANT PC SDK. Other community USB integrations require replacing the driver, which is not suitable here. The selected implementation uses the official PC SDK binaries and profile definitions, with their redistribution notices.

The Fitness Equipment device profile is used for FE-C compatibility. The official Windows USB driver remains a separate prerequisite; it is not bundled or installed by Wodra.

### ANT+ first-time setup

1. Open **Connect Trainer** and select **ANT+ FE-C**. The compact ANT+ Setup section appears on that screen.
2. Open Garmin’s [ANT+ Network Keys page](https://developer.garmin.com/ant-program/ant-ant-plus/network-keys/) and copy the **ANT+ 64-bit entry**.
3. Paste it into the visible key field and choose **Save key**. The field accepts 16 compact hexadecimal digits, eight comma-separated `0xNN` bytes, eight whitespace-separated bytes, or eight colon- or hyphen-separated bytes.
4. Wake the trainer, close ANTware if it is open, then choose **Connect trainer**.

The key is stored in `%LOCALAPPDATA%\Wodra\ant-config.json`, once per Windows account on each PC. It remains available through Wodra updates. Disconnect ANT+ before changing or clearing setup. Wodra validates the full input, stores the existing compact format, and never writes the key to application logs.

Advanced troubleshooting example (leave the value empty until configured in Wodra):

```json
{"networkKey":""}
```

`AntSdkDeviceSession` owns the SDK USB handle. Initial discovery opens a regular slave receive channel with wildcard ID `0/17/0`, pairing off, period 8192, and frequency 57. ANT PC SDK 3.5 dispatches ordinary `0x4E` broadcasts and channel events through `ANT_Channel.channelResponse`. It routes `CHANNEL_ID (0x51)` to `ANT_Device.deviceResponse`; it does not route that protocol response through `channelResponse`. To resolve the transmitter behind a wildcard channel, discovery sends the SDK's nonblocking `requestMessage(0, CHANNEL_ID_0x51)` after receiving FE-C telemetry, then correlates the reply's channel number to the active scan. It buffers a bounded count of valid pages while identity is pending, retries the request at most three times, and rejects zero wildcard IDs and stale attempts. Extended source IDs remain supported. If pages arrive without a usable ID response, diagnostics and the connection panel report that trainer data arrived but identification failed. A wildcard receive channel is locked to one transmitter at a time; the five-second scan does not enumerate every nearby trainer, so the picker only applies when multiple identities are actually observed. A selected or remembered trainer then connects by its exact device number, type, and transmission type. The client waits for general data, trainer data, and the requested capability page before reporting ready, and sends acknowledged simulation/basic-resistance controls followed by command-status checks. The shared FE-C decoder handles invalid values and rolling power counters.

The supplied ANTware capture on the cycling PC records 112 ordinary broadcast frames after a normal wildcard receive-channel setup: 54 general page `0x10` frames and 52 trainer page `0x19` frames. The channel-ID response identifies device 4206, type 17, transmission 5. In contrast, Wodra alpha.22 used continuous receive scan and set the wildcard pairing bit, which the SDK implements by setting bit 7 of the device type. The subsequent alpha.23 log showed the repaired channel receiving nine page `0x10` and nine page `0x19` frames, all rejected as `source_id_missing`. Inspection of the SDK dispatcher proved that Wodra had not requested the ID, and its response handler was looking at the wrong event. The current fix requests channel ID `0x51` and handles the response on `deviceResponse`, retaining early FE-C page evidence until it resolves. This is source and capture evidence for the implementation; the new request/response path still requires Wodra-to-Flux hardware acceptance.

For live acceptance, keep the ANTUSB-m connected, close ANTware or any other application using it, and wake the Flux 2 by powering it and pedaling. Then select **ANT+ FE-C** and **Connect trainer**. ANT telemetry, resistance control, and recovery still require owner hardware acceptance.
