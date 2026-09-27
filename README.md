# WiFile

Share files and chat with every PC on the same Wi-Fi. There's no internet, no accounts, no passwords, and no host/guest setup.

Install it on each PC and open it. PCs on the same network find each other automatically within a couple of seconds.

## What you get

**Left: the shared folder.** It's the real Windows Explorer view embedded in the app, so everything works the way it does in Explorer:

- Drag and drop files or folders in and out
- Copy, cut and paste (Ctrl+C / Ctrl+X / Ctrl+V), rename (F2) and delete. Pasting an image or text from the clipboard saves it as a new file.
- Right-click → New folder
- Thumbnails, plus a **View** menu (icon sizes, List, Details, Tiles, Content)
- A clickable path bar (Shared › Photos › 2024)
- Light or dark theme, following your Windows setting
- Double-click opens a file with its default app

Whatever you put here appears on every other PC running WiFile. Changes, renames, moves and deletes spread the same way.

**Right: the chat.** Send text, links, images, music or any file, including whole folders (zipped automatically):

- Send to everyone or pick one device.
- Drag files onto the chat, use the paperclip, or paste a screenshot with Ctrl+V.
- Images show a preview. Any file opens in its default app.
- Received files are saved in `Downloads\WiFile`.

## How it works

- **Replicated folder.** Every PC keeps its own full copy in `C:\Users\<you>\WiFile`. There's no server: if a PC leaves, everyone else still has the files, and a PC that was offline catches up when it returns. You can also use that folder directly from Explorer, Office and so on.
- **Discovery.** A small UDP broadcast beacon (port 45877) every 2 seconds, sent on every network adapter.
- **Transfers.** Direct TCP between PCs (port 45878, or any free port). One stream per file runs at full Wi-Fi speed. A 60 MB file reached 2 peers in about 2 seconds in local tests.
- **Conflicts.** Every change gets a hybrid-clock version, and the most recent change wins. Deletions are recorded so they spread too.
- **Safety net.** A file removed or overwritten by *another* PC goes to this PC's Recycle Bin. Files still open for writing wait until they're closed. If the whole shared folder is deleted, WiFile doesn't delete it everywhere: it recreates the folder and downloads everything again.
- **Moves don't re-download.** A moved or renamed file reuses the local bytes. Each file is checked with a fast content fingerprint.
- **Firewall-proof.** If one PC's firewall blocks incoming connections, the other PC asks it (over UDP) to connect back, and the transfer runs over that connection. WiFile also shows an **Allow** button that fixes the firewall rule after an admin prompt.
- **Live view.** Changes from other PCs, or from other apps on this PC, show up in the file view instantly.
- **Background running.** Closing the window keeps WiFile syncing in the tray. It starts with Windows by default; you can turn that off in the `⋯` menu.

## Requirements

- Windows 10 (1809+) or Windows 11. It uses the built-in .NET Framework 4.8, so there's nothing else to install.
- All PCs on the same network or subnet. Guest Wi-Fi with "client isolation" blocks PC-to-PC traffic, which no LAN app can get around.
- The installer adds one Windows Firewall rule for `WiFile.exe`. That's why it asks for admin once.

## Security note

Anyone on the same network who runs WiFile can read and change the shared folder and chat with you. That's the point of "no passwords", so use it on networks you trust (home, office, lab).

## Building from source

Requires the .NET SDK 8+ and Inno Setup 6.

```powershell
./build.ps1          # builds, runs the 3-node end-to-end test, writes dist\WiFile-Setup-1.0.0.exe
```

Run a second isolated instance on one PC for testing with `WiFile.exe --profile B`.

Layout:

- `src/WiFile/Core`: platform-neutral engine (discovery, wire protocol, sync, chat). It has no UI dependencies, so it can be reused by a future Linux front end.
- `src/WiFile/UI`: Windows UI (embedded Explorer via `IExplorerBrowser`, chat panel, tray).
- `tests/WiFile.Tests`: end-to-end test with 3 real nodes over loopback.
- `installer/WiFile.iss`: Inno Setup script.

## Linux

The Windows app is the supported target. The engine in `Core/` is plain .NET with a simple JSON-and-bytes TCP protocol, so a Linux front end, such as a headless sync daemon or a GTK UI, can interoperate with Windows PCs.
