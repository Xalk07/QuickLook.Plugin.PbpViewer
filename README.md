# QuickLook.Plugin.PbpViewer  [![GitHub all releases](https://img.shields.io/github/downloads/xalk07/QuickLook.Plugin.PbpViewer/total)](https://github.com/xalk07/QuickLook.Plugin.PbpViewer/releases)
Plugin for [QuickLook](https://github.com/QL-Win/QuickLook), allowing to preview `.pbp` file. (`EBOOT.PBP`). Used in Sony PSP, PS3 consoles.

AI-assisted using Grok, ChatGPT, Gemini; human-verified.

## Features 

- Supports displaying up to 4 images from a file.
- Correct playing `SND0.AT3` music file and `ICON1.PMF` video clip without converting to another file formats. Reading dircetly from `.pbp` file. 
- Audio plays only when you click the music icon.
- Ability to detect FakeNP games.

The `BOOT.PNG` file may be displayed in PSP demos, Minis, and official PS1 games, but this has not been tested.

## Screenshots 
![PS1-PSP game preview with all 4 images.](Preview%20images/ps1-psp.PNG)
PS1-PSP game preview with all 4 images.


![Homebrew preview with black theme.](Preview%20images/Homebrew-black.PNG)
Homebrew preview with black theme.

![FakeNP game preview with sound enabled.](Preview%20images/Game,fakenp,sound.PNG)
FakeNP game preview with sound enabled.

## Download and Installation
1. Go to [Release page](https://github.com/xalk07/QuickLook.Plugin.PbpViewer/releases) and download the latest version.
2. Make sure that you have QuickLook running in the background. Press `Spacebar` on the downloaded `.qlplugin` file.
3. Click the `Install` button in the popup window.
4. Restart QuickLook.

## Development
1. Clone repo and sub-modules
2. Build project with Release profile.
3. Run `Scripts\pack-zip.ps1`
4. Find plugin `QuickLook.Plugin.ApkViewer.qlplugin` in the project directory.

## Thanks to

- Author of [QuickLook.Plugin.ApkViewer](https://github.com/canheo136/QuickLook.Plugin.ApkViewer) for the inspiration and for using the code as a basis for the project.
- Author of [LightAT3](https://github.com/unknowall/LightAT3) for its practically functional AT3 codec. And for video codec. 
- Our entire friendly PSP community.

## License
 &nbsp;&nbsp;&nbsp;&nbsp;**GPL-3.0**
