# TESL Offline — versão pré-compilada

[English](README.md)

Não é necessário compilar.

Feche o jogo e coloque a pasta `mod-tels/` **ao lado da pasta do jogo, nunca dentro dela**:

```text
steamapps/common/
├── The Elder Scrolls Legends/
└── mod-tels/
```

## Linux

Abra um terminal na pasta `mod-tels/` e instale:

```sh
./install.sh
```

Para desinstalar:

```sh
./uninstall.sh
```

## Windows (PowerShell)

Abra o PowerShell na pasta `mod-tels/` e instale:

```powershell
.\install.ps1
```

Para desinstalar:

```powershell
.\uninstall.ps1
```

O instalador exige uma versão compatível do jogo e faz backup da DLL original. **A instalação no Windows ainda não foi validada.**

**Licença:** código próprio GPL-3.0-or-later (`LICENSE`). As dependências possuem licenças próprias em `licenses/`. Binários e dados derivados do jogo podem exigir autorização separada para redistribuição.
