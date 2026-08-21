# Gerar um .exe pra mandar pros seus amigos testarem

Depois que o servidor estiver no ar no Fly.io (veja `DEPLOY_FLYIO.md`), você
precisa gerar um `Vysor.exe` que roda no PC dos seus amigos **sem precisar
instalar nada** (nem o .NET, nem o Visual Studio) — é só isso que você manda
pra eles.

## Passo 1 — Abrir o terminal na pasta certa

1. Abra a pasta `VysorApp\VysorClient` no Explorer (dentro de onde você
   extraiu o `Vysor.zip`).
2. Clica na barra de endereço, digita `cmd` e aperta Enter — abre o
   terminal já no lugar certo.

## Passo 2 — Gerar o .exe

Cola este comando (é um só, mesmo que quebre em várias linhas na tela):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

O que cada pedaço faz, rapidinho:
- `--self-contained true` → embute o próprio .NET dentro do `.exe`, então
  quem for abrir não precisa instalar nada por conta própria.
- `-p:PublishSingleFile=true` → junta tudo num `.exe` só, em vez de uma
  pasta cheia de `.dll`.
- `-o publish` → coloca o resultado numa pasta chamada `publish` dentro de
  `VysorClient`.

Isso demora um pouco (baixa e empacota o runtime inteiro do .NET). Quando
terminar, dentro de `VysorClient\publish` vai ter um arquivo `Vysor.exe`
(**bem maior que um `.exe` normal, uns 150-170 MB** — é normal, é porque o
.NET inteiro está embutido nele).

## Passo 2.5 — Adicionar o ffmpeg.exe (pra transmissão usar a GPU)

Desde a versão com codificação de vídeo por hardware, o Vysor tenta usar a
GPU (NVENC/Quick Sync/AMF) pra transmitir com bem menos uso de CPU e menos
banda — mas isso depende de um `ffmpeg.exe` estar do lado do `Vysor.exe`. Se
ele não estiver lá, o app simplesmente cai pro pipeline antigo (JPEG) sem
travar nem dar erro — só não ganha a aceleração por GPU. Veja
`VIDEO_GPU_NOTES.md` pra entender os detalhes.

1. Baixe o build **LGPL** do ffmpeg pra Windows 64-bit em:
   https://github.com/BtbN/FFmpeg-Builds/releases/latest
   Nessa página tem vários arquivos `.zip` — procure exatamente por
   **`ffmpeg-master-latest-win64-lgpl.zip`** (sem "-shared" no nome; a
   versão "-shared" também funciona, mas vem com várias `.dll` extras pra
   copiar junto, então a sem "-shared" é mais simples: só um `ffmpeg.exe`).

   > Não use o site gyan.dev pra isso — lá os builds "release" prontos pra
   > baixar são todos licenciados como GPL (o "LGPL" que aparece na página
   > deles é só um pacote de ferramentas à parte, não o ffmpeg completo). O
   > link do BtbN acima já separa claramente os arquivos `gpl` dos `lgpl`
   > pelo nome.

2. Extraia o zip baixado e ache o `ffmpeg.exe` dentro da pasta `bin`.
3. Copie esse `ffmpeg.exe` pra dentro da pasta `VysorClient\publish` (a
   mesma pasta onde o `Vysor.exe` vai ficar depois do Passo 2), do lado do
   `Vysor.exe` — mesma ideia do `server.txt`.

Isso deixa o zip final maior (o `ffmpeg.exe` sozinho tem uns 80-100MB), então
o total que você manda pros amigos sobe de ~150-170MB pra provavelmente
~230-270MB. Se preferir não incluir, o app funciona igual, só sem o ganho de
performance da GPU.

## Passo 3 — Testar antes de mandar

1. Dá duplo clique no `Vysor.exe` dentro de `publish`. Ele deve abrir
   normalmente, do jeito que abriu quando você testou pelo Visual Studio.
2. Se você já trocou de servidor Fly (veja `DEPLOY_FLYIO.md`, Passo 5),
   confere se o endereço novo está certo — ou coloca o `server.txt` do lado
   do `.exe` dentro dessa mesma pasta `publish`.

## Passo 4 — Mandar pros seus amigos

Duas opções:

**A) Mais simples — zipar a pasta inteira**

Clica com o botão direito na pasta `publish` → "Enviar para" → "Pasta
compactada" (ou qualquer programa de zip que você já usa). Manda esse zip
pros seus amigos. Eles extraem e dão duplo clique no `Vysor.exe` — não
precisa instalar nada.

Se você estiver usando o `server.txt` (endereço do servidor separado do
`.exe`) e/ou o `ffmpeg.exe` (Passo 2.5, pra usar a GPU), garanta que os dois
estão DENTRO da pasta `publish` antes de zipar, pra ir tudo junto.

**B) Um instalador de verdade (opcional)**

Se quiser algo mais "profissional" — ícone na área de trabalho, aparece em
"Adicionar ou remover programas", etc — dá pra empacotar esse mesmo `.exe`
com o **Inno Setup** (gratuito: https://jrsoftware.org/isinfo.php). Se você
quiser, é só me pedir que eu gero o script (`.iss`) do instalador pra você
usar — mas para simplesmente "seus amigos testarem", a opção A já resolve
sem complicação extra.

## Resumo do fluxo completo

1. `fly launch` (uma vez) → pega o endereço do servidor novo.
2. Coloca esse endereço no `server.txt` (ou no `DefaultServerUrl` do
   código, antes de publicar).
3. `dotnet publish ...` (comando do Passo 2) → gera o `Vysor.exe`.
4. Zipa a pasta `publish` e manda pros amigos.
5. Quando trocar de servidor de novo no futuro, só atualiza o `server.txt`
   — não precisa repetir o Passo 3 nem 4.
