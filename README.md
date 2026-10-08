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
emoting, collects and stacks gifts, claims PawPass rewards you have already
earned, exchanges duplicate cosmetics and emotes, and can temporarily use
supported unowned cosmetics, UI Themes, and emotes during a session. AutoCat
calls Bongo Cat's own functions directly instead of simulating mouse or
keyboard input, and it never replaces or modifies any installed Bongo Cat
file. The menu sizes itself to your display, and its accent color is yours to
pick.

## Download and run

Download `autocat.exe` from the [Releases page](https://github.com/kaankutluturk/autocat/releases/latest)
and run it. It's a single self-contained file: no installer, no separate
DLL, nothing else to place alongside it. Bongo Cat can already be running or
not; AutoCat attaches automatically either way.

## Updating

Close AutoCat and Bongo Cat, replace your `autocat.exe` with the new one, and
launch it. Your settings carry over, and files left behind by older versions
are cleaned up automatically.

## First launch and controls

AutoCat starts paused. While it is paused it does not click, emote, collect
gifts, claim PawPass rewards or exchange anything. Insta Gift is the one
exception: if it is checked, it stays armed while paused. Your checkboxes are
remembered between launches, so pressing resume starts everything you left on.
Unlock All is never remembered and is off every time AutoCat starts.

Insert shows or hides the menu. Home pauses or resumes automation. Both are
rebindable in the Misc tab: click a binding, then press the key (with or
without Ctrl, Shift, Alt or Win), mouse button or wheel direction you want, with
the menu in front. AutoCat only watches the inputs you have bound.

The menu is titled `autocat`. It has a taskbar button while it is visible, and
AutoCat keeps an icon in the notification area while it runs: click it to show
or hide the menu, or right-click it for show or hide and unload. Running
`autocat.exe` again brings the existing menu forward instead of opening a
second copy.

## The tabs

| Tab | What it does |
|---|---|
| Auto Clicker | Auto clicking (1 to 200/sec) and auto emoting (1 to 100/sec, reusable equipped emotes only, no consumables) toggle independently and share the one pause/resume button. |
| Automation | Auto Collect submits eligible normal and emote gift claims. Auto Claim PawPass claims PawPass rewards you have already earned. Auto-slot + Exchange trades real eligible duplicate cosmetics and emotes and never touches a slot you filled manually. All three wait while AutoCat is paused. |
| Experimental | Insta Gift and Unlock All, both described below. |
| Theme | Pick your own accent color with a color wheel or a hex code. |
| Misc | Key bindings, reset settings, unload, and Extreme Rates. |

**Insta Gift and Auto Collect.** Steam grants normal and emote gift tokens at
intervals that aren't defined, and each balance can accumulate up to 10. The
game also makes you wait out a timer before claiming a gift you're otherwise
eligible for. Insta Gift removes that wait; Auto Collect submits the actual
claim. Used together, they let you spend a token backlog you already earned
back to back and stack the resulting chests, instead of checking in every
few minutes. This automates existing eligibility. It doesn't manipulate
tokens.

**Unlock All.** Temporarily makes supported eligible unowned hats, skins, UI
Themes, and emotes available through the normal in-game inventory during the
current session. It does not change Steam ownership or inventory quantities,
and never removes anything you legitimately own. Turning Unlock All off or
unloading AutoCat restores normal availability and your legitimate equipment.

## Display and appearance

The menu sizes itself to your display, following both your Windows scaling
setting and your screen resolution. If you change either while AutoCat is
open, or drag the menu to a monitor with different scaling, it resizes on its
own.

The **theme** tab sets the accent color used for the logo, highlights, and
sliders. Pick one on the color wheel or type a hex code; **reset** brings back
the original. Reset Settings in the Misc tab restores it too.

## Settings and logs

Settings save automatically a moment after you change them, and again when
you hide the menu or unload. They live in `Documents\AutoCat\settings.xml`
and logs in `Documents\AutoCat\logs\`, regardless of where `autocat.exe`
runs from. Right-click the status footer to open the logs folder, copy the
current session ID, or copy a full diagnostic summary. Logs stay on your
machine and are never uploaded automatically.

## Limitations

- A gift claim still needs a real token, enough tap funds, and Steam's
  approval. AutoCat can read and spend existing tokens; it can't create or
  refill them, change their grant interval, or raise the balance cap.
- A PawPass claim needs a reward you have actually earned on a track you own.
  AutoCat can't unlock the premium pass, add progress, or claim anything the
  game would not let you claim yourself.
- Extreme Rates raises the values you can request, not what the game can
  actually deliver.
- A Bongo Cat update may require a matching AutoCat release.
- A key press shorter than about 15 milliseconds, as some macro tools send, can
  be missed by a binding.
- Resizing while open needs Windows 10 version 1703 or later. On older
  Windows the menu is sized once, when AutoCat starts.

## Reporting a problem

Attach the session log from `Documents\AutoCat\logs\`, note roughly when it
happened and what was enabled, and open an issue on GitHub. Check the log
yourself before sharing it.

## Building from source

You need 64-bit Windows with Windows PowerShell and the .NET Framework 4
compiler (`csc.exe`, part of Windows), plus an installed copy of Bongo Cat,
whose game assemblies the runtime component is compiled against. From the
repository folder:

```powershell
.\build.ps1
```

Pass `-GameDirectory` if Bongo Cat is not in the default Steam location. The
build writes `bin\autocat.exe`, with the runtime component embedded in it.
`.\package.ps1` checks the executable, copies it to `outputs\` (or to the
folder you give with `-OutputDirectory`) and prints its SHA-256.

**Running the tests.** None of these start Bongo Cat or attach to it, and none
contacts the internet. `--ui-test` needs an interactive Windows desktop. Run
them from the repository folder after building:

```powershell
bin\autocat.exe --self-test C:\full\path\self-test.txt
bin\autocat.exe --ui-test C:\full\path\ui-test.txt
experiments\Test-RuntimeLogic.ps1
experiments\diagnostics\Test.ps1
experiments\diagnostics\Test-WorkerDiagnostics.ps1
experiments\Test-UnlockSession.ps1
experiments\Test-PawPass.ps1
experiments\Test-Packaging.ps1
```

`--self-test` and `--ui-test` write PASS lines (or a FAIL line and a non-zero
exit code) to the file you name, which should be a full path. The PowerShell
suites write their build products and results to `test-output\`, which git
ignores; each ends with an error if anything fails. They compile small stand-ins
for the game's types, so they check AutoCat's own logic, not how the real game
behaves. `Test-Packaging.ps1` needs the built `bin\autocat.exe`.

---

<div align="center">

Licensed under [MIT](LICENSE).

</div>
