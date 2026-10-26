# <img src="docs/Images/AppLogo.png" width="32"> Sine Player
The Sine Player is a modern Windows app written in C#. The app provided basic functionality for comic reading, along with some common utilities such as file management, searching, tagging, rating, etc.

Sine Player irregularly ships with new features and bug fixes. You can get the latest version of Sine Player in the [Microsoft Store](https://www.microsoft.com/store/apps/9NS9FG32DCP5).

![Sine Player Screenshot](docs/Images/SinePlayerScreenshot.png)

## Development
Prerequisites:
- Your computer must be running Windows 10, version 22H2 or newer. Windows 11 is recommended.
- Install the latest version of [Visual Studio](https://visualstudio.microsoft.com/downloads/) (the free community edition is sufficient).
  - Install the ".NET desktop development" and "WinUI application development" workloads.
  - Install the latest Windows 11 SDK.
- Install the [XAML Styler](https://marketplace.visualstudio.com/items?itemName=TeamXavalon.XAMLStyler2022) Visual Studio extension.
- Get the code:
    ```
    git clone git@github.com:aicd0/SinePlayer.git
    ```
- Download the FFmpeg native libraries for the architectures you build (they are not in the repository):
  - x64: `https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-25-13-00/ffmpeg-N-128094-g31855b2a70-win64-gpl-shared.zip`
    Copy `avcodec-63.dll`, `avdevice-63.dll`, `avfilter-12.dll`, `avformat-63.dll`, `avutil-61.dll`, `swresample-7.dll` and `swscale-10.dll` from the archive's `bin` folder into `src/SinePlayer.Core/native/win-x64`.
  - ARM64: `https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-25-13-00/ffmpeg-N-128094-g31855b2a70-winarm64-gpl-shared.zip`
    Copy the same seven files from the archive's `bin` folder into `src/SinePlayer.Core/native/win-arm64`.
- Open [SinePlayer.sln](src/SinePlayer.sln) in Visual Studio to build and run the Sine Player app.

## Plugins
Sine Player can be extended with plugins (written in C#). See [Sine Player Plugins](https://github.com/aicd0/SinePlayerPlugins) for more information.

## Contributing
If Sine Player is not working properly, you can [submit an issue on GitHub](https://github.com/aicd0/SinePlayer/issues/new/choose). If you know how to fix an issue, it is also encouraged to create a [pull request](https://github.com/aicd0/SinePlayer/pulls) for it.

## License
Licensed under the [GNU General Public License v3.0 or later](LICENSE.md).
