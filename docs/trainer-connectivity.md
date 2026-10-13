# Trainer connectivity

Wodra currently connects Bluetooth LE smart trainers using FTMS. The connection screen owns one selected trainer for the application session, starts scanning when opened, automatically connects one compatible trainer, and asks the rider to choose if more than one is present. The last successfully used Bluetooth address is stored in user settings. Heart rate discovery is not part of trainer readiness.

## Lifecycle and presentation

Continue is enabled only after the Bluetooth link has been initiated by uncached FTMS GATT discovery, the control-point indication is subscribed, FTMS control is acknowledged, and Indoor Bike Data notifications are subscribed. The device panel retains that client across route selection and ride transitions. An unexpected link loss gets at most three reconnect attempts; an explicit disconnect invalidates the attempt and suppresses recovery. Reconnection performs FTMS setup again and reapplies the current ride grade and rider/bike weights. Telemetry readouts expose awaiting, unsupported, stale, measured-zero and current values separately. Stale power/cadence are replaced with zero only for ride physics and retain their original timestamp for UI freshness checks.

## ANT+ FE-C integration points (#3)

`TrainerDiscoveryPolicy` defines protocol-neutral candidate selection and identity preference. A future ANT+ provider should produce the same device identity, capabilities, ready state, telemetry timestamps and loss/recovery events, and should implement the existing `ITrainerClient` control contract. It must not appear in the UI until an actual FE-C scan/control implementation and hardware checks exist. Cross-protocol device de-duplication needs a stable hardware identity; a Bluetooth address alone cannot identify the corresponding ANT+ channel.

## Protocol preference (#4)

`LastTrainerBluetoothAddress` currently remembers the last successful Bluetooth trainer. It is a remembered-device preference, not a Bluetooth-versus-ANT+ protocol preference. When both providers exist, add a persisted protocol preference before discovery, honor it before de-duplication, and give the rider an explicit fallback choice when that provider is unavailable. No ANT+ preference option is currently exposed.
