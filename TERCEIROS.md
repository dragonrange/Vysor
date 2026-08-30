# Componentes de terceiros

O Vysor é distribuído sob a licença MIT (ver `LICENSE`). Os componentes abaixo
vêm junto ou são usados por ele, e têm licenças próprias.

Esta nota mora aqui, e não dentro do `LICENSE`, por um motivo prático: o GitHub
identifica a licença de um projeto comparando o arquivo com os textos
conhecidos. Qualquer parágrafo a mais faz a comparação falhar, e o projeto passa
a aparecer como "sem licença" — que é justamente o que impede a inscrição em
programas de assinatura digital para código aberto.

## ffmpeg

Distribuído junto com o Vysor como `ffmpeg.exe` e executado como **processo
separado** (o Vysor não o incorpora nem faz ligação de código com ele). É usado
para codificar e decodificar vídeo com aceleração por placa de vídeo.

- Site e código-fonte: https://ffmpeg.org
- Licença: LGPL 2.1+ ou GPL 2+, conforme a compilação
- Compilação usada nas publicações automáticas:
  https://github.com/BtbN/FFmpeg-Builds

## Bibliotecas usadas pelo aplicativo

Obtidas via NuGet, cada uma sob a própria licença:

| Componente | Para quê |
|---|---|
| Microsoft.AspNetCore.SignalR.Client | conversa com o servidor de salas |
| System.Drawing.Common | captura e tratamento de imagem |
| NAudio | captura e reprodução de áudio |
