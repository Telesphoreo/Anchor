# Anchor

Anchor turns your Anker Solix power station into a UPS for your Windows PC. It sits in your system tray, talks to the power station over Bluetooth, and shuts your computer down gracefully when the power goes out and the battery gets low.

If you've ever wished PowerChute worked without a special cable or a dedicated UPS box, this is that. Your power station is already running your PC. Anchor just watches the battery and handles the shutdown so you don't lose work.

## Screenshots

![Anchor Settings](docs/screenshots/settings.png)

![Anchor Status](docs/screenshots/status.png)

## What it does

Anchor connects to your power station over Bluetooth and watches two things: whether wall power is plugged in, and what the battery level is.

When you lose wall power and the battery drops to your chosen floor (say, 15%), Anchor starts a countdown. If the power comes back during the countdown, it cancels. If not, it shuts down Windows cleanly.

There's a dry run mode if you want to test your settings without actually shutting down. It runs through all the same logic but just logs what it would have done.

## Which models work

The C1000X is tested and working. The C1000 Gen 2, C300, C800, F2000, and F3800 should also work but I haven't personally tested them.

Other Anker stuff (Solarbank, PowerCore chargers, power banks) won't work. They either don't broadcast the data Anchor needs or don't make sense as a PC backup.

## Install

Download the latest installer from the releases page and run it. It needs admin rights to install to Program Files.

Windows will probably complain because the installer isn't signed. Click "More info" then "Run anyway" to get past SmartScreen.

Anchor sets itself to start with Windows by default. You can change this in Settings later. Your PC needs Bluetooth, and it needs to be close enough to the power station for Bluetooth to reach.

## Getting started

Click the Anchor icon in your system tray and open Settings. Hit Scan to find your power station, pick it from the list, tell Anchor which model it is, and set your battery floor (the percentage where Anchor starts the shutdown countdown). Save, and Anchor connects and starts watching.

Leave dry run on for your first test if you want to confirm everything works before trusting it with a real shutdown.

## Settings

Your settings live in `%APPDATA%\Anchor\config.json`. You can edit the file directly if you want (just restart Anchor after), but the Settings dialog handles everything. The main things you can change are which device to connect to, what model it is, the battery floor (default 15%), and how long the shutdown countdown runs (default 60 seconds).

The uninstaller leaves your settings folder alone, so your config survives reinstalls.

## Updates

Anchor checks for updates when it starts. If there's a new version, you'll get a notification and a menu option to download it. You can also check manually from the tray menu.

## Building it yourself

You'll need the .NET 10 SDK, PowerShell 7, and Inno Setup 6 if you want to build the installer. From the repo root: `pwsh scripts\build-installer.ps1`. Or just `dotnet run --project src\AnchorTray` to run it during development.

## If scanning doesn't find anything

Press the Bluetooth button on your power station. If you set it up over WiFi, that actually turns Bluetooth off, so pressing the button turns it back on. If that doesn't work, try power cycling the station. Also make sure you're within Bluetooth range (roughly 30 feet) and that the Anker phone app isn't connected, since the station only talks to one thing at a time.

You can run Anchor straight from source with `dotnet run --project src/AnchorTray` if you don't want to install it. Just close any installed copy first since only one instance can run at a time. Logs are at `%APPDATA%\Anchor\logs\anchor.log`.

## Not affiliated with Anker

This is a hobby project. "Anker" and "Solix" are their trademarks. Licensed under MIT. The Bluetooth code is based on [SolixBLE](https://github.com/flip-dots/SolixBLE).
