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

1. Install the supported USB driver if Windows needs it.
2. Open Wodra and use **ANT+ Setup** on the startup screen.
3. Open Garmin’s [ANT+ Network Keys page](https://developer.garmin.com/ant-program/ant-ant-plus/network-keys/), copy the ANT+ network key, paste it into Wodra, and choose **Save**.
4. Select ANT+ FE-C and connect the trainer.

The key is stored in `%LOCALAPPDATA%\Wodra\ant-config.json`, once per Windows account on each PC. It remains available through Wodra updates. Disconnect ANT+ before changing or clearing setup. Wodra validates the 16 hexadecimal digits and never writes the key to application logs.

Advanced troubleshooting example (leave the value empty until configured in Wodra):

```json
{"networkKey":""}
```

`AntSdkDeviceSession` owns the SDK USB handle. Initial discovery uses ANT continuous receive scan mode for five seconds, filters source IDs to FE-C pages, deduplicates candidates, and connects directly when one trainer is found. Multiple matches use the existing trainer picker. A selected or remembered trainer is then paired by its exact device number, type, and transmission type. The client waits for general data, trainer data, and the requested capability page before reporting ready, and sends acknowledged simulation/basic-resistance controls followed by command-status checks. The shared FE-C decoder handles invalid values and rolling power counters. These code paths have not yet been exercised against the Flux 2. If the ANTUSB-m/driver does not support continuous scan mode, multiple-trainer discovery will report that limitation rather than silently selecting an arbitrary trainer.

Before live acceptance, install the official ANTUSB-m driver package if Windows does not show the dongle as started, use the key helper above, and close ANTware or any other application using the dongle. Keep the ANTUSB-m connected. Wake the Flux 2 by powering it and pedaling; then select **ANT+ FE-C** and **Connect trainer**. No trainer was awakened or controlled during this implementation session.
