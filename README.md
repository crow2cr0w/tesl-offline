# TESL Offline — source code

[Português (Brasil)](README.pt-BR.md)

Place `mod-tels-src/` **next to the game folder, never inside it**:

```text
steamapps/common/
├── The Elder Scrolls Legends/
└── mod-tels-src/
```

## Linux (Arch Linux)

Install the build dependencies:

```sh
sudo pacman -S --needed mono dotnet-sdk
```

From inside `mod-tels-src/`, build and install:

```sh
./build.sh
./install.sh
```

To uninstall: `./uninstall.sh`.

## Windows (PowerShell)

Building requires Linux. On Windows, install WSL with Ubuntu **once** (PowerShell as administrator):

```powershell
wsl --install -d Ubuntu-24.04
```

After the initial Ubuntu setup, install the build dependencies in Ubuntu:

```sh
sudo apt update
sudo apt install -y mono-devel dotnet-sdk-8.0
```

Open **PowerShell inside `mod-tels-src/`** and run:

```powershell
wsl sh ./build.sh
.\install.ps1
```

To uninstall from the same folder: `.\uninstall.ps1`.

Close the game before installing or uninstalling. Building also creates the sibling `mod-tels/` folder for distribution, but **you do not need to enter it**.

**License:** original mod code is GPL-3.0-or-later (`LICENSE`); third-party code in `vendor-src/` retains its own licenses. Game-derived binaries and data may require separate redistribution permission.
