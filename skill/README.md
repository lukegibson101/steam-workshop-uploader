# Claude skill for steam-workshop-uploader

This tool has no UI — it's designed to be driven by [Claude Code](https://claude.com/claude-code). This folder contains a skill that teaches Claude how to use it correctly, including the "restart Steam first" gotcha.

## Install

```bash
# From the cloned repo root
mkdir -p ~/.claude/skills/publish-to-steam-workshop
cp skill/SKILL.md ~/.claude/skills/publish-to-steam-workshop/SKILL.md
```

Restart Claude Code (or start a new session). The skill becomes available — invoke it with `/publish-to-steam-workshop` or just ask Claude to "publish my mod to Steam Workshop" and it'll pick up the skill from the description match.

## What the skill does

When invoked, Claude:

1. Asks for / confirms the tool location, manifest location, and change note
2. Runs pre-flight checks (binary built, Steam running, `steam_appid.txt` matches manifest)
3. **Prompts you to fully restart Steam** before running (load-bearing — without it the upload silently hangs)
4. Runs the uploader in background, streams progress
5. On success, reports the Workshop URL and reminds you to flip visibility to Public after self-test

## Why this exists

The tool itself is fully scriptable. But Workshop publishing has one gotcha that's caused community-wide grief for years (silent hang at "preparing config" without a Steam restart). A skill is the right place to encode that procedural wisdom so it gets surfaced every time, not just when you remember to read the README.
