# Audio verification harness

This Windows x64 diagnostic tool links the delivered production sources without changing them. Build with .NET 8 SDK. Run the examples from the root of the extracted `ViPER4Windows` folder.

`inspect` and `probe` open the selected render endpoint for metadata and inspect existing global driver maps/events. They do not play or capture audio and do not create/change driver maps.

Example preparation:

```powershell
dotnet build verification/driver-test-source/DriverTest.csproj
dotnet verification/driver-test-source/bin/Debug/net8.0-windows/DriverTest.dll inspect --output verification/driver-test-source/results/inspection
dotnet verification/driver-test-source/bin/Debug/net8.0-windows/DriverTest.dll validate-settings --dll ViperDsp.dll --output verification/driver-test-source/results/settings-validation
```

`validate-settings` checks all five sequence presets against the production native snapshot layout at 44.1 and 48 kHz and processes generated samples in memory. It does not play/capture audio or modify shared driver parameters.

## Virtual route tests

First fully exit the ViPER controller and close FxSound. Pause other playback. Obtain endpoint IDs with `ViPER4Windows-RE.exe --audio-devices devices.json`. Set `$virtualId` to the virtual ViPER render ID and `$physicalId` to the physical render ID.

```powershell
dotnet verification/driver-test-source/bin/Debug/net8.0-windows/DriverTest.dll system-sequence --source $virtualId --endpoint $physicalId --dll ViperDsp.dll --output test-results/system
dotnet verification/driver-test-source/bin/Debug/net8.0-windows/DriverTest.dll system-volume --source $virtualId --endpoint $physicalId --dll ViperDsp.dll --output test-results/volume
dotnet verification/driver-test-source/bin/Debug/net8.0-windows/DriverTest.dll system-soak --source $virtualId --endpoint $physicalId --dll ViperDsp.dll --output test-results/soak
```

`system-sequence` plays quiet generated tones at peak 0.015 (about −36.5 dBFS). It compares the physical output with the processor stopped, bypassed, neutral, bass enabled and EQ enabled. It temporarily changes all three default Windows output roles and restores their original values in `finally`. Inspect `system-summary.json` and `route/` reports. The bypass measurement checks that FxSound is not independently forwarding dry audio.

`system-volume` temporarily changes the virtual endpoint volume and mute, measures −12 dB attenuation and silence, then restores the original volume/mute in `finally`. Physical output volume is left unchanged. Inspect `volume-summary.json`.

`system-soak` runs the silent keepalive for 120 seconds to check continuous processing, output clock adaptation, queue depth, dropped frames and underruns. It does not change Windows defaults. This test measures streaming stability; effect changes are checked by the sequence test.

The app's separate watchdog crash-restoration test is recorded in `../system-audio-watchdog.json`; the harness commands themselves rely on normal exception cleanup and do not start that watchdog. All commands activate render endpoints and speaker loopback, never microphones.

## APO tests

Use the physical output, an installed and registered APO, and administrator rights for its global control channel. Fully exit other control applications before running a sequence.

```powershell
dotnet verification/driver-test-source/bin/Debug/net8.0-windows/DriverTest.dll capture --endpoint $physicalId --mode untouched --output test-results/untouched
dotnet verification/driver-test-source/bin/Debug/net8.0-windows/DriverTest.dll sequence --endpoint $physicalId --dll ViperDsp.dll --output test-results/apo
```

Bare endpoint GUIDs are also accepted and expanded to render IDs. Default selection uses the Windows multimedia render role. Use an explicit physical endpoint for the APO sequence.

Only speaker WASAPI loopback is activated. A microphone endpoint is rejected by its Core Audio data flow. No microphone APIs are called. Source peak is 0.015 (-36.5 dBFS); existing endpoint/session volume is left unchanged in the APO sequence. Each capture plays six 0.6-second tones (63, 125, 250, 1000, 4000, 8000 Hz) with fades, initial silence, and tail silence. Sequence runs baseline bypass, neutral master, bass, EQ, and baseline again.

Each run exports source.wav, per-mode loopback WAVs, per-mode JSON, and summary.json with measured tone amplitudes/differences and driver frame counters. Invalid/denied global map opens contain the exact native Win32 error; 5 is access denied and 2 is not found. `restoration.json` records whether the previous parameters and bulk assets were restored. If no complete previous state could be saved, master processing is disabled when the test finishes. The harness should not run concurrently with another control app publishing new presets.

Loopback includes other applications' playback. Pause unrelated playback before interpreting measurements. FxSound is a virtual output, so its loopback may omit downstream FxSound/physical output processing; these measurements alone cannot prove the final audible hardware path.

Loopback captures the Windows speaker stream, not analog sound from the hardware. Results from this PC are in the adjacent JSON reports. The fresh APO test proves DLL loading and native frame processing; it does not claim a post-refresh APO-specific bass/EQ A/B comparison. The virtual route's bass/EQ comparison was independently measured on Realtek.

Technique follows Microsoft's [Loopback Recording](https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording) and [IAudioCaptureClient::GetBuffer](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer) documentation.

