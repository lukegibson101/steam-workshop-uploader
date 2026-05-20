---
name: publish-to-steam-workshop
description: Publish or update an item on the Steam Workshop via the steam-workshop-uploader CLI tool. Use when the user asks to publish a mod/UGC to Steam Workshop, ship a new release, update a Workshop item, or run the uploader. Works against any Steam app with Workshop support — AppID comes from the manifest. Linux-native; tool calls SteamUGC directly via Steamworks.NET.
---

# publish-to-steam-workshop

You are about to publish content to Steam Workshop using the [steam-workshop-uploader](https://github.com/lukegibson101/steam-workshop-uploader) CLI tool. Follow this procedure carefully — it encodes hours of dead-end debugging.

## The ONE rule

**Always remind the user to fully restart Steam before publishing.** Not just close the window — `Steam → Exit` from the menu bar, wait 10s, relaunch.

Without this, SteamUGC silently hangs at "preparing config" with no error. This applies to every standalone Workshop uploader on every Steam app; it's a stale-cache issue in the running Steam client, not a bug in this tool. The tool will auto-detect and abort after 60s of no progress, but pre-empt it: ask the user to restart Steam first.

## Inputs you need from the user

Before doing anything, confirm or gather:
1. **Tool location** — where they cloned `steam-workshop-uploader`. If unknown, ask. Common: `~/Documents/development/steam-workshop-uploader/` or `~/src/steam-workshop-uploader/`.
2. **Manifest location** — path to their `workshop.json`. Usually inside a per-project folder (e.g. `<their-project>/tools/workshop-uploader/workshop.json`), NOT inside the uploader tool's own directory. The tool reads any JSON, content paths in the manifest resolve relative to the manifest's directory.
3. **Change note** — one-line summary of what's changing. Required, per submission.

If publishing a brand-new item (no existing `publishedfileid` in the manifest), confirm they're OK with the tool creating a new Workshop shell — the new ID will be persisted back to the manifest.

## Pre-flight checks

Run in this order, fix issues before proceeding:

1. **Confirm tool is built**: `ls <tool-dir>/bin/Debug/net8.0/SteamWorkshopUploader.dll`. If missing, run `cd <tool-dir> && ./fetch-deps.sh && dotnet build`.
2. **Confirm Steam process exists**: `pgrep -af steam | grep -v grep`. If not running, ask user to launch Steam.
3. **Confirm `steam_appid.txt` matches the manifest's `appid`**:
   - `cat <tool-dir>/steam_appid.txt` and compare with `jq -r .appid <manifest-path>`.
   - If mismatched: rewrite `steam_appid.txt` to match. Steam silently refuses cross-app calls if these disagree.
4. **Sanity-check content and preview paths exist**: resolve them relative to the manifest dir and `ls` them.
5. **Check the content folder structure matches the target game's convention**:
   - Some games (e.g. C&C Remastered, App 1213210) require the mod files to live in a NAMED SUBFOLDER inside the uploaded content (`<contentfolder>/<ModName>/ccmod.json`), not at the root. Subscribers' clients download fine but the in-game mod scanner never finds the mod. Verify against other subscribed mods of the same app on the user's machine before publishing.
   - **Never use symlinks** for the wrapper subfolder — SteamUGC preserves symlinks AS symlinks in the depot, and other subscribers fail to install with "Disk write failure" when their client tries to materialise them. Use `rsync -a --delete <build>/ <wrapper>/<ModName>/` or `cp -aL` to produce a real directory.

## The Steam restart prompt

Before running the uploader, present this verbatim to the user:

> Before I run the uploader: **have you fully restarted Steam recently** (within the last few minutes)? Specifically: `Steam → Exit` from Steam's menu bar (not just close window), wait ~10s, relaunch, log in if prompted.
>
> Skip this and the upload will hang silently with no error. The tool will auto-abort after 60s but it's faster to pre-empt. Confirm you've done it, then I'll fire the upload.

Wait for explicit confirmation. Don't run the uploader until the user has confirmed.

## Running the upload

```bash
cd <tool-dir>
export PATH="$HOME/.dotnet:$PATH"
dotnet run --no-build -- <manifest-path> "<change-note>"
```

Run as a background bash task (`run_in_background: true`) so progress notifications stream in. Don't poll the output file in a tight loop — just wait for the completion notification or for the user to interrupt.

Tail Steam's own log too if the user wants verification:
```bash
tail -f ~/.local/share/Steam/logs/workshop_log.txt
```
A successful upload writes `Upload finished for workshop item <id> : OK`.

## After success

Report back to the user:
- Workshop item URL: `https://steamcommunity.com/sharedfiles/filedetails/?id=<id>`
- Visibility setting (1 = Friends-Only, etc.) — remind them to flip to Public via the Workshop website's Owner Controls panel after they've self-tested
- For incremental updates, Steam may report `No content change detected` in the log — that's correct behaviour, it diffs by content hash

## Common failure modes

| Symptom | Cause | Fix |
|---|---|---|
| Hangs at "preparing config" >60s | Stale Steam state | Fully restart Steam, retry |
| `SteamAPI.Init() failed` | Steam not running, or not logged in to the account that owns the app | Launch Steam, log in, retry |
| `Unable to load shared library 'steam_api'` | `native/libsteam_api.so` missing | `cd <tool-dir> && ./fetch-deps.sh` |
| `EntryPointNotFoundException` on `SteamAPI_*` | Mixed managed/native versions | Re-run `fetch-deps.sh` to get matched pair |
| `m_bUserNeedsToAcceptWorkshopLegalAgreement` reported | First-time publishing under this account | User visits item URL in browser, accepts the banner, retry |
| `CreateItem FAILED — EResult.k_EResultAccessDenied` | Account doesn't own the app, or app doesn't have Workshop enabled | Verify ownership and Workshop is enabled for the AppID |
| Item created but content shows 0 B on Workshop page | Submit step failed silently or was killed | Re-run with same manifest — `publishedfileid` is persisted, no new shell will be created |

## Don't-dos

- Don't run the uploader without prompting the user to restart Steam first.
- Don't loop-retry on a hang — kill and surface the issue. Hangs are diagnostic, not transient.
- Don't modify the manifest's `publishedfileid` after the tool has persisted it; that's the immutable item ID.
- Don't suggest fixes like "try a different EWorkshopFileType" or "run from the game install dir" — those have been ruled out experimentally for the hang symptom. The fix is always: restart Steam.
- Don't create multiple new item shells trying to fix a hang. Empty shells accumulate on the user's Workshop profile and clutter it.
- Don't claim the upload succeeded based on the tool's stdout alone — also confirm via Steam's `workshop_log.txt` showing `Upload finished : OK`, or by fetching the Workshop page to confirm File Size > 0.
