# ⚡ Vysor

Compartilhamento de tela entre amigos, com vídeo e áudio indo **direto de um
computador para o outro** — sem passar por servidor nenhum.

[![Licença: MIT](https://img.shields.io/badge/licença-MIT-blue.svg)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D6.svg)](https://github.com/dragonrange/Vysor/releases/latest)
[![Baixar](https://img.shields.io/github/v/release/dragonrange/Vysor?label=última%20versão)](https://github.com/dragonrange/Vysor/releases/latest)

## O que ele faz

- **Tela e áudio direto entre os PCs.** Conexão ponto a ponto por UDP, com
  furo de NAT. O servidor só apresenta um computador ao outro; ele não tem
  como transportar mídia, e isso é estrutural, não uma configuração.
- **Criptografado de ponta a ponta.** AES-256-GCM, com a chave derivada do
  código da sala. Quem não foi convidado não tem como montar a imagem.
- **Codificação por placa de vídeo.** H.264 via NVENC, Quick Sync ou AMF,
  com queda para JPEG onde não houver GPU compatível.
- **Vários ao mesmo tempo.** Dá pra assistir a mais de uma pessoa, com volume
  separado por participante e o arranjo das telinhas calculado para aproveitar
  a área disponível.
- **Convites clicáveis.** Um link abre o app já dentro da sala.
- **Integração com Discord.** Um bot avisa no canal do grupo quando alguém abre
  uma sala.

## Instalação

Baixe em [Releases](https://github.com/dragonrange/Vysor/releases/latest):

- **`VysorSetup.exe`** — instalador. Configura a regra de firewall e o link
  `vysor://` automaticamente.
- **`VysorPortatil.zip`** — não precisa instalar. Serve para quem é bloqueado
  pelo Smart App Control do Windows 11 (erro 4551), que recusa executáveis sem
  assinatura digital.

## Como funciona por dentro

```
        ┌──────────┐   códigos de sala, nomes e endereços    ┌──────────┐
        │  Vysor   │◄──────────  (poucos KB) ──────────────►│ Servidor │
        │   (A)    │                                         └──────────┘
        └────┬─────┘
             │   vídeo e áudio, criptografados
             │   ── nunca passam pelo servidor ──
        ┌────▼─────┐
        │  Vysor   │
        │   (B)    │
        └──────────┘
```

O servidor existe por um motivo só: dois computadores atrás de roteadores
domésticos não conseguem se achar sozinhos. Ele apresenta um ao outro e sai
do caminho.

## Estrutura

| Pasta | O que é |
|---|---|
| `VysorClient/` | o aplicativo (WPF, .NET 8) |
| `VysorServer/` | servidor de sinalização (ASP.NET Core + SignalR) |
| `VysorLinux/` | porte para Linux, ainda não testado |

## Compilar

O build oficial roda no GitHub Actions ([build.yml](.github/workflows/build.yml)):
publicar uma tag `vX.Y.Z` gera o instalador e a versão portátil sozinho.

Para compilar na mão, veja [GERAR_EXE.md](GERAR_EXE.md).

## Privacidade e licença

- [Política de privacidade](PRIVACIDADE.md) — resumo: nada é gravado em disco,
  não há contas nem rastreadores, e a mídia não passa pelo servidor.
- [Licença MIT](LICENSE)
- [Componentes de terceiros](TERCEIROS.md)
