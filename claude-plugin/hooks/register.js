// Ringside's Claude Code mod.
//
// After each turn, and whenever a plan limit moves a whole point, Claude Code hands mods the
// same 5-hour and weekly limits its status line gets. This mod writes them to the small file
// the Ringside meter reads, so the meter stays current in every place Claude Code runs: the
// terminal, the Claude desktop app's Code tab and the VS Code extension.
//
// It reads no credentials, calls no network and touches no other file. It reads two
// environment variables to place that file: HOME and CLAUDE_CONFIG_DIR (which account's folder).

let last = ''
let lastAt = 0

// One feed file per Claude Code config folder, so two Claude accounts kept in two folders
// never write over each other's limits. No CLAUDE_CONFIG_DIR, or the default ~/.claude, keeps
// the original name; ~/.claude-company gives claude-statusline-claude-company.json.
// Same rule as StatusLineBridge.FeedFileFor; a test holds them together.
function feedFile(configDir, home) {
  const plain = 'claude-statusline.json'
  const clean = (configDir || '').trim().replace(/[\\/]+$/, '')
  if (!clean || clean === `${(home || '').replace(/[\\/]+$/, '')}/.claude`) return plain
  const name = clean.split(/[\\/]/).pop().replace(/^\.+/, '').toLowerCase()
  const slug = name.replace(/[^a-z0-9-]+/g, '-').replace(/^-+|-+$/g, '')
  return !slug || slug === 'claude' ? plain : `claude-statusline-${slug}.json`
}

async function feedPath($, file) {
  const home = await $.env.get('HOME')
  const custom = await $.env.get('RINGSIDE_DATA')
  if (custom) return `${custom}/${file}`
  const appData = await $.env.get('APPDATA')
  if (appData) return `${appData}\\Ringside\\${file}`
  if (!home) return null
  if (await $.fs.exists(`${home}/Library/Application Support`)) return `${home}/Library/Application Support/Ringside/${file}`
  const xdg = await $.env.get('XDG_CONFIG_HOME')
  return `${xdg || `${home}/.config`}/Ringside/${file}`
}

async function publish($, rateLimits) {
  const windows = {}
  for (const w of rateLimits ?? []) {
    if (w.kind !== 'five_hour' && w.kind !== 'seven_day') continue
    const item = { used_percentage: Math.round(w.percentUsed * 100) / 100 }
    const resets = w.resetsAt ? Date.parse(w.resetsAt) : NaN
    if (!Number.isNaN(resets)) item.resets_at = Math.floor(resets / 1000)
    windows[w.kind] = item
  }
  if (Object.keys(windows).length === 0) return
  const shape = JSON.stringify(windows)
  const now = await $.clock.now()
  // The same reading again only refreshes the time, at most once a minute.
  if (shape === last && now - lastAt < 60_000) return
  last = shape
  lastAt = now
  // "feed" names the file this reading is for, so Ringside can turn away readings that an older
  // copy of this mod, still loaded somewhere, writes to the wrong account's file.
  const file = feedFile(await $.env.get('CLAUDE_CONFIG_DIR'), await $.env.get('HOME'))
  const path = await feedPath($, file)
  if (path) await $.fs.write(path, JSON.stringify({ updated: Math.floor(now / 1000), feed: file, ...windows }))
}

export function register(on) {
  on('session.measure', async ($, e, next) => {
    try {
      await publish($, e.rateLimits)
    } catch {
      // A meter must never get in the way of the session.
    }
    return next(e)
  })
}
