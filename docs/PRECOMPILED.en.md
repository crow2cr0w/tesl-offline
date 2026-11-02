# TESL Offline — precompiled version

[Português (Brasil)](README.pt-BR.md)

No compilation required.

Close the game and place `mod-tels/` **next to the game folder, never inside it**:

```text
steamapps/common/
├── The Elder Scrolls Legends/
└── mod-tels/
```

## Linux

Open a terminal in `mod-tels/` and install:

```sh
./install.sh
```

To uninstall:

```sh
./uninstall.sh
```

## Windows (PowerShell)

Open PowerShell in `mod-tels/` and install:

```powershell
.\install.ps1
```

To uninstall:

```powershell
.\uninstall.ps1
```

The installer requires a compatible game version and backs up the original DLL. **Windows installation has not yet been validated.**

**License:** original code GPL-3.0-or-later (`LICENSE`). Third-party dependencies retain their licenses in `licenses/`. Game-derived binaries and data may require separate redistribution permission.
