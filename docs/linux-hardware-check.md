# Linux hardware check

Run these on a real KDE Plasma 6 PC with Bluetooth and a power station. Containers cover packaging
and D-Bus wiring, but not the radio or the desktop session. Run everything as your normal desktop
user, not root, which also confirms that BlueZ's default D-Bus policy allows Anchor's calls.

## Bluetooth (SolixConsole)

1. Check BlueZ: `systemctl status bluetooth`, `bluetoothctl --version` (5.62 or newer reports the
   MTU), and `bluetoothctl show` (`Powered: yes`).
2. Publish the console:
   `dotnet publish tools/SolixConsole -c Release -f net10.0 -r linux-x64 --self-contained -o ~/solixconsole`
3. Scan: `~/solixconsole/SolixConsole -v 2>&1 | tee scan.log`. The station should be listed with its
   product type (b118 for a C1000 Gen 2).
4. Connect: `~/solixconsole/SolixConsole <MAC> <model> -v 2>&1 | tee connect.log`. Expect
   "Negotiated ATT MTU: N", "Negotiations ... succeeded", then battery and AC lines. Leave it
   running for 30 minutes, press the station's Bluetooth button once, and check that it
   reconnects.
5. Run `bluetoothctl remove <MAC>`, then repeat step 4. Expect "BlueZ has no object ...
   discovering".
6. `bluetoothctl info <MAC>` shows `Paired: no` and `Trusted: no`.
7. `bluetoothctl power off; ~/solixconsole/SolixConsole` shows `RadioOff`. Then run
   `bluetoothctl power on`.

If any step fails, run `sudo btmon > btmon.log` while you retry, and keep all the logs.

## Desktop (installed package, dry run on)

1. The tray icon appears. Click it to open Status.
2. Settings: click Scan, pick the station, and save with dry run on.
3. Unplug wall power. You get a "Wall power lost" notification.
4. Set the floor above the current battery level. The countdown window appears above other
   windows, including a focused or fullscreen app. The "Shutdown countdown started"
   notification stays on screen; click its "Cancel shutdown" to cancel.
5. Plug in, unplug, and let a countdown reach zero. You get "Dry run: shutdown suppressed", and
   the log has a `[DRY RUN]` line.
6. Lock the screen, power-cycle the station's Bluetooth, and unlock. No availability
   notifications appear while locked, and one appears about a minute after you unlock if the
   state changed.
7. Log out and back in. Anchor starts on its own (`~/.config/autostart/anchor.desktop`).
8. With dry run off, open a text editor with unsaved changes and let a countdown reach zero.
   Plasma asks the editor to save before it shuts down, and the log says "via KDE session".

Logs are in `~/.config/Anchor/logs/anchor.log`.
