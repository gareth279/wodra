# Trainer connectivity

Wodra connects Bluetooth LE smart trainers using FTMS and supports ANT+ FE-C through an Elite ANTUSB-m receiver. The connection screen owns one selected trainer for the application session, starts scanning when opened, automatically connects one compatible trainer, and asks the rider to choose when multiple devices are found. Each protocol's trainer identity is stored separately. Heart rate discovery is not part of trainer readiness.

The first **Connect trainer** action also starts Bluetooth heart-rate and Elite Sterzo Smart discovery independently. One compatible optional device is connected automatically; multiple devices of that category get their own picker. The last successfully connected BLE identity is preferred on later scans. No device, scan failure, cancellation, or failed optional connection leaves trainer readiness unchanged. The trainer becomes ready only after its own protocol setup completes. Optional searches can be retried or cancelled separately, and leaving the connection screen cancels in-flight optional scans and connection attempts. ANT+ FE-C remains a trainer protocol; optional heart-rate and steering devices currently use Bluetooth LE.

The production RideScreen scene test uses fake radio clients to hold trainer control acknowledgement while optional scans remain pending. It verifies independent initial discovery, rejects Continue before the trainer setup completes, confirms optional scans do not delay a ready trainer, and checks cancellation on Back. Discovery policies also cover compatible-device filtering, repeated advertisements, a single automatic choice, multiple-device pickers, and remembered identity preference. Physical hardware acceptance remains required for Bluetooth trainer + HR + Sterzo coexistence, FTMS configuration and telemetry, ANTUSB-m/FE-C control, dropouts and reconnects, and recordings with a heart-rate strap.

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

`AntSdkDeviceSession` owns the SDK USB handle. Initial discovery first uses the ANT SDK's RX scan mode with extended source IDs enabled. It listens for up to the bounded discovery interval, accepts only frames identified as the FE-C indoor-bike profile (device type 17), and collects distinct source identities. If scan mode is rejected or yields no eligible source, Wodra closes that session and falls back to a regular wildcard slave receive channel (`0/17/0`, pairing off, period 8192, frequency 57). This is the normal receive path retained for actual connection and for adapters that do not provide scan-mode frames.

ANT PC SDK 3.5 dispatches ordinary `0x4E` broadcasts and channel events through `ANT_Channel.channelResponse`; it routes `CHANNEL_ID (0x51)` to `ANT_Device.deviceResponse`. The normal wildcard path sends a nonblocking channel-ID request after FE-C pages arrive, buffers a bounded number of pages while identity is pending, and rejects zero IDs, non-FE-C profiles, and stale callbacks. A candidate from RX scan is identified by extended source fields. In either mode, multiple observed eligible sources remain an explicit picker choice, with the saved verified source first; the subsequent trainer connection uses that exact identity. Initial connection tries a saved identity first, then automatically performs fresh discovery if its telemetry search expires. In-ride recovery remains pinned to the already selected source. A saved identity is updated only from fresh discovery, an extended source ID, or a matching channel-ID response; ordinary frames received on a channel configured for a saved number are not treated as independent identity proof. **Multiple-transmitter behavior still needs owner hardware acceptance with the Elite steering unit nearby.**

The client waits for general and trainer telemetry pages before reporting ready; capability metadata may remain unknown for compatibility. It sends acknowledged simulation/basic-resistance controls and verifies command status when provided. The shared FE-C decoder handles invalid values and rolling power counters.

The supplied ANTware capture on the cycling PC records 112 ordinary broadcast frames after a normal wildcard receive-channel setup: 54 general page `0x10` frames and 52 trainer page `0x19` frames. The channel-ID response identifies device 4206, type 17, transmission 5. In contrast, Wodra alpha.22 used continuous receive scan and set the wildcard pairing bit, which the SDK implements by setting bit 7 of the device type. The subsequent alpha.23 log showed the repaired channel receiving nine page `0x10` and nine page `0x19` frames, all rejected as `source_id_missing`. Inspection of the SDK dispatcher proved that Wodra had not requested the ID, and its response handler was looking at the wrong event. The current fix requests channel ID `0x51` and handles the response on `deviceResponse`, retaining early FE-C page evidence until it resolves. This is source and capture evidence for the implementation; the new request/response path still requires Wodra-to-Flux hardware acceptance.

For live acceptance, keep the ANTUSB-m connected, close ANTware or any other application using it, and wake the Flux 2 by powering it and pedaling. Then select **ANT+ FE-C** and **Connect trainer**. ANT telemetry, resistance control, and recovery still require owner hardware acceptance.
