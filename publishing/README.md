# Publishing to Playnite's add-on browser

Two files make Play Next show up in Playnite's built-in add-on browser (with auto-updates):

- [`../installer.yaml`](../installer.yaml): the **installer manifest**. It lists every released version and where to download its `.pext`. It lives in this repo, so it must be updated with each release.
- [`PlayNext_67c5a910-8da6-44c2-bd79-88b4c171c113.yaml`](PlayNext_67c5a910-8da6-44c2-bd79-88b4c171c113.yaml): the **add-on manifest**. It's submitted once to [PlayniteAddonDatabase](https://github.com/JosefNemec/PlayniteAddonDatabase) under `addons/generic/`.

## Current status (November 2026)

New submissions to PlayniteAddonDatabase are **on hold**. A new add-on portal is planned with Playnite 11 and will be backported to Playnite 10. Until then, only long-standing community members can submit, by contacting "Crow" on Playnite's Discord. In the meantime, people can install Play Next from the [GitHub releases](https://github.com/ahar2611/PlayNext/releases).

## Releasing a new version

1. Bump `Version` in `src/extension.yaml` and `<Version>` in `src/PlayNext.csproj`.
2. Add the changes to `CHANGELOG.md`, and add a new entry at the **top** of `installer.yaml` with the matching `PackageUrl`.
3. Commit, then tag and push: `git tag v1.2.1 && git push origin v1.2.1`.
4. GitHub Actions builds `PlayNext_67c5a910-8da6-44c2-bd79-88b4c171c113_1_2_1.pext` and attaches it to a **draft** release. Review it on GitHub and publish it.

`RequiredApiVersion` is the Playnite SDK version the add-on is built against (`PlayniteSDK` in `src/PlayNext.csproj`, currently 6.11.0).
