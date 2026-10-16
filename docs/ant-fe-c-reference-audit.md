# ANT+ FE-C reference audit for #65

Audit date: 2026-10-16. Alpha.25 baseline: `52bffcce854bdd4f4e42b92bfc45370fdad9e730`. Alpha.25's corrected page-70 request encoding remains in place. This audit compares the new live logs with the pinned Incyclist implementation and ANT+ FE-C Profile Rev 5.0. Synthetic tests do not establish live Flux 2 control.

## Latest reproduction

`wodra-20261016-152209.jsonl` contains two connection attempts. Each opens one ANT USB device, accepts network/channel setup and `OPEN_RX_SCAN`, resolves the same transmitter identity `4206/17/5`, and receives FE-C pages 16 and 25. Each sends three page-70 requests for page 54; the ANT SDK returns `Pass` for each acknowledged transfer, but no page 54 arrives. Wodra then waits 40 seconds and disconnects because `AntFeCProtocol.IsReady` required a capability response. This confirms radio reception and trainer telemetry; it does not confirm FE-C control or resistance change.

`wodra-20261016-152137.jsonl` is only the updater session. The named Godot logs show startup/graphics initialization and no extra ANT evidence. The 15:21:35 log refers to an earlier session; it is not the 15:22:09 connection attempt. No raw ANT data payloads or network-key values are included here.

## Protocol requirements versus Wodra policy

The ANT+ Fitness Equipment Device Profile Rev 5.0 states in §6.2 that a controller should request page 54 to determine control use and modes. §8.10.1 says the equipment shall send page 54 on request and all equipment are required to support it. Thus the Tacx's missing response is a profile deviation if the correctly received request was processed; the log's ANT transport `Pass` alone cannot prove the request reached the FE application. Missing page 54 is not proof that telemetry is absent, nor is it a reason for Wodra to erase valid telemetry and wait 40 seconds.

The Incyclist `FitnessEquipmentSensor` does not decode capability/status pages and its FE adapter establishes the connection from sensor data. `initControl` sends user configuration and initial control; subsequent controls are serialized and sent as acknowledged data. It does not poll page 71 before treating the transport send as complete. This is practical interoperability behavior, not evidence that the profile's page-54 requirement is optional.

Profile §8.12.3 defines page 71 as trainer-reported command status. A controllable FE shall broadcast it in response to a request. But §8.13.1 says page-70 requests may be used as needed; they are not required to be interleaved regularly. Wodra's old design issued a page-70 request after every control and failed the command if page 71 did not arrive within three seconds. That was an unnecessary Wodra gate and could turn a delivered control into an application failure. The revised client does not poll/wait for page 71. If a matching page 71 is received, Wodra records that trainer-reported result separately.

The profile says FE-C equipment must support target-power control (§5.2.1 and §8.8.2), and page 51 is track-resistance/simulation control (§8.8.4). Page 54 flags, when received and valid, are still honored. When absent, Wodra keeps support `Unknown`; it attempts normal simulation control as a compatibility path, matching Incyclist's permissive startup/control flow. A valid page 54 that explicitly clears simulation support blocks a controlled ride. No capability bits are fabricated.

## Changes beyond the alpha.25 request-byte correction

- Readiness now means that general page 16 and trainer page 25 telemetry have arrived. Capability page 54 remains requested with the corrected request, validated when received, and may update the active session later, but it no longer blocks telemetry readiness.
- FE-C capability state is tri-state: supported, unsupported, or unknown. Unknown allows a clearly labelled compatibility control attempt; explicit unsupported remains unavailable.
- The first simulation command sends user configuration page 55 before track-resistance page 51. All capability requests and controls now share the command semaphore, avoiding overlapping acknowledged sends.
- Control completion follows the ANT acknowledged-data return. A transport failure remains a failure. Page 71 polling and its mandatory wait were removed; a matching received page 71 can still update trainer-reported status.
- The UI says telemetry connected, shows whether capability state is unknown, and reports control transport/trainer status separately from physical resistance, which is never claimed as verified. Grade-cache/log field wording now describes transport acknowledgement.
- `AntFeCSessionState` is shared by the production ANT client and simulated transport regression tests. It resets on disconnect/reconnect and preserves late capability and status updates for the active session.

| Area | Reference implementation | Wodra decision |
|---|---|---|
| FE connection | Incyclist starts from FE sensor data; no page-54 gate. | Adopt telemetry-based readiness (both pages 16 and 25), while leaving capability unknown if absent. |
| User configuration/control | Adapter initializes with user config and initial control; sends are serialized. | Preserve page 55 before first route-grade page 51; serialize with capability requests. |
| Capabilities | Reference sensor does not implement page-54 state. | Validate and honor page 54 when available; compatibility attempt only when unknown. |
| Command status | Reference does not decode page 71. | Remove mandatory status polling; retain optional matching page-71 observation as separate evidence. |
| Evidence | Reference reports transport send completion. | Keep transport acknowledgement, trainer-reported result, and physical resistance verification distinct. |

Reviewed immutable source commits: [`incyclist/devices` `fc33350859f9b4089548c546e3b862a2df668e1f`](https://github.com/incyclist/devices/tree/fc33350859f9b4089548c546e3b862a2df668e1f) and [`incyclist/ant-plus` `a77eb3b57d361b78972f26057895d5c531b0b8d4`](https://github.com/incyclist/ant-plus/tree/a77eb3b57d361b78972f26057895d5c531b0b8d4). Source: [FE adapter initialization/update](https://github.com/incyclist/devices/blob/fc33350859f9b4089548c546e3b862a2df668e1f/src/antv2/fe/adapter.ts#L244-L304), [FE sensor pages and support state](https://github.com/incyclist/ant-plus/blob/a77eb3b57d361b78972f26057895d5c531b0b8d4/src/sensors/fitness-equipment-sensors.ts#L80-L165). Official profile materials are listed by [Garmin ANT+ Device Profiles](https://thisisant.developer.garmin.com/pages/developer/ant-plus/device-profiles/index.html); relevant Profile Rev 5.0 sections are §§5.2.1, 6.2, 8.8.2–8.8.4, 8.10.1, 8.12.3, 8.13.1.

No Incyclist code was copied and no dependency was added.

## Verification boundary

These logs establish ANT device open, channel setup, transmitter identity, pages 16/25, and ANT transport acknowledgement for page-70 capability requests. They do not establish page 54, any control send after this change, a trainer command result, physical resistance change, or recovery. The production client was not re-tested against the dongle/Flux 2 during this change. Owner testing remains required for live telemetry, ride control, resistance change using normal ride settings, disconnect/reconnect, and power-cycle recovery. The app must not describe transport acknowledgement as physical verification.
