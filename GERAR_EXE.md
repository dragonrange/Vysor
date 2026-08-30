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

**B) Um instalador de verdade (recomendado a partir de agora)**

Existe um instalador pronto em `VysorClient/installer/Vysor.iss`, feito com o
**Inno Setup** (gratuito: https://jrsoftware.org/isinfo.php). Ele não é só
"mais profissional" — ele resolve um problema de verdade: agora que o
vídeo/áudio vão sempre DIRETO entre os PCs (P2P por UDP, nunca mais pelo
servidor), o Windows Firewall bloqueia por padrão a entrada desse tráfego, e
cada pessoa que só copiasse o `.exe` "cru" (opção A) precisaria liberar isso
na mão — foi exatamente isso que fez um teste real não mostrar vídeo nenhum.

O instalador resolve isso configurando a regra de firewall automaticamente
durante a instalação (que já pede permissão de administrador de qualquer
jeito). Ninguém precisa mexer em Firewall na mão nunca mais.

Pra gerar:

```powershell
cd VysorClient\installer
& "C:\Users\<seu usuário>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" Vysor.iss
```

(Se o Inno Setup estiver instalado em outro lugar, ou aparecer no Menu
Iniciar, você também pode simplesmente abrir `Vysor.iss` nele e clicar em
"Compilar".)

O instalador final sai em `VysorClient/installer/Output/VysorSetup.exe` —
é ESSE arquivo que você manda pros seus amigos (não precisa zipar nada).
Ele já pega o `Vysor.exe`, o `ffmpeg.exe` e o `server.txt` (se existirem)
de dentro da pasta `publish`, então gere o instalador DEPOIS do Passo 2
(e do 2.5, se for incluir o ffmpeg).

## Passo 5 — Publicar uma ATUALIZAÇÃO (pra quem já instalou)

Quem já tem o Vysor instalado não precisa baixar nada na mão de novo: o
app confere sozinho, ao abrir, se existe uma versão mais nova no GitHub
Releases (ver `VysorClient/Services/UpdateChecker.cs`) e mostra um botão
amarelo "⬆ Atualização disponível" no canto superior esquerdo. Clicando,
ele baixa o instalador novo e abre — o instalador já sabe fechar o Vysor
que está rodando (`CloseApplications=yes` no `Vysor.iss`) e substitui tudo.

Pra isso funcionar, TODA atualização precisa seguir esta receita:

1. Suba o número de versão em **dois lugares**, com o MESMO valor:
   - `VysorClient/VysorClient.csproj` → `<Version>1.1.0</Version>`
   - `VysorClient/installer/Vysor.iss` → `#define MyAppVersion "1.1.0"`
2. Gere o `.exe` (Passo 2, e 2.5 se usar ffmpeg) e o instalador (Passo 4,
   opção B) do jeito de sempre.
3. No GitHub (repositório `dragonrange/Vysor`, já público):
   **Releases → Draft a new release**.
   - **Tag**: `v1.1.0` — TEM que começar com "v" e bater com o número do
     passo 1 (é isso que o app compara pra saber se é mais novo).
   - **Anexe** (arraste ou "Attach binaries") o arquivo
     `VysorClient/installer/Output/VysorSetup.exe` — o nome do arquivo
     TEM que continuar sendo exatamente `VysorSetup.exe` (é esse nome que
     `UpdateChecker.cs` procura nos assets da release).
   - Clique **Publish release**.
4. Pronto. Na próxima vez que alguém abrir o Vysor (a checagem só acontece
   ao abrir o app, não fica confirmando toda hora), o botão de atualização
   aparece sozinho.

Se você publicar uma release SEM anexar o `VysorSetup.exe` (ou esquecer o
"v" na tag), o app simplesmente não mostra nada — ele falha em silêncio de
propósito, pra uma checagem de atualização nunca poder quebrar a abertura
do app.

## Resumo do fluxo completo

1. `fly launch` (uma vez) → pega o endereço do servidor novo.
2. Coloca esse endereço no `server.txt` (ou no `DefaultServerUrl` do
   código, antes de publicar).
3. `dotnet publish ...` (comando do Passo 2) → gera o `Vysor.exe`.
4. Zipa a pasta `publish` e manda pros amigos.
5. Quando trocar de servidor de novo no futuro, só atualiza o `server.txt`
   — não precisa repetir o Passo 3 nem 4.
