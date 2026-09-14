<div align="center">
<img src="assets/autocat.png" width="120" alt="AutoCat">

# AutoCat

**A compact automation helper for Bongo Cat.**

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)
[![Latest release](https://img.shields.io/github/v/release/kaankutluturk/autocat)](https://github.com/kaankutluturk/autocat/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/kaankutluturk/autocat/total)](https://github.com/kaankutluturk/autocat/releases/latest)

</div>

---

AutoCat is a compact Windows utility for Bongo Cat. It automates clicking and
emoting, collects and stacks gifts, exchanges duplicate cosmetics and emotes,
and can temporarily preview unowned cosmetics and emotes during a session. It
calls Bongo Cat's own functions directly instead of simulating mouse or
keyboard input, and it never replaces or modifies any installed Bongo Cat
file.

## Download and run

Download `autocat.exe` from the [Releases page](https://github.com/kaankutluturk/autocat/releases/latest)
and run it. It's a single self-contained file: no installer, no separate
DLL, nothing else to place alongside it. Bongo Cat can already be running or
not; AutoCat attaches automatically either way.

## First launch and controls

AutoCat starts paused with every feature disabled. Insert shows or hides the
menu. Home pauses or resumes automation. Both are rebindable in the Misc
tab. There's no tray icon; running `autocat.exe` again brings the existing
menu forward instead of opening a second copy.

## The tabs

| Tab | What it does |
|---|---|
| Auto Clicker | Auto clicking (1 to 200/sec) and auto emoting (1 to 100/sec, reusable equipped emotes only, no consumables) toggle independently and share one pause/resume state. |
| Automation | Auto Collect submits eligible normal and emote gift claims. Auto-slot + Exchange trades real eligible duplicate cosmetics and emotes and never touches a slot you filled manually. |
| Experimental | Insta Gift and Unlock All, both described below. |
| Misc | Key bindings, save/reset settings, unload, and Extreme Rates. |

**Insta Gift and Auto Collect.** Steam grants normal and emote gift tokens at
intervals that aren't defined, and each balance can accumulate up to 10. The
game also makes you wait out a timer before claiming a gift you're otherwise
eligible for. Insta Gift removes that wait; Auto Collect submits the actual
claim. Used together, they let you spend a token backlog you already earned
back to back and stack the resulting chests, instead of checking in every
few minutes. This automates existing eligibility. It doesn't manipulate
tokens.

**Unlock All.** Temporarily adds unowned cosmetics and emotes to the running
session, selected through the normal in-game inventory. It doesn't
permanently add anything to your account or Steam inventory, and it never
removes anything you legitimately own. Temporary items disappear when you
disable Unlock All, unload AutoCat, or restart Bongo Cat.

## Settings and logs

Settings are saved to `Documents\AutoCat\settings.xml` and logs to
`Documents\AutoCat\logs\`, regardless of where `autocat.exe` runs from.
Right-click the status footer to open the logs folder, copy the current
session ID, or copy a full diagnostic summary. Logs stay on your machine and
are never uploaded automatically.

## Limitations

- A gift claim still needs a real token, enough tap funds, and Steam's
  approval. AutoCat can read and spend existing tokens; it can't create or
  refill them, change their grant interval, or raise the balance cap.
- Extreme Rates raises the values you can request, not what the game can
  actually deliver.
- A Bongo Cat update may require a matching AutoCat release.

## Reporting a problem

Attach the session log from `Documents\AutoCat\logs\`, note roughly when it
happened and what was enabled, and open an issue on GitHub. Check the log
yourself before sharing it.

## Building from source

Build with `./build.ps1` in Windows PowerShell, on 64-bit Windows with the
.NET Framework compiler and an installed Bongo Cat. Pass `-GameDirectory`
for a non-default install location. `--self-test` and `--ui-test` cover the
core logic without needing the game running.

---

<div align="center">

Licensed under [MIT](LICENSE).

</div>
