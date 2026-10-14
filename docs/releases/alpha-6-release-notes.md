# Wodra 0.1.0-alpha.6

## Trainer reconnection

- Unexpected Bluetooth loss now starts repeated discovery of the selected trainer for up to 90 seconds, with fresh BLE and GATT resources on every attempt.
- Recovery follows the selected trainer by its remembered address or advertised identity, so a different nearby trainer is not silently selected.
- A successful return repeats FTMS control-point subscription, waits for the control acknowledgement, restores telemetry, and reapplies the current ride settings.
- If automatic recovery expires, Retry is available in the ride HUD and continues recovery without ending the ride or FIT recording.
- Recovery diagnostics include attempt timing, discovered and selected identities, failure stage, and restoration time.

## Cause and verification

In alpha.5, recovery stopped after three short retries that reused the saved address without rediscovering the trainer. The Flux 2's recorded `Unreachable` result occurred during FTMS service discovery; it identifies the failing stage but does not establish the BLE stack's underlying reason. The later successful fresh connection and its timing support recovery ending before the trainer had returned to advertising as the actionable failure.

Automated recovery policy and lifecycle regression checks pass. Real Tacx Flux 2 power-cycle and in-ride Retry verification are still pending.
