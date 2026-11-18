# <img src="docs/Images/AppLogo.png" width="32"> Sine Player
Sine Player is a media player for Windows, written in C# with WinUI 3. It plays video and audio with subtitle rendering, and manages a local media library with folders, collections, tags, favorites, play queue, playback history and search.

Sine Player irregularly ships with new features and bug fixes. You can get the latest version of Sine Player in the [Microsoft Store (TODO)](https://www.microsoft.com/store/apps/9NS9FG32DCP5).

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
- Download the native libraries (they are not in the repository): [link](https://github.com/aicd0/SinePlayer/releases/download/v0.0.0/Native.zip).
  Extract the `Native` folder directly into `src/SinePlayer.Core`.
- Open [SinePlayer.sln](src/SinePlayer.sln) in Visual Studio to build and run the Sine Player app.

## Contributing
If Sine Player is not working properly, you can [submit an issue on GitHub](https://github.com/aicd0/SinePlayer/issues/new/choose). If you know how to fix an issue, it is also encouraged to create a [pull request](https://github.com/aicd0/SinePlayer/pulls) for it.

## License
Licensed under the [GNU General Public License v3.0 or later](LICENSE.md).
