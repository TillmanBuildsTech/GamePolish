---
id: 402
title: Registry repo and seed data
phase: 4
bucket: mvp
depends_on: []
blocks: [501]
status: todo
area: docs
---

# 402 — Registry repo and seed data

## Objective

The public repo `game-polish/registries` exists and serves `octowow.json` at
`https://raw.githubusercontent.com/game-polish/registries/main/octowow.json` — the exact URL
`games/octowow.yaml` declares as `registry_url` — with 23 schema-valid mod entries from the design note's
two seed tables, plus a validator so a malformed registry fails in the registries repo, not in the app.

## Context

The registry is the discovery layer: the app fetches and caches it, and Discover (305, full 501) renders from
it. Task **211** consumes the format — read it first. Schema: `CLAUDE.md` → "Registry JSON Schema (draft)"
(`game_id`, `updated_at`, `mods[]`, and per entry `id`, `name`, `author`, `description`, `source`, `tags`,
`thumbnail`). Seed data: the two tables in `../game-polish-notes/docs/games/octowow.md` "Seed Registry" — 18
from OctoUpdater's `RECOMMENDED_ADDONS` plus 5 OctoWoW-specific finds. All 23 sources are GitHub repos and
the note is authoritative for the URLs.

Verified 2026-10-15: `gh repo view game-polish/registries` → `Could not resolve to a Repository`; `gh api
orgs/game-polish` → 404 and the authed account `Tillman32` is in `MorphStack`, `M-Wolf-Media-Marketing`,
`TillmanBuildsTech`, not `game-polish`; the raw URL 404s; `jq` is not installed (Python 3.14.6 is — write
the validator in Python stdlib); the local repo has no git remote. **Do not dodge this by publishing elsewhere** — the descriptor hard-codes `game-polish/registries`, and
creating the org (or being invited to it) is a human action: stop and ask.

## Scope

- Settle the owner, then create the repo if absent: public, default branch `main`.
- `octowow.json` at the repo root: `game_id: "octowow"`, one real `updated_at`, all 23 entries.
- A short `README.md` (schema, raw URL) and `scripts/validate_registry.py`, run by a GitHub Action.
- The commands below run, with their real output pasted into the report.

## Out of scope

- `games/octowow.yaml` (401) — this task must only agree with its `registry_url`.
- Fetch, cache and offline handling (211); the Discover UI (305/501); a second game (607).
- Any C# change: if the app cannot consume this file, report a 211 bug instead of reshaping the schema.

## Implementation notes

```bash
gh repo view game-polish/registries                       # resolves? done, skip creation
gh api user/orgs --jq '.[].login' | grep -x game-polish   # member? then you may create
gh repo create game-polish/registries --public --description "Game Polish mod registries — one JSON file per supported game"
```

If the first succeeds there is nothing to create. If the second prints nothing, **stop and ask a human** for
the org or an invite — never pick a different owner. Then confirm
`gh repo view game-polish/registries --json visibility,defaultBranchRef` reports `PUBLIC` and `main`; the raw
URL 404s until the first push to `main`.

The 23 entries: `id` is the lowercase addon-folder name, `author` the owner in the URL, tags a first-pass
guess from a controlled vocabulary (`ui`, `map`, `quest`, `loot`, `bag`, `combat`, `stats`, `gear`, `class`,
`utility`, `library`, `settings`, `tweaks`, `leveling`, `instances`, `inventory`, `auction`, `meter`,
`comparison`, `talents`, `paladin`, `auras`, `database`, `essential`, `octowow`).

| id | name | source repo | author | tags |
|---|---|---|---|---|
| `atlasloot` | AtlasLoot | https://github.com/Otari98/AtlasLoot | Otari98 | loot, database |
| `aux-addon` | aux-addon | https://github.com/OldManAlpha/aux-addon | OldManAlpha | utility, auction |
| `bettercharacterstats` | BetterCharacterStats | https://github.com/pepopo978/BetterCharacterStats | pepopo978 | ui, stats |
| `doiteauras` | DoiteAuras | https://github.com/deceius/DoiteAuras | deceius | ui, auras |
| `flighttracker` | FlightTracker | https://github.com/Lexxoi/FlightTracker | Lexxoi | map, utility |
| `instancejournal` | InstanceJournal | https://github.com/Arthur-Helias/InstanceJournal | Arthur-Helias | instances |
| `itemrack` | ItemRack | https://github.com/Otari98/ItemRack | Otari98 | gear, utility |
| `levelrange-octo` | LevelRange-Octo | https://github.com/Dusk-92/LevelRange-Octo | Dusk-92 | ui, leveling |
| `magnify` | Magnify | https://github.com/paokkerkir/Magnify | paokkerkir | ui, utility |
| `modernmapmarkers` | ModernMapMarkers | https://github.com/tilare/ModernMapMarkers | tilare | map |
| `nampowersettings` | NampowerSettings | https://github.com/Dusk-92/NampowerSettings | Dusk-92 | settings, utility |
| `pallypowertw` | PallyPowerTW | https://github.com/ShikawaLePaladin/PallyPowerTW | ShikawaLePaladin | class, paladin |
| `pfquest` | pfQuest | https://github.com/The-Kludge-Bureau/pfQuest | The-Kludge-Bureau | quest, map, essential |
| `pfquest-turtle` | pfQuest-turtle | https://github.com/KameleonUK/pfQuest-turtle | KameleonUK | quest, map |
| `pfui` | pfUI | https://github.com/brues-code/pfUI | brues-code | ui, essential |
| `shagudps` | ShaguDPS | https://github.com/shagu/ShaguDPS | shagu | combat, meter |
| `succ-bag` | SUCC-bag | https://github.com/Otari98/SUCC-bag | Otari98 | bag, inventory |
| `superapi` | SuperAPI | https://github.com/balakethelock/SuperAPI | balakethelock | library |
| `pfquest-octo` | pfQuest-octo | https://github.com/paokkerkir/pfQuest-octo | paokkerkir | quest, map, octowow |
| `octopawn` | OctoPawn | https://github.com/iGreed1993/OctoPawn | iGreed1993 | gear, comparison |
| `octospec` | OctoSpec | https://github.com/iGreed1993/OctoSpec | iGreed1993 | class, talents |
| `shagutweaks` | ShaguTweaks | https://github.com/paokkerkir/ShaguTweaks | paokkerkir | ui, tweaks |
| `shagutweaks-extras` | ShaguTweaks-extras | https://github.com/paokkerkir/ShaguTweaks-extras | paokkerkir | ui, tweaks |

- **ids are permanent** — `manifest.json` keys on them, so renaming after publication orphans installs.
  Verified: for all 23 entries the id equals both the lowercase addon-folder name and the lowercase last
  segment of `source`, so the engine can derive the destination folder from `source`; if that ever breaks,
  the schema needs a `folder` field — raise it with 211.
- **descriptions and thumbnails are guesses** needing review by someone who plays OctoWoW. Use
  `https://opengraph.githubassets.com/1/<owner>/<repo>`, or omit `thumbnail` if 211 treats it as optional.
- **`updated_at` must be honest**: UTC, ISO-8601 `Z`, the time of the last real content change
  (`git log -1 --format=%cI -- octowow.json`). Never bump it from CI on a schedule; there is no content.
- Validator failures, all hard: unparseable JSON; `game_id` not matching the file name; `updated_at` not UTC;
  `mods` empty; a missing field; a duplicate or non-kebab-case id; a source that is not
  `https://github.com/<owner>/<repo>`; empty tags; the folder-name rule broken.
- `raw.githubusercontent.com` caches for minutes, so a stale body right after a push is expected — re-check
  with `curl -s "$RAW?cb=$(date +%s)"`, and never "fix" the file because of it.

## Acceptance criteria

- `gh repo view game-polish/registries --json visibility,defaultBranchRef` reports public + `main`.
- Fetching the raw URL returns valid JSON: `game_id == "octowow"`, a UTC `updated_at`, exactly 23 entries.
- Every `source` is a GitHub URL, every `id` unique and kebab-case, every `author` the owner in `source`.
- `scripts/validate_registry.py` exits 0 on the committed file and non-zero on a corrupted copy (both runs
  pasted); the curl check prints 23 entries, unique kebab-case ids, GitHub-only sources, the folder-name rule.
- The raw URL in that repo's README and `registry_url` in `games/octowow.yaml` are byte-identical.

## Verification

```bash
RAW=https://raw.githubusercontent.com/game-polish/registries/main/octowow.json
python scripts/validate_registry.py octowow.json && echo VALIDATOR_OK
curl -s "$RAW" | python -c "import json,sys,re;d=json.load(sys.stdin);m=d['mods'];ids=[x['id'] for x in m];assert d['game_id']=='octowow' and len(m)==23,(d['game_id'],len(m));assert len(set(ids))==23 and all(x['source'].startswith('https://github.com/') for x in m);assert all(re.fullmatch(r'[a-z0-9]+(-[a-z0-9]+)*',i) for i in ids);assert all(x['id']==x['source'].rsplit('/',1)[-1].lower() for x in m);print('registry ok:',len(m),'mods')"
```

## References

- `../game-polish-notes/docs/games/octowow.md` — "Seed Registry" (both tables)
- `CLAUDE.md` — "Registry JSON Schema (draft)"; `.todo/000-CONVENTIONS.md` — invariant 5
- Tasks: 401, 211, 305, 501, 607 — blocks 501
