# Folder Bridge

Folder Bridge keeps a folder identical on two Windows computers over your local network. There is no cloud and no account: the computers find each other on the LAN and talk directly over an encrypted connection.

## Using it

Open Folder Bridge on both computers. On one of them click **Pair a device**, pick the other computer (or type its address) and press **Pair** on both once they show the same six-digit code. Then click **Add a folder**, choose the folder and tick the other computer. The other computer asks where to keep it, and from then on changes on either side are copied to the other within a few seconds.

Closing the window keeps Folder Bridge running in the tray. On first start Windows Firewall asks whether to let it through; allow it on private networks. It listens on TCP port 22000 and announces itself on UDP port 22027.

## What happens to your files

Every file is checked with SHA-256 after it arrives and only then replaces the old copy. Files deleted on one computer are moved to the hidden `.folderbridge\trash` folder on the other and kept there for 30 days. If the same file was changed on both computers before they could sync, the newer edit wins and the other one is kept next to it as `name.conflict-COMPUTER-date.ext`.

## Building

You need the .NET 10 SDK.

```
dotnet test
dotnet publish src/FolderBridge -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The result is a single `publish\FolderBridge.exe` that runs without .NET installed. Settings and the device certificate live in `%LOCALAPPDATA%\FolderBridge`; set `FOLDERBRIDGE_DATA_DIR` to run a second copy with its own data.
