# TESL Offline — código-fonte

[English](README.md)

Coloque `mod-tels-src/` **ao lado da pasta do jogo, nunca dentro dela**:

```text
steamapps/common/
├── The Elder Scrolls Legends/
└── mod-tels-src/
```

## Linux (Arch Linux)

Instale as dependências:

```sh
sudo pacman -S --needed mono dotnet-sdk
```

Dentro de `mod-tels-src/`, compile e instale:

```sh
./build.sh
./install.sh
```

Para desinstalar: `./uninstall.sh`.

## Windows (PowerShell)

A compilação usa Linux. No Windows, instale o WSL com Ubuntu **uma única vez** (PowerShell como administrador):

```powershell
wsl --install -d Ubuntu-24.04
```

Após a configuração inicial do Ubuntu, instale nele as dependências:

```sh
sudo apt update
sudo apt install -y mono-devel dotnet-sdk-8.0
```

Abra o **PowerShell dentro de `mod-tels-src/`** e execute:

```powershell
wsl sh ./build.sh
.\install.ps1
```

Para desinstalar, na mesma pasta: `.\uninstall.ps1`.

Feche o jogo antes de instalar ou desinstalar. A compilação também gera a pasta irmã `mod-tels/` para distribuição, mas **não é necessário entrar nela**.

**Licença:** código próprio sob GPL-3.0-or-later (`LICENSE`); dependências em `vendor-src/` mantêm suas licenças. Binários e dados derivados do jogo podem exigir autorização separada para redistribuição.
