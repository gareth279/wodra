# ANTUSB diagnostic probe

Run `powershell -ExecutionPolicy Bypass -File tools/ant-sdk/Probe-AntUsb.ps1` from the repository root to check the bundled ANT SDK dependencies, Windows PnP reporting for Elite ANTUSB-m (`VID_0FCF&PID_1009`), SDK USB enumeration, and device open/release. The enumeration call itself also verifies that the native ANT entry point can load; library load, architecture, and entry-point exceptions are reported separately.

The probe does not set a network key, configure/open a radio channel, send trainer data, or issue resistance commands. A zero SDK device count is reported as an enumeration failure; missing PnP results alone are not treated as proof that the dongle is absent. A successful probe only establishes stage 1. Wodra's ANT+ connection diagnostics record the later network, source-ID, channel, FE-C page, capability, and command-acknowledgement stages.
