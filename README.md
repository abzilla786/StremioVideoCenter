# Zino's Media Center

**Zino's Media Center** is a media-browsing and playback extension for [Playnite](https://playnite.link/). It brings movies, TV shows, anime, and local media together in one interface, with support for compatible Stremio add-ons and external MPV playback.

## Features

- Browse movies, TV shows, and anime.
- Discover catalogue information and streams through compatible add-ons.
- Search supported catalogues.
- Browse and play local media.
- Track your list and viewing history.
- Play media using MPV.
- Use supported subtitle sources and playback controls.
- Optionally configure Real-Debrid through compatible add-ons.

Available catalogues, streams, subtitles, and playback options depend on the add-ons and services configured by the user.

## Requirements

- Windows
- [Playnite](https://playnite.link/) — developed and targeted against Playnite 10.62
- An internet connection for online catalogues, add-ons, and streams

MPV is used for media playback. Compatible add-ons or local media files provide the content sources.

## Installation

Download and install the extension using the instructions provided with its published release package:

[Zino's Media Center releases](https://github.com/abzilla786/StremioVideoCenter/releases)

## Getting started

1. Install the extension using the release instructions.
2. Open Playnite and launch **Zino's Media Center**.
3. Browse Movies, TV Shows, Anime, or Local Media.
4. Configure the add-ons you want to use.
5. Select a title and choose an available stream or local file.

## Add-ons and external services

Zino's Media Center integrates with compatible Stremio add-ons for catalogue information and stream sources. Each add-on may offer different content and functionality.

Only configure add-ons and services you trust. Their availability, terms, and privacy practices are governed by their respective operators.

### Real-Debrid

Real-Debrid is optional. Where supported, it can be configured through a compatible add-on. It is not required for general browsing.

## Local media and playback

Use the Local Media area to browse supported media on your computer. Ensure that the relevant folders are accessible to your Windows account and that the files are supported by the bundled MPV player.

Playback controls depend on the current playback view and input device. Supported controls include common playback actions, seeking, short skips, volume adjustment, and subtitle selection where available.

## Privacy

The extension may contact online catalogue providers, configured add-ons, subtitle services, and stream providers. These services may receive requests and associated information, such as search terms, content identifiers, IP addresses, or credentials and tokens you choose to configure.

Review the privacy policies and terms of the services you use. Do not share logs publicly without checking them for private information or credentials.

## Troubleshooting

**No catalogue results appear**
- Check your internet connection.
- Check that the relevant add-on is installed and configured.
- Try again later if a third-party service is unavailable.

**No streams are available**
- Try another title or source.
- Check the add-on's configuration and availability.
- If using Real-Debrid, verify its configuration with the relevant add-on.

**Playback does not start**
- Try a different stream or local file.
- Confirm that MPV is present in the installed package.
- Check whether security software is blocking the player or its connection.

To report an issue, include the steps to reproduce it and relevant logs after removing private information and credentials.

## Development

Zino's Media Center is a C# and WPF extension for Playnite.

- **Source code:** https://github.com/abzilla786/StremioVideoCenter
- **Issues:** https://github.com/abzilla786/StremioVideoCenter/issues
- **Releases:** https://github.com/abzilla786/StremioVideoCenter/releases

Contributions should be focused and must not include local configuration, credentials, private logs, or unrelated build output.

## Licence and acknowledgements

The project's original code and documentation are licensed under the MIT License, as stated in [`LICENSE`](LICENSE), to the extent the project owner has the right to license that material.

MPV and other third-party components retain their respective licences. The project's MIT licence does not replace or override those licences. See [`Licence.md`](Licence.md) and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

Zino's Media Center is an independent community project and is not an official product of or endorsed by Playnite, MPV, or Stremio.

## Licensing and third-party software

The project includes MPV and other third-party components, which are distributed under their respective licences. Those components are not all covered by the licence for the project's original code.

- See [`LICENSE`](LICENSE) for the project's original material, subject to the final release licence confirmation.
- See [`mpv/LICENSE-mpv.txt`](mpv/LICENSE-mpv.txt) for MPV-related licensing information, if included in the release.
- See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) and the accompanying third-party licence materials for additional notices, when supplied with the release.
- See [`mpv/MPV_REDISTRIBUTION.md`](mpv/MPV_REDISTRIBUTION.md) for the current MPV redistribution notes, if present in the repository.

## Credits

- [Playnite](https://playnite.link/) — game library and extension platform.
- [MPV](https://mpv.io/) — media player.
- [Stremio](https://www.stremio.com/) and compatible add-on developers — ecosystem and integrations used by configured add-ons.

Zino's Media Center is an independent community project and is not an official product of Playnite, MPV, or Stremio.
