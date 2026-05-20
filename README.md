# steam-workshop-uploader

A small, Linux-native command-line tool to publish content to the Steam Workshop via SteamUGC. Written in C# on .NET 8 using [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET).

Built to replace bitrotted game-bundled "official" uploader executables that hang on every modern platform. Should work for any Steam app with Workshop support — the AppID is configurable per manifest.

---

## ⚠ Read this first: the one rule

**Always fully restart Steam before publishing.**

In Steam's menu bar: `Steam → Exit` (not just close window — full exit so background helpers shut down). Wait ~10 seconds. Relaunch. Log in if prompted. Then run the uploader.

Without this, the SteamUGC submit silently hangs at "preparing config" with no error — Steam logs `Upload starting` then never progresses. This affects every standalone Workshop uploader on every platform (this one, EA's frozen `Uploader.exe`, every clone), and it's the reason many modders give up and conclude "Workshop publishing is broken for this game."

It isn't. Steam just needs a clean process state for UGC submission. A 2020 [Reddit thread for C&C Remastered modders](https://www.reddit.com/r/commandandconquer/comments/h139eb/anyone_any_idea_why_a_mod_wont_upload_to_the/) hinted at this with "I ran the exact same files through a VM and it worked" — a virgin Steam install = a fresh process = no stale cache.

Hours of debugging led to this one-line fix. Don't skip it.

The tool will warn you and exit early if it detects no progress for >60s in "preparing config" — at which point the answer is: restart Steam, retry.

---

## What it does

- Reads a `workshop.json` manifest (same schema as EA's tool, plus an `appid` field)
- Initialises SteamAPI against your running, logged-in Steam client
- If `publishedfileid` is empty, calls `SteamUGC.CreateItem` and persists the new ID back to the JSON
- Calls the full SteamUGC setter sequence (content, preview, language, title, description, visibility, tags) — order and surface matched against EA's MapEditor source so it behaves like an officially-blessed publisher
- Submits, streams progress to stdout, exits 0 on success

That's it. ~350 lines, no abstractions, no UI.

There IS no UI — the tool is designed to be driven from a terminal or, more pleasantly, from [Claude Code](https://claude.com/claude-code) via the bundled [skill](skill/). The skill teaches Claude the "restart Steam first" rule so you don't have to remember it yourself.

---

## Install

Prerequisites:
- Linux x64 (other platforms not yet tested — Steamworks.NET supports them but you'll need different native libs)
- .NET 8 SDK — install without sudo via `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"`, then `export PATH="$HOME/.dotnet:$PATH"`
- Running, logged-in Steam client owning the app you're publishing to

```bash
git clone https://github.com/lukegibson101/steam-workshop-uploader.git
cd steam-workshop-uploader
./fetch-deps.sh           # downloads Steamworks.NET native + managed libs
echo "<YOUR_APP_ID>" > steam_appid.txt
dotnet build
```

`fetch-deps.sh` pulls `Steamworks.NET.dll` and `libsteam_api.so` from the upstream [Steamworks.NET Standalone release](https://github.com/rlabrecque/Steamworks.NET/releases/latest) — they're not committed because Valve's redistribution terms are clearer when fetched fresh from upstream.

---

## Configure

Copy `workshop.example.json` to `workshop.json` and edit:

```json
{
  "appid": 480,
  "publishedfileid": "",
  "contentfolder": "./build",
  "previewfile": "preview.jpg",
  "visibility": 1,
  "title": "My Workshop Item",
  "description": "Steam BBCode supported.",
  "tags": ["Tag1"],
  "metadata": "",
  "language": "English",
  "filetype": "Community"
}
```

| Field | Notes |
|---|---|
| `appid` | **Required.** Your Steam App ID (e.g. `480` for Spacewar test app). Must also match `steam_appid.txt`. |
| `publishedfileid` | Leave empty for a first publish — tool calls `CreateItem` and writes the new ID back. For updates, the persisted ID is used. |
| `contentfolder` | Path to the folder whose contents become the Workshop item. Relative paths resolve from the JSON's directory. |
| `previewfile` | Path to preview JPG/PNG, **< 1 MB**. Set to `""` or omit to keep existing preview unchanged. |
| `visibility` | `0` Public, `1` Friends Only, `2` Private, `3` Unlisted. Use `1` for first-publish testing. |
| `tags` | Array of tag strings. Most apps validate these against an allow-list. |
| `language` | Optional, defaults to `"English"`. Steam uses this for localised listings. |
| `filetype` | Optional, defaults to `"Community"`. Other values: `Art`, `Microtransaction`, etc. — see [`EWorkshopFileType`](https://partner.steamgames.com/doc/api/ISteamRemoteStorage#EWorkshopFileType). Locked at item creation; can't be changed later. |

---

## Publish

```bash
dotnet run --no-build -- workshop.json "vX.Y.Z — one-line change summary"
```

Expected output:
```
appid:    480
item:     <will create new>
title:    My Workshop Item
...
submitting update...
  [    0s] preparing config
  [    1s] preparing content
  [    1s] uploading content
  [   21s] uploading content       100% 88.1 MB/88.1 MB
  [   22s] committing
SUCCESS — item NNNNNNN updated.
  https://steamcommunity.com/sharedfiles/filedetails/?id=NNNNNNN
```

After success the tool exits 0 and the new `publishedfileid` is in your `workshop.json` (if it was a create).

---

## Troubleshooting

### Hung at "preparing config" with no progress

You skipped step zero. Full `Steam → Exit`, wait 10s, relaunch, retry. The tool auto-aborts after 60s of no progress in this state with an explicit message.

If the hang persists *after* a clean Steam restart: check whether your Steam account is currently "playing" the app on another device (Family Sharing, Remote Play, another login). Exit any such session — Steam can hold UGC state across devices. Then restart Steam again and retry.

### `Unable to load shared library 'steam_api'`

`native/libsteam_api.so` isn't where the runtime expects. Re-run `./fetch-deps.sh`, then `dotnet build`.

### `EntryPointNotFoundException` on `SteamAPI_*`

Managed/native version mismatch. Both `lib/Steamworks.NET.dll` and `native/libsteam_api.so` must come from the same Steamworks.NET Standalone release. Don't mix this repo's `fetch-deps.sh` output with the Steamworks.NET NuGet package — they diverge.

### `m_bUserNeedsToAcceptWorkshopLegalAgreement` reported

Visit the item URL in a browser (logged into Steam), accept the Workshop Contributor Agreement banner, retry.

### Steam logs `Upload starting` but tool never reports `committing` and Steam log never reports `Upload finished`

Almost always the stale-Steam-state issue. Tail your Steam log to confirm:
```bash
tail -f ~/.local/share/Steam/logs/workshop_log.txt
```
If you see `Upload starting` without a matching `Upload finished` within a couple of minutes, kill the uploader, restart Steam, retry.

---

## Why this exists

Many older Steam games shipped a one-off Workshop uploader executable bundled with the game. These tools are typically Unity or .NET apps wrapping `Steamworks.NET`, frozen at whatever Steamworks SDK version was current when the game shipped. Years later they often still "work" — until they don't, and their build dependencies are too old to debug.

Symptoms that this tool can replace your game's bundled uploader:
- The official uploader hangs at "validating" / "preparing" with no error
- The uploader was Windows-only and you're on Linux/macOS
- You want to automate publishing (CI, batch updates, Claude-driven release flows)

If your game has a working publisher built into the game itself (e.g. C&C Remastered's map editor publishes maps), use that for the content types it supports — it's the most reliable path. This tool is for the cases where no such option exists.

---

## Credits

- [**Steamworks.NET**](https://github.com/rlabrecque/Steamworks.NET) by Riley Labrecque — the C# binding doing all the real work.
- **EA's MapEditor source**, shipped inside `Command & Conquer Remastered Collection`'s `SOURCECODE/` folder under GPL v3 — referenced for the exact SteamUGC setter sequence (`SetItemContent` → `SetItemPreview` → `SetItemUpdateLanguage` → `SetItemTitle` → `SetItemDescription` → `SetItemVisibility` → `SetItemTags`).
- The [2020 r/commandandconquer thread](https://www.reddit.com/r/commandandconquer/comments/h139eb/anyone_any_idea_why_a_mod_wont_upload_to_the/) whose "ran through a virgin VM and it worked" comment was the breadcrumb that led to the restart-Steam fix.

---

## License

MIT. See [LICENSE](LICENSE).
