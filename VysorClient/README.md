# VysorClient

App desktop (Windows, WPF) do Vysor. Interface escura estilo Discord, com:

- **Lobby**: nome/apelido, criar sala ou entrar com código.
- **Sala**: lista de participantes à direita (cada um com um play que liga
  quando a pessoa está transmitindo), espaço de transmissões à esquerda/
  centro (com grid dinâmico para várias transmissões ao mesmo tempo, pin
  para focar uma delas, e controles de volume/mudo por transmissão),
  botão "Transmitir" flutuando no centro-baixo do espaço de transmissões.
- **Compartilhar tela**: modal com abas "Tela Inteira" / "Janelas",
  miniaturas reais de cada opção, qualidade (720p/1080p) e taxa de quadros
  (30/60fps), e opção de transmitir o áudio junto.

## Como funciona por baixo dos panos

- **Vídeo**: captura a tela (ou janela) via GDI, redimensiona e comprime em
  JPEG a cada frame, envia pro `VysorServer` via SignalR, que repassa pros
  outros participantes da sala. O servidor nunca decodifica a imagem — só
  repassa os bytes.
- **Áudio**: captura via WASAPI loopback (veja `AUDIO_NOTES.md` na raiz do
  projeto para o funcionamento detalhado — inclui a lógica de nunca
  transmitir o áudio do Discord), comprime em μ-law e envia pelo mesmo canal.
- **NAudio** é usado para captura/reprodução/mixagem de áudio.

## Rodando

Veja `COMO_TESTAR.md` na raiz do projeto para o passo a passo completo
(Visual Studio + `Vysor.sln`).
